using Microsoft.AspNetCore.SignalR;
using TalkLens.Ai;
using TalkLens.Core;

namespace TalkLens.Web;

public sealed record PollRequest(string? Question, IReadOnlyList<string>? Options);
public sealed record VoteRequest(string? Voter, int Choice);
public sealed record RatingRequest(string? Voter, int Stars, string? Comment);
public sealed record InsightRequest(string? Language);

/// <summary>Polls, ratings, subtitles, chapters, the glossary and the speaker's pace.</summary>
public static partial class Endpoints
{
    private const int MaxPollOptions = 6;
    private const int MaxPollChars = 200;
    private const int MaxOptionChars = 80;
    private const int MaxCommentChars = 500;
    private const int MaxVoterChars = 64;
    private const int VotesPerMinute = 30;
    private const int InsightsPerMinute = 20;
    // Above this a listener struggles, and captions and signs fall behind.
    private const int FastWordsPerMinute = 170;
    private const long BehindMs = 6000;
    private const long PaceWindowMs = 60000;

    /// <summary>What phones see. Counts appear only once enough people voted that a count does not reveal anyone's vote.</summary>
    private static object PollView(Store store, Poll poll)
    {
        var counts = store.PollCounts(poll);
        var minimum = store.MinimumParticipants();
        var total = counts.Sum();
        return new { poll.Id, poll.TalkId, poll.Question, poll.Options, poll.Open, total = Reports.Gate(total, minimum), counts = total >= minimum ? counts : null, minimum };
    }

    private static Task PollChanged(IHubContext<LiveHub> hub, Store store, Poll poll, long room) => Task.WhenAll(
        hub.Clients.Group(LiveHub.RoomGroup(room)).SendAsync("poll", PollView(store, poll)),
        hub.Clients.Group(LiveHub.OperatorGroup).SendAsync("polls", room));

    private static bool ValidVoter(string? voter) => voter is { Length: > 0 and <= MaxVoterChars };

    public static void MapEngagementApi(this WebApplication app)
    {
        // ----- Polls: the latest of the talk in this room, open or just closed -----
        app.MapGet("/api/rooms/{room:long}/poll", (long room, string? voter, Store store, TalkService talks) =>
        {
            if (talks.Current(room) is not { } talk || store.ListPolls(talk.Id).LastOrDefault() is not { } poll) return Results.Ok(new { poll = (object?)null });
            return Results.Ok(new { poll = PollView(store, poll), mine = ValidVoter(voter) ? store.VoteOf(poll.Id, voter!) : null });
        });

        app.MapPost("/api/polls/{id:long}/vote", async (long id, VoteRequest request, HttpContext http, Store store, IHubContext<LiveHub> hub) =>
        {
            if (!ValidVoter(request.Voter)) return Results.BadRequest(new { error = "voter" });
            if (!Allow("vote:" + Remote.ClientKey(http), VotesPerMinute)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            if (store.FindPoll(id) is not { } poll) return Results.NotFound();
            if (!poll.Open) return Results.Conflict(new { error = "closed" });
            if (request.Choice < 0 || request.Choice >= poll.Options.Count) return Results.BadRequest(new { error = "choice" });
            store.Vote(id, request.Voter!, request.Choice);
            if (store.FindTalk(poll.TalkId) is { } talk) await PollChanged(hub, store, poll, talk.RoomId);
            return Results.Ok(PollView(store, poll));
        });

        // ----- Ratings: during the talk or after it, one per phone, which it can change -----
        app.MapGet("/api/talks/{id:long}/rating", (long id, string? voter, Store store) =>
            Results.Ok(new { mine = ValidVoter(voter) ? store.RatingOf(id, voter!) : null }));

        app.MapPost("/api/talks/{id:long}/rating", (long id, RatingRequest request, HttpContext http, Store store, TalkService talks) =>
        {
            if (!ValidVoter(request.Voter)) return Results.BadRequest(new { error = "voter" });
            if (request.Stars is < 1 or > 5) return Results.BadRequest(new { error = "stars" });
            var comment = request.Comment?.Trim() ?? "";
            if (comment.Length > MaxCommentChars) return Results.BadRequest(new { error = "comment" });
            if (!Allow("rate:" + Remote.ClientKey(http), VotesPerMinute)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            if (store.FindTalk(id) is not { } talk) return Results.NotFound();
            if (!CanRead(http, store, talks, talk)) return Locked();
            store.Rate(id, request.Voter!, request.Stars, comment);
            return Results.Ok();
        });

        // ----- Made by the AI from the transcript -----
        app.MapPost("/api/talks/{id:long}/subtitles", (long id, InsightRequest request, HttpContext http, Store store, TalkService talks, TalkInsights insights) =>
            InsightAsync(id, request, http, store, talks, (talk, live, language, ct) => insights.SubtitlesAsync(talk, live, language, ct)));
        app.MapPost("/api/talks/{id:long}/chapters", (long id, InsightRequest request, HttpContext http, Store store, TalkService talks, TalkInsights insights) =>
            InsightAsync(id, request, http, store, talks, (talk, live, language, ct) => insights.ChaptersAsync(talk, live, language, ct)));
        app.MapPost("/api/talks/{id:long}/glossary", (long id, InsightRequest request, HttpContext http, Store store, TalkService talks, TalkInsights insights) =>
            InsightAsync(id, request, http, store, talks, (talk, live, language, ct) => insights.GlossaryAsync(talk, live, language, ct)));
    }

    private static async Task<IResult> InsightAsync<T>(long id, InsightRequest request, HttpContext http, Store store, TalkService talks,
        Func<Talk, bool, string, CancellationToken, Task<T?>> make) where T : class
    {
        if (store.FindTalk(id) is not { } talk) return Results.NotFound();
        if (!CanRead(http, store, talks, talk)) return Locked();
        if (!Allow("insight:" + Remote.ClientKey(http), InsightsPerMinute)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        var language = LanguageCode().IsMatch(request.Language ?? "") ? request.Language! : talk.Language;
        var result = await make(talk, talks.IsRunning(id), language, http.RequestAborted);
        // Null means no model or no internet: the page says so instead of showing nothing.
        return result == null ? Results.Json(new { error = "unavailable" }, statusCode: 503) : Results.Ok(new { language, items = result });
    }

    public static void MapEngagementOperatorApi(this RouteGroupBuilder op)
    {
        op.MapGet("/rooms/{room:long}/polls", (long room, Store store, TalkService talks) =>
        {
            var talk = talks.Latest(room);
            return talk == null ? [] : store.ListPolls(talk.Id).Select(p => new { p.Id, p.Question, p.Options, p.Open, counts = store.PollCounts(p) }).Reverse().ToList();
        });

        op.MapPost("/rooms/{room:long}/polls", async (long room, PollRequest request, Store store, TalkService talks, IHubContext<LiveHub> hub) =>
        {
            if (talks.Current(room) is not { } talk) return Results.BadRequest(new { error = "Start the talk first." });
            var question = request.Question?.Trim() ?? "";
            var options = (request.Options ?? []).Select(o => o.Trim()).Where(o => o.Length > 0).Distinct().ToList();
            if (question.Length is 0 or > MaxPollChars) return Results.BadRequest(new { error = "Write a question." });
            if (options.Count is < 2 or > MaxPollOptions || options.Any(o => o.Length > MaxOptionChars))
                return Results.BadRequest(new { error = $"Give between 2 and {MaxPollOptions} short answers." });
            var poll = store.OpenPoll(talk.Id, question, options);
            await PollChanged(hub, store, poll, room);
            return Results.Ok(poll);
        });

        op.MapPost("/polls/{id:long}/close", async (long id, Store store, IHubContext<LiveHub> hub) =>
        {
            if (store.FindPoll(id) is not { } poll) return Results.NotFound();
            store.ClosePoll(id);
            if (store.FindTalk(poll.TalkId) is { } talk) await PollChanged(hub, store, store.FindPoll(id)!, talk.RoomId);
            return Results.Ok();
        });

        // ----- For the speaker screen: slow down when the room cannot keep up -----
        op.MapGet("/rooms/{room:long}/pace", (long room, Store store, TalkService talks) =>
        {
            if (talks.Current(room) is not { } talk) return Results.Ok(new { live = false });
            var elapsed = talks.ElapsedMs(room);
            var window = Math.Min(PaceWindowMs, elapsed);
            var words = store.GetSegments(talk.Id, elapsed - window).Sum(s => s.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
            var wordsPerMinute = window < 20000 ? 0 : (int)(words * 60000L / window);
            var behindMs = talks.CaptionLagMs(room);
            return Results.Ok(new
            {
                live = true,
                wordsPerMinute,
                behindMs,
                tooFast = wordsPerMinute > FastWordsPerMinute,
                behind = behindMs > BehindMs,
                fastAbove = FastWordsPerMinute,
            });
        });
    }
}
