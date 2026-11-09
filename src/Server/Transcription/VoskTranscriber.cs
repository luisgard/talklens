using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using TalkLens.Core;
using Vosk;

namespace TalkLens.Transcription;

/// <summary>Plan B: small CPU model that needs no internet (RNF-06).</summary>
public sealed class VoskTranscriber(string language) : ITranscriber
{
    private const long MaxUtteranceMs = 12000;
    private static readonly ConcurrentDictionary<string, Lazy<Model>> Models = new();

    private readonly Channel<AudioFrame> _frames = Channel.CreateBounded<AudioFrame>(
        new BoundedChannelOptions(600) { FullMode = BoundedChannelFullMode.DropOldest });
    private VoskRecognizer? _recognizer;
    private Task? _loop;
    private int _utterance;
    private long? _utteranceStart;
    private string _lastPartial = "";

    public string Name => $"vosk-{language}";
    public event Action<TranscriptEvent>? Transcript;
    public event Action<Exception>? Failed;

    public static string? ModelPath(string language)
    {
        var models = Paths.Models;
        return !Directory.Exists(models) ? null
            : Directory.GetDirectories(models, $"vosk-model-small-{language}*").FirstOrDefault();
    }

    /// <summary>Loads the model ahead of time so falling back is instant.</summary>
    public static void Preload(string language)
    {
        Vosk.Vosk.SetLogLevel(-1);
        if (ModelPath(language) != null) _ = LoadModel(language);
    }

    public Task StartAsync(CancellationToken ct)
    {
        return Task.Run(() =>
        {
            Vosk.Vosk.SetLogLevel(-1);
            _recognizer = new VoskRecognizer(LoadModel(language), AudioFrame.SampleRate);
            _loop = Task.Run(ProcessAsync, CancellationToken.None);
        }, ct);
    }

    public void Feed(AudioFrame frame) => _frames.Writer.TryWrite(frame);

    private static Model LoadModel(string language)
    {
        var path = ModelPath(language) ?? throw new InvalidOperationException($"No local model for '{language}' in models/");
        return Models.GetOrAdd(language, _ => new Lazy<Model>(() => new Model(path))).Value;
    }

    private async Task ProcessAsync()
    {
        long lastT = 0;
        try
        {
            await foreach (var frame in _frames.Reader.ReadAllAsync())
            {
                lastT = frame.TalkMs;
                if (_recognizer!.AcceptWaveform(frame.Pcm, frame.Pcm.Length))
                {
                    EmitFinal(_recognizer.Result(), lastT);
                    continue;
                }
                var partial = ReadText(_recognizer.PartialResult(), "partial");
                if (partial.Length == 0) continue;
                _utteranceStart ??= frame.TalkMs - 500;
                if (partial != _lastPartial)
                {
                    _lastPartial = partial;
                    Transcript?.Invoke(new TranscriptEvent(Key, partial, false, _utteranceStart.Value, frame.TalkMs));
                }
                if (frame.TalkMs - _utteranceStart > MaxUtteranceMs) EmitFinal(_recognizer.FinalResult(), lastT);
            }
            EmitFinal(_recognizer!.FinalResult(), lastT);
        }
        catch (Exception e)
        {
            Failed?.Invoke(e);
        }
    }

    private string Key => $"l{GetHashCode():x}-{_utterance}";

    private void EmitFinal(string json, long t)
    {
        var text = ReadText(json, "text");
        if (text.Length > 0)
            Transcript?.Invoke(new TranscriptEvent(Key, text, true, _utteranceStart ?? t, t));
        _utterance++;
        _utteranceStart = null;
        _lastPartial = "";
    }

    private static string ReadText(string json, string property)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";
    }

    public async ValueTask DisposeAsync()
    {
        _frames.Writer.TryComplete();
        if (_loop != null) await _loop;
        _recognizer?.Dispose();
    }
}
