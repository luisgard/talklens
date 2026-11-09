namespace TalkLens.Transcription;

/// <summary>
/// Keeps captions alive: cloud while it works, local model the moment it stops (RNF-06), back to cloud on reconnect.
/// A short audio ring is replayed into the local model so the words spoken during the outage are not lost.
/// </summary>
public sealed class TranscriptionManager(
    Func<ITranscriber>? cloud, Func<ITranscriber>? local, Func<bool> online, Func<long> now, long stallMs, ILogger log) : IAsyncDisposable
{
    private const long ReplayMs = 8000;
    // Longer than the connectivity probe takes to notice, so a dead link does not flap between engines.
    private const long CloudRetryMs = 10000;

    private readonly object _gate = new();
    private readonly Queue<AudioFrame> _ring = new();
    private readonly CancellationTokenSource _stop = new();

    private ITranscriber? _active;
    private ITranscriber? _retiring;
    private bool _activeIsCloud;
    private bool _cloudFailed;
    private long _nextCloudAttempt;
    private long? _answerDueSince;
    private Task? _loop;

    public event Action<TranscriptEvent, string>? Transcript;
    public event Action<string>? ModeChanged;

    /// <summary>cloud | local | none</summary>
    public string Mode { get; private set; } = "none";
    public string Engine => _active?.Name ?? "none";
    public string LastProblem { get; private set; } = "";

    public void Start() => _loop = Task.Run(async () =>
    {
        while (!_stop.IsCancellationRequested)
        {
            try { await TickAsync(); }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { log.LogError(e, "Transcription supervisor error"); }
            try { await Task.Delay(500, _stop.Token); } catch (OperationCanceledException) { return; }
        }
    });

    public void Feed(AudioFrame frame)
    {
        lock (_gate)
        {
            _ring.Enqueue(frame);
            while (_ring.Count > 0 && frame.TalkMs - _ring.Peek().TalkMs > ReplayMs) _ring.Dequeue();
            _active?.Feed(frame);
        }
    }

    /// <summary>One supervisor step. Public so tests can drive it deterministically.</summary>
    public async Task TickAsync()
    {
        if (_activeIsCloud)
        {
            string? problem = _cloudFailed ? LastProblem
                : !online() ? "internet down"
                : _answerDueSince is { } since && now() - since > stallMs ? "cloud stopped responding"
                : null;
            if (problem != null) await FallBackAsync(problem);
            return;
        }

        if (cloud != null && online() && now() >= _nextCloudAttempt && await TryStartCloudAsync()) return;
        if (_active == null && local != null) await StartLocalAsync(replay: false);
    }

    private async Task<bool> TryStartCloudAsync()
    {
        var engine = cloud!();
        try
        {
            Wire(engine, isCloud: true);
            await engine.StartAsync(_stop.Token);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LastProblem = e.Message;
            _nextCloudAttempt = now() + CloudRetryMs;
            log.LogWarning("Cloud transcription unavailable: {Message}", e.Message);
            await engine.DisposeAsync();
            return false;
        }

        ITranscriber? previous;
        lock (_gate)
        {
            previous = _active;
            _retiring = previous;
            _active = engine;
            _activeIsCloud = true;
            _cloudFailed = false;
            _answerDueSince = null;
        }
        SetMode("cloud");
        if (previous != null) await previous.DisposeAsync();
        _retiring = null;
        return true;
    }

    private async Task FallBackAsync(string reason)
    {
        log.LogWarning("Leaving cloud transcription: {Reason}", reason);
        LastProblem = reason;
        _nextCloudAttempt = now() + CloudRetryMs;
        ITranscriber? old;
        lock (_gate)
        {
            old = _active;
            _active = null;
            _activeIsCloud = false;
            _cloudFailed = false;
            _answerDueSince = null;
        }
        if (local != null) await StartLocalAsync(replay: true);
        else SetMode("none");
        if (old != null) await old.DisposeAsync();
    }

    private async Task StartLocalAsync(bool replay)
    {
        var engine = local!();
        try
        {
            Wire(engine, isCloud: false);
            await engine.StartAsync(_stop.Token);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LastProblem = e.Message;
            log.LogError("Local transcription unavailable: {Message}", e.Message);
            await engine.DisposeAsync();
            SetMode("none");
            return;
        }
        lock (_gate)
        {
            if (replay) foreach (var frame in _ring) engine.Feed(frame);
            _active = engine;
        }
        SetMode("local");
    }

    private void Wire(ITranscriber engine, bool isCloud)
    {
        engine.Transcript += e =>
        {
            if (!ReferenceEquals(engine, _active) && !ReferenceEquals(engine, _retiring)) return;
            if (ReferenceEquals(engine, _active)) _answerDueSince = null;
            Transcript?.Invoke(e, isCloud ? "cloud" : "local");
        };
        engine.AnswerDue += () =>
        {
            if (ReferenceEquals(engine, _active)) _answerDueSince ??= now();
        };
        engine.Answered += () =>
        {
            if (ReferenceEquals(engine, _active)) _answerDueSince = null;
        };
        engine.Failed += e =>
        {
            if (!ReferenceEquals(engine, _active)) return;
            LastProblem = e.Message;
            _cloudFailed = isCloud;
            if (!isCloud) log.LogError(e, "Local transcription failed");
        };
    }

    private void SetMode(string mode)
    {
        if (Mode == mode) return;
        Mode = mode;
        ModeChanged?.Invoke(mode);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop != null) await _loop;
        ITranscriber? active;
        lock (_gate)
        {
            active = _active;
            _retiring = active;
            _active = null;
        }
        if (active != null) await active.DisposeAsync();
    }
}
