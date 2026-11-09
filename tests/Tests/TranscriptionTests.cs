using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TalkLens.Transcription;
using Xunit;

namespace TalkLens.Tests;

public class TranscriptionManagerTests
{
    private sealed class FakeEngine(string name, bool failOnStart = false) : ITranscriber
    {
        public string Name => name;
        public List<AudioFrame> Fed { get; } = [];
        public bool Disposed { get; private set; }
        public event Action<TranscriptEvent>? Transcript;
        public event Action<Exception>? Failed;
        public event Action? AnswerDue;
        public event Action? Answered;

        public Task StartAsync(CancellationToken ct) =>
            failOnStart ? throw new InvalidOperationException("no connection") : Task.CompletedTask;

        public void Feed(AudioFrame frame) => Fed.Add(frame);
        public void Say(string text) => Transcript?.Invoke(new TranscriptEvent("k", text, true, 0, 0));
        public void EndTurn() => AnswerDue?.Invoke();
        public void AnswerWithNoWords() => Answered?.Invoke();
        public void Fail() => Failed?.Invoke(new InvalidOperationException("socket closed"));

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Rig
    {
        public bool Online = true;
        public long Now;
        public readonly List<FakeEngine> Clouds = [];
        public readonly List<FakeEngine> Locals = [];
        public readonly List<(string Text, string Source)> Heard = [];
        public bool CloudRefuses;
        public TranscriptionManager Manager { get; }

        public Rig(bool hasLocal = true, bool hasCloud = true)
        {
            Manager = new TranscriptionManager(
                hasCloud ? () => Add(Clouds, new FakeEngine("cloud", CloudRefuses)) : null,
                hasLocal ? () => Add(Locals, new FakeEngine("local")) : null,
                () => Online, () => Now, 6000, NullLogger.Instance);
            Manager.Transcript += (e, source) => Heard.Add((e.Text, source));
        }

        private static FakeEngine Add(List<FakeEngine> list, FakeEngine engine)
        {
            list.Add(engine);
            return engine;
        }
    }

    private static AudioFrame Frame(long t, bool voice = true)
    {
        var pcm = new byte[AudioFrame.Bytes];
        if (voice)
            for (var i = 0; i < pcm.Length; i += 2) BitConverter.TryWriteBytes(pcm.AsSpan(i), (short)(i % 8 == 0 ? 8000 : -8000));
        return new AudioFrame(pcm, t);
    }

    [Fact]
    public async Task Uses_cloud_when_online()
    {
        var rig = new Rig();
        await rig.Manager.TickAsync();
        Assert.Equal("cloud", rig.Manager.Mode);
        Assert.Empty(rig.Locals);
    }

    [Fact]
    public async Task Without_internet_it_starts_on_the_local_model()
    {
        var rig = new Rig { Online = false };
        await rig.Manager.TickAsync();
        Assert.Equal("local", rig.Manager.Mode);
    }

    [Fact]
    public async Task Cloud_refusing_to_start_falls_through_to_local()
    {
        var rig = new Rig { CloudRefuses = true };
        await rig.Manager.TickAsync();
        Assert.Equal("local", rig.Manager.Mode);
        Assert.True(rig.Clouds[0].Disposed);
    }

    [Fact]
    public async Task Internet_loss_switches_to_local_and_replays_recent_audio()
    {
        var rig = new Rig();
        await rig.Manager.TickAsync();
        for (long t = 0; t < 3000; t += 100) rig.Manager.Feed(Frame(t));

        rig.Online = false;
        await rig.Manager.TickAsync();

        Assert.Equal("local", rig.Manager.Mode);
        Assert.Equal(30, rig.Locals[0].Fed.Count);
        Assert.True(rig.Clouds[0].Disposed);
    }

    [Fact]
    public async Task Replay_keeps_only_the_last_few_seconds()
    {
        var rig = new Rig();
        await rig.Manager.TickAsync();
        for (long t = 0; t < 60000; t += 100) rig.Manager.Feed(Frame(t));
        rig.Online = false;
        await rig.Manager.TickAsync();
        Assert.InRange(rig.Locals[0].Fed.Count, 70, 90);
    }

    [Fact]
    public async Task Cloud_error_switches_to_local()
    {
        var rig = new Rig();
        await rig.Manager.TickAsync();
        rig.Clouds[0].Fail();
        await rig.Manager.TickAsync();
        Assert.Equal("local", rig.Manager.Mode);
        Assert.Equal("socket closed", rig.Manager.LastProblem);
    }

    [Fact]
    public async Task Cloud_that_never_answers_a_finished_turn_counts_as_failed()
    {
        var rig = new Rig();
        await rig.Manager.TickAsync();
        rig.Clouds[0].EndTurn();
        rig.Now = 5000;
        await rig.Manager.TickAsync();
        Assert.Equal("cloud", rig.Manager.Mode);
        rig.Now = 7000;
        await rig.Manager.TickAsync();
        Assert.Equal("local", rig.Manager.Mode);
    }

    [Fact]
    public async Task Noise_the_cloud_hears_no_words_in_is_not_a_stall()
    {
        var rig = new Rig();
        await rig.Manager.TickAsync();
        rig.Manager.Feed(Frame(0));
        rig.Clouds[0].EndTurn();
        rig.Clouds[0].AnswerWithNoWords();
        rig.Now = 60000;
        await rig.Manager.TickAsync();
        Assert.Equal("cloud", rig.Manager.Mode);
    }

    [Fact]
    public async Task A_long_turn_still_in_progress_is_not_a_stall()
    {
        var rig = new Rig();
        await rig.Manager.TickAsync();
        for (long t = 0; t < 9000; t += 100) rig.Manager.Feed(Frame(t));
        rig.Now = 9000;
        await rig.Manager.TickAsync();
        Assert.Equal("cloud", rig.Manager.Mode);
    }

    [Fact]
    public async Task Silence_never_counts_as_a_stall()
    {
        var rig = new Rig();
        await rig.Manager.TickAsync();
        for (long t = 0; t < 60000; t += 100) rig.Manager.Feed(Frame(t, voice: false));
        rig.Now = 60000;
        await rig.Manager.TickAsync();
        Assert.Equal("cloud", rig.Manager.Mode);
    }

    [Fact]
    public async Task Returns_to_cloud_after_reconnecting()
    {
        var rig = new Rig();
        await rig.Manager.TickAsync();
        rig.Online = false;
        await rig.Manager.TickAsync();
        Assert.Equal("local", rig.Manager.Mode);

        rig.Online = true;
        rig.Now = 2000;
        await rig.Manager.TickAsync();
        Assert.Equal("local", rig.Manager.Mode);

        rig.Now = 11000;
        await rig.Manager.TickAsync();
        Assert.Equal("cloud", rig.Manager.Mode);
        Assert.Equal(2, rig.Clouds.Count);
        Assert.True(rig.Locals[0].Disposed);
    }

    [Fact]
    public async Task Late_text_from_a_replaced_engine_is_dropped()
    {
        var rig = new Rig();
        await rig.Manager.TickAsync();
        rig.Online = false;
        await rig.Manager.TickAsync();
        rig.Clouds[0].Say("stale");
        rig.Locals[0].Say("fresh");
        Assert.Equal([("fresh", "local")], rig.Heard);
    }

    [Fact]
    public async Task No_engines_means_no_captions_and_no_crash()
    {
        var rig = new Rig(hasLocal: false, hasCloud: false);
        await rig.Manager.TickAsync();
        rig.Manager.Feed(Frame(0));
        Assert.Equal("none", rig.Manager.Mode);
    }
}

public class GroqTranscriberTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(respond(request));
        }
    }

    private static AudioFrame Voice(long t)
    {
        var pcm = new byte[AudioFrame.Bytes];
        for (var i = 0; i < pcm.Length; i += 2) BitConverter.TryWriteBytes(pcm.AsSpan(i), (short)8000);
        return new AudioFrame(pcm, t);
    }

    [Fact]
    public void Wav_header_describes_16k_mono_pcm()
    {
        var wav = GroqTranscriber.BuildWav([Voice(0)]);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(16000, BitConverter.ToInt32(wav, 24));
        Assert.Equal(1, BitConverter.ToInt16(wav, 22));
        Assert.Equal(AudioFrame.Bytes, BitConverter.ToInt32(wav, 40));
        Assert.Equal(44 + AudioFrame.Bytes, wav.Length);
    }

    [Fact]
    public async Task Silent_blocks_are_never_sent_or_billed()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"text":"hola"}""") });
        await using var engine = new GroqTranscriber(new HttpClient(handler), "http://stub", "k", "m", "es", 1);
        await engine.StartAsync(default);
        for (long t = 0; t < 3000; t += 100) engine.Feed(new AudioFrame(new byte[AudioFrame.Bytes], t));
        await engine.DisposeAsync();
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Speech_becomes_final_captions_with_block_times()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"text":" hola mundo "}""") });
        var engine = new GroqTranscriber(new HttpClient(handler), "http://stub", "k", "m", "es", 1);
        var heard = new List<TranscriptEvent>();
        engine.Transcript += heard.Add;
        await engine.StartAsync(default);
        for (long t = 0; t < 1000; t += 100) engine.Feed(Voice(t));
        await engine.DisposeAsync();

        var caption = Assert.Single(heard);
        Assert.True(caption.Final);
        Assert.Equal("hola mundo", caption.Text);
        Assert.Equal(0, caption.StartMs);
    }

    [Fact]
    public async Task An_empty_answer_counts_as_an_answer_but_not_as_a_caption()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"text":""}""") });
        var engine = new GroqTranscriber(new HttpClient(handler), "http://stub", "k", "m", "es", 1);
        var heard = new List<TranscriptEvent>();
        var answers = 0;
        engine.Transcript += heard.Add;
        engine.Answered += () => answers++;
        await engine.StartAsync(default);
        for (long t = 0; t < 1000; t += 100) engine.Feed(Voice(t));
        await engine.DisposeAsync();

        Assert.Empty(heard);
        Assert.Equal(1, answers);
    }

    [Fact]
    public async Task Server_error_reports_failure_so_the_manager_can_fall_back()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var engine = new GroqTranscriber(new HttpClient(handler), "http://stub", "k", "m", "es", 1);
        Exception? failure = null;
        engine.Failed += e => failure = e;
        await engine.StartAsync(default);
        for (long t = 0; t < 1000; t += 100) engine.Feed(Voice(t));
        await engine.DisposeAsync();
        Assert.IsType<HttpRequestException>(failure);
    }
}

public class OpenAiRealtimeTests
{
    private static AudioFrame Frame(long t, bool voice)
    {
        var pcm = new byte[AudioFrame.Bytes];
        if (voice)
            for (var i = 0; i < pcm.Length; i += 2) BitConverter.TryWriteBytes(pcm.AsSpan(i), (short)8000);
        return new AudioFrame(pcm, t);
    }

    [Fact]
    public void Upsampling_produces_24k_from_16k()
    {
        var output = OpenAiRealtimeTranscriber.To24k(new byte[AudioFrame.Bytes]);
        Assert.Equal(AudioFrame.Bytes * 3 / 2, output.Length);
    }

    /// <summary>Plays the documented server side of a transcription session.</summary>
    [Fact]
    public async Task Speaks_the_documented_protocol()
    {
        var port = Random.Shared.Next(20000, 40000);
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var received = new List<JsonElement>();
        string? authorization = null;
        var commitSeen = new TaskCompletionSource();
        var serverDone = new TaskCompletionSource();

        _ = Task.Run(async () =>
        {
          try
          {
            var context = await listener.GetContextAsync();
            authorization = context.Request.Headers["Authorization"];
            var ws = (await context.AcceptWebSocketAsync(null)).WebSocket;
            var buffer = new byte[1 << 20];
            while (ws.State == WebSocketState.Open)
            {
                var length = 0;
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), default);
                    length += result.Count;
                } while (!result.EndOfMessage);
                if (result.MessageType == WebSocketMessageType.Close) break;
                var message = JsonDocument.Parse(buffer.AsMemory(0, length)).RootElement.Clone();
                lock (received) received.Add(message);
                if (message.GetProperty("type").GetString() != "input_audio_buffer.commit") continue;

                foreach (var reply in new[]
                {
                    """{"type":"input_audio_buffer.committed","item_id":"item1"}""",
                    """{"type":"conversation.item.input_audio_transcription.delta","item_id":"item1","content_index":0,"delta":"hola "}""",
                    """{"type":"conversation.item.input_audio_transcription.delta","item_id":"item1","content_index":0,"delta":"mundo"}""",
                    """{"type":"conversation.item.input_audio_transcription.completed","item_id":"item1","content_index":0,"transcript":"Hola mundo."}""",
                })
                    await ws.SendAsync(Encoding.UTF8.GetBytes(reply), WebSocketMessageType.Text, true, default);
                commitSeen.TrySetResult();
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", default);
                break;
            }
            serverDone.TrySetResult();
          }
          catch (Exception e)
          {
              serverDone.TrySetException(e);
          }
        });

        var engine = new OpenAiRealtimeTranscriber($"ws://127.0.0.1:{port}/", "secret", "gpt-live-transcribe", "es");
        var heard = new List<TranscriptEvent>();
        var finished = new TaskCompletionSource();
        Exception? failure = null;
        engine.Transcript += e =>
        {
            lock (heard) heard.Add(e);
            if (e.Final) finished.TrySetResult();
        };
        engine.Failed += e => failure = e;
        var answersDue = 0;
        var answers = 0;
        engine.AnswerDue += () => answersDue++;
        engine.Answered += () => answers++;
        await engine.StartAsync(default);

        // 1 s of speech, then a pause long enough to end the turn.
        for (long t = 1000; t < 2000; t += 100) engine.Feed(Frame(t, true));
        for (long t = 2000; t < 3000; t += 100) engine.Feed(Frame(t, false));

        await serverDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 50 && failure == null; i++) await Task.Delay(20);
        await engine.DisposeAsync();

        Assert.Equal("Bearer secret", authorization);

        var session = received[0];
        Assert.Equal("session.update", session.GetProperty("type").GetString());
        var input = session.GetProperty("session").GetProperty("audio").GetProperty("input");
        Assert.Equal("transcription", session.GetProperty("session").GetProperty("type").GetString());
        Assert.Equal("audio/pcm", input.GetProperty("format").GetProperty("type").GetString());
        Assert.Equal(24000, input.GetProperty("format").GetProperty("rate").GetInt32());
        Assert.Equal("gpt-live-transcribe", input.GetProperty("transcription").GetProperty("model").GetString());
        Assert.Equal(JsonValueKind.Null, input.GetProperty("turn_detection").ValueKind);

        Assert.Contains(received, m => m.GetProperty("type").GetString() == "input_audio_buffer.append");

        Assert.Equal(["hola", "hola mundo", "Hola mundo."], heard.Select(h => h.Text));
        Assert.True(heard[^1].Final);
        Assert.Equal(1000, heard[^1].StartMs);
        Assert.Equal(2700, heard[^1].EndMs);
        Assert.Equal(1, answersDue);
        Assert.Equal(1, answers);

        // The server hung up: the manager must hear about it.
        Assert.NotNull(failure);
    }
}
