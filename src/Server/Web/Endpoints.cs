using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.SignalR;
using TalkLens.Ai;
using TalkLens.Core;
using TalkLens.Media;

namespace TalkLens.Web;

public sealed record AskRequest(string Question);
public sealed record CatchUpRequest(int? Minutes, string? Language);
public sealed record HostRequest(string Host);
public sealed record RoomRequest(string Name);
public sealed record QuestionStatusRequest(string Status);

public static partial class Endpoints
{
    private const int MaxQuestionChars = 500;
    private const int AsksPerMinute = 6;
    private const int MaxRoomNameChars = 60;
    private static readonly string[] QuestionStatuses = ["open", "shown", "answered", "dismissed"];
    private static readonly SemaphoreSlim AnswerSlots = new(8, 8);
    private static readonly ConcurrentDictionary<string, Queue<DateTime>> AskLog = new();

    /// <summary>Paths phones probe to detect a captive portal. Answering with a redirect pops the app open (RF-07).</summary>
    private static readonly string[] CaptiveProbes =
        ["/generate_204", "/gen_204", "/hotspot-detect.html", "/library/test/success.html", "/connecttest.txt", "/ncsi.txt", "/redirect", "/canonical.html", "/success.txt"];

    /// <summary>The talk the agenda expects next in a room, so attendees can tell "not started yet" from "running late" from "over".
    /// Talks often start late or end early, so one that already ran here is skipped even while its slot lasts.</summary>
    public static AgendaItem? NextInAgenda(Store store, long room, Talk? latest)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return store.ListAgenda().FirstOrDefault(item => item.RoomId == room && item.EndsAt > now
            && !(latest != null && string.Equals(item.Title.Trim(), latest.Title.Trim(), StringComparison.OrdinalIgnoreCase)));
    }

    public static void MapAttendeeApi(this WebApplication app)
    {
        app.MapGet("/api/rooms", (StatusBroadcaster status) => status.PublicSummary());

        app.MapGet("/api/rooms/{room:long}/state", (long room, HttpContext http, TalkService talks, AppOptions options, StatusBroadcaster status, Connectivity net, Store store, VoiceModel voice) =>
        {
            if (store.ListRooms().FirstOrDefault(r => r.Id == room) is not { } found) return Results.NotFound();
            var latest = talks.Latest(room);
            return Results.Ok(new
            {
                room = found,
                talk = latest,
                next = NextInAgenda(store, room, latest),
                live = talks.Current(room) != null,
                elapsedMs = talks.ElapsedMs(room),
                videoReady = status.CameraLive(room),
                internet = net.Online,
                captionMode = talks.CaptionMode(room),
                languages = options.Languages,
                voiceCloud = voice.Available,
                remote = Remote.IsRemote(http),
                remoteVideo = store.IsOn(Settings.RemoteVideo),
                capacity = store.GetLimits(room).Capacity,
                full = status.IsFull(room),
            });
        });

        app.MapGet("/api/rooms/{room:long}/transcript", (long room, TalkService talks, Store store) =>
        {
            var talk = talks.Latest(room);
            return talk == null
                ? Results.Ok(new { segments = Array.Empty<Segment>(), slides = Array.Empty<Slide>() })
                : Results.Ok(new { segments = store.GetSegments(talk.Id), slides = store.GetSlides(talk.Id) });
        });

        app.MapGet("/slides/{talkId:long}/{slideId:long}.jpg", (long talkId, long slideId) =>
        {
            var path = TalkService.SlidePath(talkId, slideId);
            return File.Exists(path) ? Results.File(path, "image/jpeg") : Results.NotFound();
        });

        app.MapPost("/api/rooms/{room:long}/ask", async (long room, AskRequest request, HttpContext http, TalkService talks, Answerer answerer) =>
        {
            var question = request.Question?.Trim() ?? "";
            if (question.Length == 0 || question.Length > MaxQuestionChars) return Results.BadRequest(new { error = "question_length" });
            if (talks.Latest(room) is not { } talk) return Results.Ok(new Answer("", [], "empty"));
            return await WithAnswerSlotAsync(http, ct => answerer.AskAsync(talk.Id, question, ct));
        });

        app.MapPost("/api/rooms/{room:long}/questions", async (long room, AskRequest request, HttpContext http, TalkService talks, QuestionQueue queue) =>
        {
            var question = request.Question?.Trim() ?? "";
            if (question.Length == 0 || question.Length > MaxQuestionChars) return Results.BadRequest(new { error = "question_length" });
            if (talks.Current(room) is not { } talk) return Results.Conflict(new { error = "not_live" });
            if (!AllowAsk("speaker:" + Remote.ClientKey(http))) return Results.Json(new { error = "rate_limited" }, statusCode: 429);
            await queue.SubmitAsync(talk, talks.ElapsedMs(room), question);
            return Results.Ok();
        });

        app.MapPost("/api/rooms/{room:long}/catchup", async (long room, CatchUpRequest request, HttpContext http, TalkService talks, Answerer answerer, Store store) =>
        {
            if (talks.Latest(room) is not { } talk) return Results.Ok(new Answer("", [], "empty"));
            var from = request.Minutes is { } minutes ? Math.Max(0, talks.ElapsedMs(room) - minutes * 60000L) : 0;
            store.LogCatchUp(talk.Id, talks.ElapsedMs(room));
            var language = LanguageCode().IsMatch(request.Language ?? "") ? request.Language! : talk.Language;
            return await WithAnswerSlotAsync(http, ct => answerer.CatchUpAsync(talk.Id, from, language, ct));
        });
    }

    public static void MapOperatorApi(this WebApplication app)
    {
        var op = app.MapGroup("/api/op").AddEndpointFilter(async (context, next) =>
            context.HttpContext.RequestServices.GetRequiredService<OperatorAuth>().IsOperator(context.HttpContext)
                ? await next(context)
                : Results.Unauthorized());

        op.MapEventOperatorApi();
        op.MapEngagementOperatorApi();
        op.MapWallOperatorApi();
        op.MapNowOperatorApi();
        op.MapPost("/rooms", (RoomRequest request, Store store) =>
        {
            var name = request.Name?.Trim() ?? "";
            return name.Length is 0 or > MaxRoomNameChars ? Results.BadRequest(new { error = "Give the room a name." }) : Results.Ok(store.AddRoom(name));
        });
        op.MapPut("/rooms/{room:long}", (long room, RoomRequest request, Store store) =>
        {
            var name = request.Name?.Trim() ?? "";
            if (name.Length is 0 or > MaxRoomNameChars) return Results.BadRequest(new { error = "Give the room a name." });
            store.RenameRoom(room, name);
            return Results.Ok();
        });
        op.MapDelete("/rooms/{room:long}", (long room, Store store, TalkService talks) =>
        {
            if (talks.Current(room) != null) return Results.BadRequest(new { error = "Stop the talk in this room first." });
            if (store.ListRooms().Count <= 1) return Results.BadRequest(new { error = "An event needs at least one room." });
            store.DeleteRoom(room);
            return Results.Ok();
        });
        op.MapGet("/rooms/{room:long}/questions", (long room, TalkService talks, QuestionQueue queue) =>
            talks.Latest(room) is { } talk ? queue.Groups(talk.Id) : []);
        op.MapPost("/rooms/{room:long}/questions/{group:long}/status", async (long room, long group, QuestionStatusRequest request, TalkService talks, Store store, IHubContext<LiveHub> hub) =>
        {
            if (!QuestionStatuses.Contains(request.Status)) return Results.BadRequest(new { error = "Unknown status." });
            if (talks.Latest(room) is not { } talk) return Results.NotFound();
            store.SetGroupStatus(talk.Id, group, request.Status);
            await hub.Clients.Group(LiveHub.OperatorGroup).SendAsync("questions", room);
            return Results.Ok();
        });
        op.MapPost("/rooms/{room:long}/talks/start", async (long room, StartTalkRequest request, TalkService talks) =>
        {
            try
            {
                return Results.Ok(await talks.StartAsync(room, request));
            }
            catch (InvalidOperationException e)
            {
                return Results.BadRequest(new { error = e.Message });
            }
        });
        op.MapPost("/rooms/{room:long}/talks/stop", async (long room, TalkService talks) =>
        {
            await talks.StopAsync(room);
            return Results.Ok();
        });
        op.MapGet("/talks", (Store store) => store.ListTalks());
        op.MapDelete("/talks/{id:long}", (long id, TalkService talks) =>
        {
            if (talks.IsRunning(id)) return Results.BadRequest(new { error = "Stop the talk first." });
            talks.DeleteTalkData(id);
            return Results.Ok();
        });
        op.MapGet("/status", (StatusBroadcaster status) => status.LastStatus);
        op.MapGet("/devices", async (AppOptions options) => await ListDevicesAsync(options));
        op.MapGet("/network", (LanInfo lan, AppOptions options, OperatorAuth auth) => new
        {
            host = lan.Host,
            addresses = LanInfo.Addresses(),
            attendeeUrl = lan.HttpUrl + "/",
            wifiSsid = options.Network.WifiSsid,
        });
        op.MapPost("/network/host", (HostRequest request, LanInfo lan) =>
        {
            if (!LanInfo.Addresses().Contains(request.Host)) return Results.BadRequest(new { error = "Unknown address." });
            lan.SetHost(request.Host);
            return Results.Ok();
        });
        op.MapGet("/qr/{which}.svg", (string which, long? room, LanInfo lan, AppOptions options, OperatorAuth auth) =>
        {
            var network = options.Network;
            string? text = which switch
            {
                "event" => lan.HttpUrl + "/",
                "attendee" => $"{lan.HttpUrl}/r/{room ?? 1}",
                "camera" => $"{lan.HttpsUrl}/publish?room={room ?? 1}&path=cam&key={auth.Key}",
                "screen" => $"{lan.HttpsUrl}/publish?room={room ?? 1}&path=slides&key={auth.Key}",
                "speaker" => $"{lan.HttpUrl}/speaker?room={room ?? 1}&key={auth.Key}",
                "checkin" => $"{lan.HttpsUrl}/checkin?key={auth.Key}",
                "wifi" when network.WifiSsid.Length > 0 => WifiPayload(network.WifiSsid, network.WifiPassword),
                _ => null,
            };
            if (text == null) return Results.NotFound();
            return Results.Text(QrImage.Svg(text), "image/svg+xml");
        });
    }

    public static void MapPages(this WebApplication app)
    {
        var pages = Path.Combine(AppContext.BaseDirectory, "private");

        app.MapGet("/operator", (HttpContext http, OperatorAuth auth, string? key) =>
            ServePrivatePage(http, auth, key, Path.Combine(pages, "operator.html"), "/operator"));

        app.MapGet("/publish", (HttpContext http, OperatorAuth auth, LanInfo lan, string? key) =>
        {
            // Browsers refuse the camera on plain HTTP, except on localhost.
            if (!http.Request.IsHttps && http.Request.Host.Host != "localhost")
                return Results.Redirect($"https://{http.Request.Host.Host}:{http.RequestServices.GetRequiredService<AppOptions>().Network.HttpsPort}{http.Request.Path}{http.Request.QueryString}");
            var room = long.TryParse(http.Request.Query["room"], out var parsed) ? parsed : 1;
            var query = $"?room={room}&path={(http.Request.Query["path"].ToString() == "slides" ? "slides" : "cam")}";
            return ServePrivatePage(http, auth, key, Path.Combine(pages, "publish.html"), "/publish" + query);
        });

        app.MapGet("/speaker", (HttpContext http, OperatorAuth auth, string? key) =>
            ServePrivatePage(http, auth, key, Path.Combine(pages, "speaker.html"), $"/speaker?room={(long.TryParse(http.Request.Query["room"], out var room) ? room : 1)}"));

        app.MapGet("/r/{room:long}", (long room, HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-cache";
            return Results.File(Path.Combine(app.Environment.WebRootPath, "room.html"), "text/html");
        });

        foreach (var probe in CaptiveProbes)
            app.MapGet(probe, (LanInfo lan) => Results.Redirect(lan.HttpUrl + "/"));
    }

    /// <summary>Same-origin relay to MediaMTX, so phones only ever talk to one port and video stays on the LAN.</summary>
    public static void MapMediaProxy(this WebApplication app)
    {
        app.Map("/media/{**path}", async (string path, HttpContext http, IHttpClientFactory factory, OperatorAuth auth) =>
        {
            var match = MediaPath().Match(path);
            if (!match.Success) return Results.NotFound();
            var publishing = match.Groups["kind"].Value == "whip";
            var privateStream = match.Groups["stream"].Value != "cam";
            if ((publishing || privateStream) && !auth.IsOperator(http)) return Results.Unauthorized();

            using var request = new HttpRequestMessage(new HttpMethod(http.Request.Method), $"{MediaMtx.HttpBase}/{path}{http.Request.QueryString}");
            if (http.Request.ContentLength > 0 || http.Request.ContentType != null)
            {
                request.Content = new StreamContent(http.Request.Body);
                if (http.Request.ContentType != null) request.Content.Headers.TryAddWithoutValidation("Content-Type", http.Request.ContentType);
            }
            if (http.Request.Headers.IfMatch.Count > 0) request.Headers.TryAddWithoutValidation("If-Match", http.Request.Headers.IfMatch.ToString());

            using var response = await factory.CreateClient("media").SendAsync(request, http.RequestAborted);
            http.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in new[] { "ETag", "Link", "Accept-Patch", "ID" })
                if (response.Headers.TryGetValues(header, out var values)) http.Response.Headers[header] = values.ToArray();
            if (response.Headers.Location is { } location)
                http.Response.Headers.Location = location.OriginalString.StartsWith('/') ? "/media" + location.OriginalString : $"/media/{path}/{location.OriginalString}";
            if (response.Content.Headers.ContentType is { } type) http.Response.ContentType = type.ToString();
            await response.Content.CopyToAsync(http.Response.Body, http.RequestAborted);
            return Results.Empty;
        });
    }

    /// <summary>Sends unknown host names (DNS hijacked by the router's portal) to the app.</summary>
    public static void UseCaptiveRedirect(this WebApplication app)
    {
        app.Use(async (http, next) =>
        {
            var lan = http.RequestServices.GetRequiredService<LanInfo>();
            if (!http.Request.IsHttps && HttpMethods.IsGet(http.Request.Method) && !lan.IsLocalHostName(http.Request.Host.Host) && !Remote.IsRemote(http))
            {
                http.Response.Redirect(lan.HttpUrl + "/");
                return;
            }
            await next();
        });
    }

    private static async Task<IResult> WithAnswerSlotAsync<T>(HttpContext http, Func<CancellationToken, Task<T>> work)
    {
        if (!AllowAsk(Remote.ClientKey(http))) return Results.Json(new { error = "rate_limited" }, statusCode: 429);
        if (!await AnswerSlots.WaitAsync(TimeSpan.FromSeconds(2), http.RequestAborted))
            return Results.Json(new { error = "busy" }, statusCode: 503);
        try
        {
            return Results.Ok(await work(http.RequestAborted));
        }
        finally
        {
            AnswerSlots.Release();
        }
    }

    /// <summary>Each answer costs money on the operator's API key, so one phone cannot run up the bill.</summary>
    private static bool AllowAsk(string client) => Allow(client, AsksPerMinute);

    private static bool Allow(string client, int perMinute)
    {
        var now = DateTime.UtcNow;
        var log = AskLog.GetOrAdd(client, _ => new Queue<DateTime>());
        lock (log)
        {
            while (log.Count > 0 && now - log.Peek() > TimeSpan.FromMinutes(1)) log.Dequeue();
            if (log.Count >= perMinute) return false;
            log.Enqueue(now);
            return true;
        }
    }

    private static IResult ServePrivatePage(HttpContext http, OperatorAuth auth, string? key, string file, string cleanUrl)
    {
        if (auth.Matches(key))
        {
            auth.SignIn(http);
            return Results.Redirect(cleanUrl);
        }
        if (!auth.IsOperator(http))
            return Results.Content("""
                <!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
                <title>TalkLens operator</title><link rel="stylesheet" href="/app.css">
                <form class="signin" method="get"><h1>Operator access</h1>
                <label for="key">Operator key</label><input id="key" name="key" autocomplete="off" autofocus>
                <button class="primary">Continue</button><p class="muted">The key is printed in the server console.</p></form>
                """, "text/html");
        http.Response.Headers.CacheControl = "no-store";
        return Results.File(file, "text/html");
    }

    private static string WifiPayload(string ssid, string password)
    {
        static string Escape(string s) => WifiEscape().Replace(s, @"\$0");
        return password.Length > 0
            ? $"WIFI:T:WPA;S:{Escape(ssid)};P:{Escape(password)};;"
            : $"WIFI:T:nopass;S:{Escape(ssid)};;";
    }

    private static async Task<object> ListDevicesAsync(AppOptions options)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var video = Directory.Exists("/dev") ? Directory.GetFiles("/dev", "video*").Order().ToList() : [];
            return new { audio = new[] { "default" }, video };
        }

        var info = new ProcessStartInfo(options.FfmpegPath)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy" }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var devices = DshowDevice().Matches(output).Select(m => (Name: m.Groups[1].Value, Kind: m.Groups[2].Value)).ToList();
        return new
        {
            audio = devices.Where(d => d.Kind == "audio").Select(d => d.Name).ToList(),
            video = devices.Where(d => d.Kind == "video").Select(d => d.Name).ToList(),
        };
    }

    [GeneratedRegex(@"^live/r\d+/(?<stream>cam|slides)/(?<kind>whip|whep)(/[\w-]+)?$")]
    private static partial Regex MediaPath();

    [GeneratedRegex(@"^[a-z]{2}$")]
    private static partial Regex LanguageCode();

    [GeneratedRegex(@"[\\;,:""]")]
    private static partial Regex WifiEscape();

    [GeneratedRegex(@"""([^""]+)""\s+\((audio|video)\)")]
    private static partial Regex DshowDevice();
}
