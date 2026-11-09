namespace TalkLens.Core;

public sealed record WaitingRequest(long Id, string Sponsor, string SponsorKey, string Person, string Slot, long Minutes);
public sealed record UnconfirmedMeeting(long Id, string From, string To, string Spot, long Minutes);

/// <param name="Visible">People who chose to be seen. Everyone in <paramref name="Alone"/> is one of them.</param>
public sealed record NowState(int Visible, IReadOnlyList<string> Alone, IReadOnlyList<WaitingRequest> SponsorsWaiting, IReadOnlyList<UnconfirmedMeeting> NotConfirmed);

/// <summary>
/// What needs the organizer during the event, and nothing else: people nobody has connected with yet, sponsor
/// requests left waiting, and meetings agreed but not confirmed. Each list is oldest first, so the top needs them most.
/// </summary>
public static class NowBoard
{
    public static readonly TimeSpan SponsorWait = TimeSpan.FromMinutes(15);

    public static NowState Compute(Store store, long nowMs)
    {
        var people = store.ListAttendees().ToDictionary(a => a.Id);
        var links = store.ListConnections();
        // Agreed to meet or met: a request still waiting for an answer is not a connection yet.
        var connected = links.Where(c => c.Status is ConnectionStatus.Accepted or ConnectionStatus.Met)
            .SelectMany(c => new[] { c.FromId, c.ToId }).ToHashSet();
        var visible = people.Values.Where(a => a.Networking).ToList();
        var alone = visible.Where(a => !connected.Contains(a.Id)).OrderBy(a => a.CreatedAt).Select(a => a.Name).ToList();

        var sponsors = store.ListSponsors().ToDictionary(s => s.Id);
        long Minutes(long since) => Math.Max(0, (nowMs - since) / 60000);
        var waiting = store.ListAllOfferRequests()
            .Where(r => r.AttendedAt == null && nowMs - r.CreatedAt >= SponsorWait.TotalMilliseconds && sponsors.ContainsKey(r.SponsorId) && people.ContainsKey(r.AttendeeId))
            .Select(r => new WaitingRequest(r.Id, sponsors[r.SponsorId].Name, sponsors[r.SponsorId].Key, people[r.AttendeeId].Name, r.Slot, Minutes(r.CreatedAt)))
            .ToList();

        var unconfirmed = links
            .Where(c => c.Status == ConnectionStatus.Accepted && people.ContainsKey(c.FromId) && people.ContainsKey(c.ToId))
            .OrderBy(c => c.AnsweredAt ?? c.CreatedAt)
            .Select(c => new UnconfirmedMeeting(c.Id, people[c.FromId].Name, people[c.ToId].Name, c.Spot, Minutes(c.AnsweredAt ?? c.CreatedAt)))
            .ToList();

        return new NowState(visible.Count, alone, waiting, unconfirmed);
    }
}

/// <summary>One message on every room screen and phone, such as a round of introductions. Kept in memory: it lasts minutes.</summary>
public sealed class Announcements
{
    public static readonly TimeSpan Lasts = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    private (string Kind, long Until)? _current;

    public (string Kind, long Until)? Current(long nowMs)
    {
        lock (_gate) return _current is { } c && c.Until > nowMs ? c : null;
    }

    public (string Kind, long Until) Start(string kind, long nowMs)
    {
        lock (_gate) return (_current = (kind, nowMs + (long)Lasts.TotalMilliseconds)).Value;
    }

    public void Stop() { lock (_gate) _current = null; }
}
