using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace TalkLens.Transcription;

/// <summary>Whisper by blocks over Groq's REST API. Blocks close on the quietest moment near the target length.</summary>
public sealed class GroqTranscriber(HttpClient http, string baseUrl, string apiKey, string model, string language, double blockSeconds) : ITranscriber
{
    private const int MaxPromptChars = 200;
    private readonly Channel<(byte[] Wav, long Start, long End)> _blocks = Channel.CreateUnbounded<(byte[], long, long)>();
    private readonly List<AudioFrame> _pending = [];
    private Task? _loop;
    private string _context = "";
    private int _count;

    public string Name => $"groq/{model}";
    public event Action<TranscriptEvent>? Transcript;
    public event Action<Exception>? Failed;
    public event Action? AnswerDue;
    public event Action? Answered;

    public Task StartAsync(CancellationToken ct)
    {
        _loop = Task.Run(ProcessAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    public void Feed(AudioFrame frame)
    {
        _pending.Add(frame);
        var target = (int)(blockSeconds * 10);
        if (_pending.Count < target) return;

        // Cut at the quietest frame of the last few so a word is not split in two.
        var cut = _pending.Count;
        var quietest = double.MaxValue;
        for (var i = _pending.Count - 1; i >= Math.Max(0, _pending.Count - 4); i--)
        {
            var rms = _pending[i].Rms();
            if (rms < quietest) { quietest = rms; cut = i + 1; }
        }
        var block = _pending.Take(cut).ToList();
        _pending.RemoveRange(0, cut);
        if (!block.Any(f => f.HasVoice)) return;
        _blocks.Writer.TryWrite((BuildWav(block), block[0].TalkMs, block[^1].TalkMs + 100));
        AnswerDue?.Invoke();
    }

    private async Task ProcessAsync()
    {
        await foreach (var (wav, start, end) in _blocks.Reader.ReadAllAsync())
        {
            try
            {
                var text = await TranscribeAsync(wav);
                Answered?.Invoke();
                if (text.Length == 0) continue;
                var joined = _context + " " + text;
                _context = joined[Math.Max(0, joined.Length - MaxPromptChars)..];
                Transcript?.Invoke(new TranscriptEvent($"g{GetHashCode():x}-{_count++}", text, true, start, end));
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
            {
                Failed?.Invoke(e);
                return;
            }
        }
    }

    private async Task<string> TranscribeAsync(byte[] wav)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "block.wav");
        form.Add(new StringContent(model), "model");
        form.Add(new StringContent(language), "language");
        form.Add(new StringContent("json"), "response_format");
        if (_context.Length > 0) form.Add(new StringContent(_context), "prompt");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/audio/transcriptions") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await http.SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Groq returned {(int)response.StatusCode}");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        return doc.RootElement.GetProperty("text").GetString()?.Trim() ?? "";
    }

    public static byte[] BuildWav(IReadOnlyList<AudioFrame> frames)
    {
        var dataBytes = frames.Sum(f => f.Pcm.Length);
        using var stream = new MemoryStream(44 + dataBytes);
        using var w = new BinaryWriter(stream);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataBytes);
        w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(AudioFrame.SampleRate);
        w.Write(AudioFrame.SampleRate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataBytes);
        foreach (var frame in frames) w.Write(frame.Pcm);
        return stream.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        _blocks.Writer.TryComplete();
        if (_loop != null) await _loop;
    }
}
