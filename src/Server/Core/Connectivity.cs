namespace TalkLens.Core;

/// <summary>Probes the AI provider so captions can drop to the local model when the hotspot dies (RNF-06).</summary>
public sealed class Connectivity(AppOptions options, ILogger<Connectivity> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2.5) };
    private int _failures;

    public bool Online { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var ok = await ProbeAsync(stop);
            _failures = ok ? 0 : _failures + 1;
            var online = ok || (Online && _failures < 2);
            if (online != Online) log.LogInformation("Internet is {State}", online ? "up" : "down");
            Online = online;
            try { await Task.Delay(Interval, stop); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task<bool> ProbeAsync(CancellationToken stop)
    {
        try
        {
            using var response = await _http.GetAsync(options.Ai.ConnectivityProbeUrl, HttpCompletionOption.ResponseHeadersRead, stop);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}

/// <summary>Anonymous head count and which caption languages someone is actually reading.</summary>
public sealed class Presence
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Room, string Language)> _byConnection = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _attendees = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _signers = new();

    public int Count => _byConnection.Count;

    /// <summary>A number only, as the spec requires: nothing records who is in which room.</summary>
    public int CountIn(long room) => _byConnection.Values.Count(c => c.Room == room);

    public void Join(string connectionId, long room, string language) => _byConnection[connectionId] = (room, language);
    public void Leave(string connectionId)
    {
        _byConnection.TryRemove(connectionId, out _);
        _attendees.TryRemove(connectionId, out _);
        _signers.TryRemove(connectionId, out _);
    }

    public void WatchSigns(string connectionId, bool on)
    {
        if (on) _signers[connectionId] = true;
        else _signers.TryRemove(connectionId, out _);
    }

    public int SignersIn(long room) => _signers.Keys.Count(id => _byConnection.TryGetValue(id, out var c) && c.Room == room);

    /// <summary>Only for people who registered and opened their ticket on this phone. Everyone else stays a number.</summary>
    public void Identify(string connectionId, long attendee) => _attendees[connectionId] = attendee;

    public IReadOnlyCollection<long> AttendeesIn(long room) =>
        _attendees.Where(a => _byConnection.TryGetValue(a.Key, out var c) && c.Room == room).Select(a => a.Value).Distinct().ToList();
    public (long Room, string Language)? Find(string connectionId) => _byConnection.TryGetValue(connectionId, out var found) ? found : null;

    public IReadOnlyCollection<string> ActiveLanguages(long room) =>
        _byConnection.Values.Where(c => c.Room == room && c.Language.Length > 0).Select(c => c.Language).Distinct().ToList();
}
