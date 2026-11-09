using TalkLens.Ai;
using TalkLens.Core;

namespace TalkLens.Web;

public sealed record ConnectRequest(long To, string? Reason);
public sealed record AnswerConnectionRequest(bool Accept, string? Spot);
public sealed record UsefulRequest(int Answer);

/// <summary>
/// Meeting people: who matches, asking with a reason, agreeing where to meet, and confirming it happened. Only people
/// who turned on "show me" see anyone or are seen, and nobody learns that their request was declined.
/// </summary>
public static partial class Endpoints
{
    private const int MaxReasonChars = 160;
    private const int MaxSpotChars = 80;
    private const int MaxPendingRequests = 15;
    private const int RequestsPerMinute = 6;

    public static void MapNetworkingApi(this WebApplication app)
    {
        app.MapGet("/api/people", async (HttpContext http, Store store, Matchmaker matchmaker, CancellationToken ct) =>
        {
            if (Me(http, store) is not { } me) return Results.Unauthorized();
            if (!me.Networking) return Results.Ok(new { visible = false, people = Array.Empty<object>() });
            var links = store.ListConnectionsOf(me.Id).ToDictionary(c => c.Other(me.Id));
            var people = (await matchmaker.FindAsync(me, ct)).Select(m => new
            {
                id = m.Person.Id,
                name = m.Person.Name,
                seeking = m.Person.Seeking,
                offering = m.Person.Offering,
                offersWhatYouSeek = m.OffersWhatYouSeek,
                seeksWhatYouOffer = m.SeeksWhatYouOffer,
                sharedTalks = m.SharedTalks,
                byMeaning = m.ByMeaning,
                link = links.TryGetValue(m.Person.Id, out var link) ? LinkFor(link, me.Id) : null,
            });
            return Results.Ok(new { visible = true, people });
        });

        app.MapGet("/api/connections", (HttpContext http, Store store) =>
        {
            if (Me(http, store) is not { } me) return Results.Unauthorized();
            var mine = store.ListConnectionsOf(me.Id);
            object Card(Connection c)
            {
                var other = store.FindAttendee(c.Other(me.Id))!;
                return new { c.Id, name = other.Name, other.Seeking, other.Offering, c.Reason, c.Spot, c.Status, useful = c.UsefulBy(me.Id), at = c.CreatedAt };
            }
            return Results.Ok(new
            {
                incoming = mine.Where(c => c.ToId == me.Id && c.Status == ConnectionStatus.Requested).Select(Card),
                together = mine.Where(c => c.Status is ConnectionStatus.Accepted or ConnectionStatus.Met).Select(Card),
                spots = store.MeetingSpots(),
            });
        });

        // Just a number, for the badge on the Connect link.
        app.MapGet("/api/connections/waiting", (HttpContext http, Store store) =>
        {
            if (Me(http, store) is not { } me) return Results.Unauthorized();
            return Results.Ok(new { count = store.ListConnectionsOf(me.Id).Count(c => c.ToId == me.Id && c.Status == ConnectionStatus.Requested) });
        });

        app.MapPost("/api/connections", (ConnectRequest request, HttpContext http, Store store) =>
        {
            if (Me(http, store) is not { } me) return Results.Unauthorized();
            if (!me.Networking) return Results.BadRequest(new { error = "hidden" });
            if (request.To == me.Id || store.FindAttendee(request.To) is not { Networking: true } other) return Results.NotFound();
            var reason = (request.Reason ?? "").Trim();
            if (reason.Length > MaxReasonChars) return Results.BadRequest(new { error = "reason" });
            if (!Allow("connect:" + me.Id, RequestsPerMinute) || store.CountPendingFrom(me.Id) >= MaxPendingRequests)
                return Results.Json(new { error = "rate_limited" }, statusCode: 429);
            var made = store.RequestConnection(me.Id, other.Id, reason);
            return made == null ? Results.Conflict(new { error = "exists" }) : Results.Ok(LinkFor(made, me.Id));
        });

        app.MapPost("/api/connections/{id:long}/answer", (long id, AnswerConnectionRequest request, HttpContext http, Store store) =>
        {
            if (Me(http, store) is not { } me) return Results.Unauthorized();
            var spot = (request.Spot ?? "").Trim();
            if (spot.Length > MaxSpotChars) return Results.BadRequest(new { error = "spot" });
            return store.AnswerConnection(id, me.Id, request.Accept, spot) ? Results.Ok() : Results.NotFound();
        });

        app.MapPost("/api/connections/{id:long}/useful", (long id, UsefulRequest request, HttpContext http, Store store) =>
        {
            if (Me(http, store) is not { } me) return Results.Unauthorized();
            if (request.Answer is < 0 or > 2) return Results.BadRequest(new { error = "answer" });
            return store.SetUseful(id, me.Id, request.Answer) ? Results.Ok() : Results.NotFound();
        });
    }

    /// <summary>What the person sees about a link. A declined request still reads "sent" to whoever asked.</summary>
    private static object LinkFor(Connection c, long me) => new
    {
        c.Id,
        status = c.Status == ConnectionStatus.Declined && c.FromId == me ? ConnectionStatus.Requested : c.Status,
        mine = c.FromId == me,
    };
}
