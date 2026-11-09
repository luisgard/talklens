using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using TalkLens.Ai;
using TalkLens.Core;
using TalkLens.Media;

namespace TalkLens.Web;

public sealed record ChatRequest(string? Name, string Text);
public sealed record SummaryRequest(string? Language);

/// <summary>Conversations and the content of talks: chat and hallway, summaries, the library, voice, rewind, and the public API (RF-11 to RF-13, RF-19, RF-21, RF-30, RF-33, RF-35).</summary>
public sealed record SignRequest(string? Text);

public static partial class Endpoints
{
    private const string LibraryHeader = "X-Library";
    private const int MaxChatChars = 500;
    private const int ChatPerMinute = 12;
    private const int MaxSearchHits = 40;
    private const int MaxMomentChars = 240;
    private const int SearchesByMeaningPerMinute = 30;
    private const int SignsPerMinute = 30;
    private static readonly TimeSpan ReplayLinkLife = TimeSpan.FromHours(6);
    private static readonly byte[] ReplaySecret = RandomNumberGenerator.GetBytes(32);
    private static readonly TimeSpan LiveSummaryLife = TimeSpan.FromMinutes(1);
    // SQLite hands a deleted talk's id to the next one, so the start time and the text are part of the keys: without them a new talk
    // got the summary of a deleted one.
    private static readonly ConcurrentDictionary<(long Talk, long Started, string Language), (Answer Answer, DateTime At, bool Final)> Summaries = new();
    private static readonly ConcurrentDictionary<(long Id, string Language, string Text), Lazy<Task<byte[]?>>> VoiceCache = new();
    private const int MaxCachedVoices = 200;

    private static bool HallwayOpen(Store store, Talk talk) =>
        talk.KeepAfter && talk.EndedAtUnixMs is { } ended && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < ended + store.HallwayWindowHours() * 3600000L;

    /// <summary>Talks in progress are free to everyone. A finished one needs the library code, when the organizer set one, or a ticket for the event.</summary>
    private static bool CanRead(HttpContext http, Store store, TalkService talks, Talk talk)
    {
        if (talks.IsRunning(talk.Id) || HallwayOpen(store, talk)) return true;
        var code = store.GetSetting(Settings.LibraryCode);
        return code.Length == 0 || http.Request.Headers[LibraryHeader] == code || Me(http, store) != null;
    }

    private static IResult Locked() => Results.Json(new { error = "locked" }, statusCode: 403);

    private static string ReplaySignature(long talk, long expires) =>
        Convert.ToHexString(HMACSHA256.HashData(ReplaySecret, Encoding.UTF8.GetBytes($"{talk}.{expires}")));

    /// <summary>A video element cannot send the library code or ticket, so whoever may read the talk gets links that expire.</summary>
    private static object ReplayInfo(HttpContext http, Store store, TalkService talks, Talk talk)
    {
        // The spec's rule: video stays in the venue unless the organizer, with the speaker's agreement, opens it.
        if (talks.IsRunning(talk.Id) || (Remote.IsRemote(http) && !store.IsOn(Settings.RemoteVideo))) return new { pending = false, parts = Array.Empty<object>() };
        var expires = DateTimeOffset.UtcNow.Add(ReplayLinkLife).ToUnixTimeSeconds();
        var signature = ReplaySignature(talk.Id, expires);
        return new
        {
            pending = Recorder.HasUnfinishedParts(talk.Id),
            parts = Recorder.ReplayParts(talk.Id).Select(p => (object)new { start = p.Start, url = $"/replay/{talk.Id}/{p.Start}.mp4?e={expires}&k={signature}" }).ToArray(),
        };
    }

    private static object TalkInfo(Store store, TalkService talks, Talk talk) => new
    {
        talk,
        live = talks.IsRunning(talk.Id),
        room = store.ListRooms().FirstOrDefault(r => r.Id == talk.RoomId)?.Name ?? "",
        hallwayOpen = talks.IsRunning(talk.Id) || HallwayOpen(store, talk),
    };

    public static void MapContentApi(this WebApplication app)
    {
        // ----- Chat: visible to everyone at the talk, and only after pressing publish -----
        app.MapGet("/api/chat/{talk:long}", (long talk, Store store) =>
        {
            if (talk == 0 && !store.IsOn(Settings.Community)) return Results.NotFound();
            return Results.Ok(store.ListChat(talk));
        });

        app.MapPost("/api/chat/{talk:long}", async (long talk, ChatRequest request, HttpContext http, Store store, TalkService talks, IHubContext<LiveHub> hub) =>
        {
            var text = request.Text?.Trim() ?? "";
            if (text.Length is 0 or > MaxChatChars) return Results.BadRequest(new { error = "text_length" });
            if (talk == 0)
            {
                if (!store.IsOn(Settings.Community)) return Results.NotFound();
            }
            else
            {
                if (store.FindTalk(talk) is not { } found) return Results.NotFound();
                if (!talks.IsRunning(talk) && !HallwayOpen(store, found)) return Results.Conflict(new { error = "closed" });
            }
            if (!Allow("chat:" + Remote.ClientKey(http), ChatPerMinute)) return Results.Json(new { error = "rate_limited" }, statusCode: 429);

            var name = (request.Name ?? "").Trim();
            if (name.Length > MaxNameChars) name = name[..MaxNameChars];
            var message = store.AddChat(talk, name.Length > 0 ? name : "Guest", text);
            await hub.Clients.Group(LiveHub.ChatGroup(talk)).SendAsync("chat", message);
            return Results.Ok(message);
        });

        // ----- A talk's own page: summary, transcript, questions, hallway -----
        app.MapGet("/api/talks/{id:long}", (long id, HttpContext http, Store store, TalkService talks) =>
        {
            if (store.FindTalk(id) is not { } talk) return Results.NotFound();
            if (!CanRead(http, store, talks, talk)) return Locked();
            return Results.Ok(new
            {
                info = TalkInfo(store, talks, talk),
                segments = store.GetSegments(id),
                slides = store.GetSlides(id),
                questions = talks.IsRunning(id) ? [] : store.ListHallQuestions(id),
                replay = ReplayInfo(http, store, talks, talk),
            });
        });

        // What the avatar signed during the talk, so the recording signs it at the same moments. Only fetched when someone turns it on.
        app.MapGet("/api/talks/{id:long}/signs", (long id, HttpContext http, Store store, TalkService talks) =>
        {
            if (store.FindTalk(id) is not { } talk) return Results.NotFound();
            if (!CanRead(http, store, talks, talk)) return Locked();
            return Results.Ok(store.GetSigns(id).Select(s => new { t = s.T, item = JsonDocument.Parse(s.Json).RootElement }));
        });

        // RF-33. The same summary for everyone reading in that language, so popular talks cost one model call.
        app.MapPost("/api/talks/{id:long}/summary", async (long id, SummaryRequest request, HttpContext http, Store store, TalkService talks, Answerer answerer) =>
        {
            if (store.FindTalk(id) is not { } talk) return Results.NotFound();
            if (!CanRead(http, store, talks, talk)) return Locked();
            var language = LanguageCode().IsMatch(request.Language ?? "") ? request.Language! : talk.Language;
            // Only during the talk: reading a summary afterwards says nothing about which minute lost people.
            if (talks.IsRunning(id)) store.LogCatchUp(talk.Id, talks.ElapsedMs(talk.RoomId));

            var key = (id, talk.StartedAtUnixMs, language);
            if (Summaries.TryGetValue(key, out var cached) && (cached.Final || DateTime.UtcNow - cached.At < LiveSummaryLife))
                return Results.Ok(new { answer = cached.Answer, slide = BestSlide(store, id, cached.Answer) });

            var live = talks.IsRunning(id);
            return await WithAnswerSlotAsync(http, async ct =>
            {
                var answer = await answerer.CatchUpAsync(id, 0, language, ct);
                if (answer.Mode == "ai") Summaries[key] = (answer, DateTime.UtcNow, !live);
                return new { answer, slide = BestSlide(store, id, answer) };
            });
        });

        app.MapPost("/api/talks/{id:long}/ask", async (long id, AskRequest request, HttpContext http, Store store, TalkService talks, Answerer answerer) =>
        {
            var question = request.Question?.Trim() ?? "";
            if (question.Length == 0 || question.Length > MaxQuestionChars) return Results.BadRequest(new { error = "question_length" });
            if (store.FindTalk(id) is not { } talk) return Results.NotFound();
            if (!CanRead(http, store, talks, talk)) return Locked();
            return await WithAnswerSlotAsync(http, ct => answerer.AskAsync(id, question, ct));
        });

        // ----- RF-12, RF-19: across every talk that was kept -----
        app.MapGet("/api/library", (HttpContext http, Store store, TalkService talks) =>
        {
            var code = store.GetSetting(Settings.LibraryCode);
            var open = code.Length == 0 || http.Request.Headers[LibraryHeader] == code || Me(http, store) != null;
            var rooms = store.ListRooms().ToDictionary(r => r.Id, r => r.Name);
            return Results.Ok(new
            {
                locked = !open,
                talks = store.ListTalks()
                    .Where(t => t.KeepAfter || talks.IsRunning(t.Id))
                    .Where(t => open || talks.IsRunning(t.Id) || HallwayOpen(store, t))
                    .Select(t => new { t.Id, t.Title, t.Speaker, room = rooms.GetValueOrDefault(t.RoomId, ""), t.StartedAtUnixMs, live = talks.IsRunning(t.Id), hallway = HallwayOpen(store, t), video = !talks.IsRunning(t.Id) && Recorder.ReplayParts(t.Id).Count > 0 }),
            });
        });

        app.MapGet("/api/library/search", async (string q, HttpContext http, Store store, VectorIndex vectors) =>
        {
            var code = store.GetSetting(Settings.LibraryCode);
            if (code.Length > 0 && http.Request.Headers[LibraryHeader] != code && Me(http, store) == null) return Locked();
            return Results.Ok(await SearchTalksAsync(http, store, vectors, q ?? ""));
        });

        // ----- Experimental: a signing avatar. Only the words of what was already public in the talk -----
        app.MapGet("/api/signs", (SignLexicon lexicon) => lexicon.Glosses.Order());
        app.MapPost("/api/signs/translate", async (SignRequest request, HttpContext http, SignTranslator signs) =>
        {
            var text = request.Text?.Trim() ?? "";
            if (text.Length == 0) return Results.BadRequest(new { error = "text" });
            if (!Allow("signs:" + Remote.ClientKey(http), SignsPerMinute)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            return Results.Ok(await signs.TranslateAsync(text, http.RequestAborted));
        });

        // ----- Find the right talk: the schedule by what each talk is about, not just its title. The schedule is public -----
        app.MapGet("/api/event/search", async (string q, HttpContext http, VectorIndex vectors) =>
        {
            var text = (q ?? "").Trim();
            if (text.Length > MaxQuestionChars) text = text[..MaxQuestionChars];
            var useMeaning = Allow("search:" + Remote.ClientKey(http), SearchesByMeaningPerMinute);
            return Results.Ok(await vectors.SessionsAsync(text, useMeaning, MaxSearchHits, http.RequestAborted));
        });

        // ----- The whole talk, to watch afterwards. Same rules as the talk's page, and the same rule for video as the live stream -----
        app.MapGet("/replay/{talk:long}/{start:long}.mp4", (long talk, long start, long? e, string? k, HttpContext http, Store store, TalkService talks) =>
        {
            if (Remote.IsRemote(http) && !store.IsOn(Settings.RemoteVideo)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var valid = e is { } expires && expires > DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(k ?? ""), Encoding.UTF8.GetBytes(ReplaySignature(talk, expires)));
            if (!valid) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (store.FindTalk(talk) == null || talks.IsRunning(talk)) return Results.NotFound();
            var part = Recorder.ReplayParts(talk).FirstOrDefault(p => p.Start == start);
            return part.File == null ? Results.NotFound() : Results.File(part.File, "video/mp4", enableRangeProcessing: true);
        });

        app.MapPost("/api/library/ask", async (AskRequest request, HttpContext http, Store store, Answerer answerer) =>
        {
            var question = request.Question?.Trim() ?? "";
            if (question.Length == 0 || question.Length > MaxQuestionChars) return Results.BadRequest(new { error = "question_length" });
            var code = store.GetSetting(Settings.LibraryCode);
            if (code.Length > 0 && http.Request.Headers[LibraryHeader] != code && Me(http, store) == null) return Locked();
            return await WithAnswerSlotAsync(http, ct => answerer.AskAcrossAsync(question, ct));
        });

        // ----- RF-11: the translated captions, read aloud -----
        app.MapGet("/api/rooms/{room:long}/voice/{id:long}/{language}.mp3", async (long room, long id, string language, TalkService talks, VoiceModel voice, Connectivity net, HttpContext http) =>
        {
            if (!voice.Available || !net.Online) return Results.NotFound();
            // Only text the server itself translated in this talk, so this cannot be used as a free speech service.
            if (talks.RecentTranslation(room, id, language) is not { } text) return Results.NotFound();
            if (VoiceCache.Count > MaxCachedVoices) VoiceCache.Clear();
            var audio = await VoiceCache.GetOrAdd((id, language, text), _ => new Lazy<Task<byte[]?>>(() => voice.SpeakAsync(text, CancellationToken.None))).Value;
            return audio == null ? Results.NotFound() : Results.File(audio, "audio/mpeg");
        });

        // ----- Rewind and remote viewing: the same video as HLS, which keeps the last few minutes -----
        app.MapGet("/hls/r{room:long}/{**file}", async (long room, string file, HttpContext http, IHttpClientFactory factory, Store store) =>
        {
            // The spec's rule: video stays in the venue unless the organizer, with the speaker's agreement, opens it.
            if (Remote.IsRemote(http) && !store.IsOn(Settings.RemoteVideo)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (file.Contains("..")) return Results.NotFound();
            using var response = await factory.CreateClient("media").GetAsync($"{MediaMtx.HlsBase}/{MediaMtx.CameraPath(room)}/{file}{http.Request.QueryString}", HttpCompletionOption.ResponseHeadersRead, http.RequestAborted);
            http.Response.StatusCode = (int)response.StatusCode;
            if (response.Content.Headers.ContentType is { } type) http.Response.ContentType = type.ToString();
            http.Response.Headers.CacheControl = "no-cache";
            await response.Content.CopyToAsync(http.Response.Body, http.RequestAborted);
            return Results.Empty;
        });

        // ----- RF-21: for streaming platforms -----
        var v1 = app.MapGroup("/api/v1").AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            http.Response.Headers.AccessControlAllowOrigin = "*";
            var key = http.RequestServices.GetRequiredService<Store>().GetSetting(Settings.ApiKey);
            return key.Length > 0 && http.Request.Headers["X-Api-Key"] == key ? await next(context) : Results.Unauthorized();
        });
        app.MapMethods("/api/v1/{**path}", ["OPTIONS"], (HttpContext http) =>
        {
            http.Response.Headers.AccessControlAllowOrigin = "*";
            http.Response.Headers.AccessControlAllowHeaders = "X-Api-Key, Content-Type";
            http.Response.Headers.AccessControlAllowMethods = "GET, POST";
            return Results.NoContent();
        });
        v1.MapGet("/rooms", (StatusBroadcaster status) => status.PublicSummary());
        v1.MapGet("/talks", (Store store) => store.ListTalks().Where(t => t.KeepAfter).Select(t => new { t.Id, t.Title, t.Speaker, t.Language, t.StartedAtUnixMs, t.EndedAtUnixMs, t.RoomId }));
        v1.MapGet("/talks/{id:long}/transcript", (long id, Store store) =>
            store.FindTalk(id) is null ? Results.NotFound() : Results.Ok(new { segments = store.GetSegments(id), slides = store.GetSlides(id).Select(s => new { s.Id, s.T, s.Text }) }));
        v1.MapPost("/talks/{id:long}/ask", async (long id, AskRequest request, HttpContext http, Store store, Answerer answerer) =>
        {
            var question = request.Question?.Trim() ?? "";
            if (question.Length == 0 || question.Length > MaxQuestionChars) return Results.BadRequest(new { error = "question_length" });
            return store.FindTalk(id) is null ? Results.NotFound() : await WithAnswerSlotAsync(http, ct => answerer.AskAsync(id, question, ct));
        });
        v1.MapPost("/talks/{id:long}/summary", async (long id, SummaryRequest request, HttpContext http, Store store, Answerer answerer) =>
        {
            if (store.FindTalk(id) is not { } talk) return Results.NotFound();
            var language = LanguageCode().IsMatch(request.Language ?? "") ? request.Language! : talk.Language;
            return await WithAnswerSlotAsync(http, ct => answerer.CatchUpAsync(id, 0, language, ct));
        });
        v1.MapGet("/search", async (string q, HttpContext http, Store store, VectorIndex vectors) => await SearchTalksAsync(http, store, vectors, q ?? ""));
    }

    /// <summary>The slide the summary drew on most, or else the one from the middle of the talk.</summary>
    public static object? BestSlide(Store store, long talkId, Answer answer)
    {
        var slides = store.GetSlides(talkId);
        if (slides.Count == 0) return null;
        var cited = answer.Citations.Where(c => c.Kind == "slide").Select(c => slides.FirstOrDefault(s => s.Id == c.RefId)).OfType<Slide>().FirstOrDefault();
        if (cited == null && answer.Citations.Count > 0)
        {
            var moment = answer.Citations[answer.Citations.Count / 2].T;
            cited = slides.LastOrDefault(s => s.T <= moment);
        }
        var slide = cited ?? slides[slides.Count / 2];
        return new { slide.Id, slide.T, slide.Text, url = $"/slides/{talkId}/{slide.Id}.jpg" };
    }

    /// <summary>The query goes to the embedding provider, like any question to the AI. Past the limit a phone still gets word search, which costs nothing.</summary>
    private static async Task<object> SearchTalksAsync(HttpContext http, Store store, VectorIndex vectors, string query)
    {
        var text = query.Trim();
        if (text.Length > MaxQuestionChars) text = text[..MaxQuestionChars];
        var meaning = Allow("search:" + Remote.ClientKey(http), SearchesByMeaningPerMinute) ? await vectors.SearchAsync(text, null, MaxSearchHits, http.RequestAborted) : [];
        return SearchResults(store, text, meaning, await vectors.WordsAsync(text, MaxSearchHits, http.RequestAborted));
    }

    public static object SearchResults(Store store, string query, IReadOnlyList<VectorHit> meaning, IReadOnlyList<(string Kind, long RefId, long TalkId)>? words = null)
    {
        var talks = store.ListTalks().ToDictionary(t => t.Id);
        var moments = Hybrid.Fuse(words ?? store.SearchAll(Retriever.BuildQuery(query), MaxSearchHits), meaning, MaxSearchHits).Where(m => talks.ContainsKey(m.TalkId));
        // In ranking order, so the talk with the closest moment comes first.
        return Hybrid.Resolve(store, moments)
            .GroupBy(found => found.Moment.TalkId)
            .Select(group =>
            {
                var talk = talks[group.Key];
                return new
                {
                    talkId = talk.Id, talk.Title, talk.Speaker, count = group.Count(),
                    moments = group.Take(5).Select(found => new
                    {
                        kind = found.Moment.Kind, t = found.T, byMeaning = found.Moment.ByMeaning,
                        text = found.Text.Length <= MaxMomentChars ? found.Text : found.Text[..MaxMomentChars].TrimEnd() + "…",
                    }).ToList(),
                };
            })
            .ToList();
    }

    public static void MapEventPages(this WebApplication app)
    {
        var pages = Path.Combine(AppContext.BaseDirectory, "private");
        string Public(string file) => Path.Combine(app.Environment.WebRootPath, file);
        IResult Serve(HttpContext http, string file)
        {
            http.Response.Headers.CacheControl = "no-cache";
            return Results.File(Public(file), "text/html");
        }

        foreach (var (route, file) in new[] { ("/welcome", "welcome.html"), ("/signs", "signs.html"), ("/event", "event.html"), ("/me", "me.html"), ("/library", "library.html"), ("/album", "album.html"), ("/community", "community.html"), ("/connect", "connect.html"), ("/embed", "embed.html") })
            app.MapGet(route, (HttpContext http) => Serve(http, file));
        app.MapGet("/talk/{id:long}", (long id, HttpContext http) => Serve(http, "talk.html"));
        app.MapGet("/sponsor/{key}", (string key, HttpContext http) => Serve(http, "sponsor.html"));
        // These only carry a code to the page, which then asks before anything is shared.
        app.MapGet("/ticket/{token}", (string token) => Results.Redirect($"/me?ticket={Uri.EscapeDataString(token)}"));
        app.MapGet("/connect/{code}", (string code) => Results.Redirect($"/me?connect={Uri.EscapeDataString(code)}"));
        app.MapGet("/stand/{code}", (string code) => Results.Redirect($"/me?stand={Uri.EscapeDataString(code)}"));

        foreach (var name in new[] { "start", "manage", "organizer", "reports" })
            app.MapGet("/" + name, (HttpContext http, OperatorAuth auth, string? key) => ServePrivatePage(http, auth, key, Path.Combine(pages, name + ".html"), "/" + name));

        // A phone's camera only opens over HTTPS.
        app.MapGet("/checkin", (HttpContext http, OperatorAuth auth, string? key) =>
        {
            if (!http.Request.IsHttps && http.Request.Host.Host != "localhost")
                return Results.Redirect($"https://{http.Request.Host.Host}:{http.RequestServices.GetRequiredService<AppOptions>().Network.HttpsPort}{http.Request.Path}{http.Request.QueryString}");
            return ServePrivatePage(http, auth, key, Path.Combine(pages, "checkin.html"), "/checkin");
        });
    }
}
