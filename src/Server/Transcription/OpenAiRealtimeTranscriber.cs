using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace TalkLens.Transcription;

/// <summary>
/// OpenAI realtime transcription over WebSocket. gpt-live-transcribe has no server VAD, so the client
/// ends a turn itself on a pause, or after MaxTurnMs of continuous speech.
/// </summary>
public sealed class OpenAiRealtimeTranscriber(string url, string apiKey, string model, string language) : ITranscriber
{
    // Shorter pauses cut people mid-sentence, and the model then transcribes each piece without the rest.
    private const long PauseMs = 800;
    private const long MaxTurnMs = 10000;

    private readonly ClientWebSocket _ws = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<string> _outgoing = Channel.CreateBounded<string>(
        new BoundedChannelOptions(300) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Dictionary<string, Item> _items = [];
    private readonly Queue<(long Start, long End)> _closedTurns = new();
    private readonly object _gate = new();
    private Task? _send;
    private Task? _receive;
    private long? _turnStart;
    private long _lastVoice;
    private long _lastFrameT;

    private sealed class Item(long start)
    {
        public long Start { get; } = start;
        public long? End { get; set; }
        public StringBuilder Text { get; } = new();
    }

    public string Name => $"openai/{model}";
    public event Action<TranscriptEvent>? Transcript;
    public event Action<Exception>? Failed;
    public event Action? AnswerDue;
    public event Action? Answered;

    public async Task StartAsync(CancellationToken ct)
    {
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {apiKey}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        await _ws.ConnectAsync(new Uri(url), timeout.Token);

        var session = JsonSerializer.Serialize(new
        {
            type = "session.update",
            session = new
            {
                type = "transcription",
                audio = new
                {
                    input = new
                    {
                        format = new { type = "audio/pcm", rate = 24000 },
                        transcription = new { model, languages = new[] { language }, delay = "low" },
                        turn_detection = (object?)null,
                    },
                },
            },
        });
        await SendTextAsync(session, timeout.Token);
        _send = Task.Run(SendLoopAsync, CancellationToken.None);
        _receive = Task.Run(ReceiveLoopAsync, CancellationToken.None);
    }

    public void Feed(AudioFrame frame)
    {
        _outgoing.Writer.TryWrite(
            $$"""{"type":"input_audio_buffer.append","audio":"{{Convert.ToBase64String(To24k(frame.Pcm))}}"}""");
        _lastFrameT = frame.TalkMs;

        lock (_gate)
        {
            if (frame.HasVoice)
            {
                _turnStart ??= frame.TalkMs;
                _lastVoice = frame.TalkMs;
            }
            if (_turnStart is not { } start) return;
            var paused = frame.TalkMs - _lastVoice >= PauseMs;
            if (!paused && frame.TalkMs - start < MaxTurnMs) return;
            _closedTurns.Enqueue((start, frame.TalkMs));
            _turnStart = null;
        }
        _outgoing.Writer.TryWrite("""{"type":"input_audio_buffer.commit"}""");
        AnswerDue?.Invoke();
    }

    /// <summary>Linear upsample from 16 kHz to the 24 kHz the API expects. Good enough for speech.</summary>
    public static byte[] To24k(byte[] pcm16k)
    {
        var samples = pcm16k.Length / 2;
        var output = new byte[samples * 3 / 2 * 2];
        for (var i = 0; i < output.Length / 2; i++)
        {
            var position = i * 2.0 / 3.0;
            var index = (int)position;
            var a = BitConverter.ToInt16(pcm16k, index * 2);
            var b = index + 1 < samples ? BitConverter.ToInt16(pcm16k, (index + 1) * 2) : a;
            var value = (short)(a + (b - a) * (position - index));
            output[i * 2] = (byte)value;
            output[i * 2 + 1] = (byte)(value >> 8);
        }
        return output;
    }

    private Task SendTextAsync(string text, CancellationToken ct) =>
        _ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var message in _outgoing.Reader.ReadAllAsync(_stop.Token))
                await SendTextAsync(message, _stop.Token);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException e) { Failed?.Invoke(e); }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var result = await _ws.ReceiveAsync(buffer, _stop.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new WebSocketException("OpenAI closed the realtime session");
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                Handle(message.ToArray());
                message.SetLength(0);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is WebSocketException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Failed?.Invoke(e);
        }
    }

    private void Handle(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        switch (root.GetProperty("type").GetString())
        {
            case "input_audio_buffer.committed":
                lock (_gate)
                {
                    var item = GetItem(root.GetProperty("item_id").GetString()!);
                    if (_closedTurns.TryDequeue(out var turn)) item.End = turn.End;
                }
                break;
            case "conversation.item.input_audio_transcription.delta":
            {
                var id = root.GetProperty("item_id").GetString()!;
                Item item;
                lock (_gate) item = GetItem(id);
                item.Text.Append(root.GetProperty("delta").GetString());
                Transcript?.Invoke(new TranscriptEvent(id, item.Text.ToString().Trim(), false, item.Start, item.End ?? _lastFrameT));
                break;
            }
            case "conversation.item.input_audio_transcription.completed":
            {
                var id = root.GetProperty("item_id").GetString()!;
                Item item;
                lock (_gate)
                {
                    item = GetItem(id);
                    _items.Remove(id);
                }
                var text = root.GetProperty("transcript").GetString()?.Trim() ?? "";
                Answered?.Invoke();
                if (text.Length > 0)
                    Transcript?.Invoke(new TranscriptEvent(id, text, true, item.Start, item.End ?? _lastFrameT));
                break;
            }
            case "conversation.item.input_audio_transcription.failed":
            case "error":
                var detail = root.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m) ? m.GetString() : "transcription failed";
                Failed?.Invoke(new InvalidOperationException(detail));
                break;
        }
    }

    private Item GetItem(string id)
    {
        if (_items.TryGetValue(id, out var item)) return item;
        var start = _closedTurns.TryPeek(out var closed) ? closed.Start : _turnStart ?? _lastFrameT;
        return _items[id] = new Item(start);
    }

    public async ValueTask DisposeAsync()
    {
        _outgoing.Writer.TryComplete();
        _stop.Cancel();
        if (_send != null) await _send;
        if (_receive != null) await _receive;
        _ws.Dispose();
        _stop.Dispose();
    }
}
