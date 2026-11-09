using System.Collections.Concurrent;
using System.IO.Compression;
using Microsoft.AspNetCore.SignalR;
using TalkLens.Core;
using TalkLens.Media;

namespace TalkLens.Web;

public sealed record ReactRequest(string? Voter, string? Kind);
public sealed record VoterRequest(string? Voter);
public sealed record PhotoStatusRequest(string? Status);

/// <summary>Reactions, the photo wall and what the room's big screen needs.</summary>
public static partial class Endpoints
{
    // A tap every half second is plenty to show on the screen, and keeps one phone from flooding it.
    private const long ReactionGapMs = 500;
    private const int ReactionsPerMinute = 120;
    private const int PhotoActionsPerMinute = 30;
    private static readonly ConcurrentDictionary<string, long> LastReaction = new();

    /// <summary>What anyone sees of a photo. Who sent it never leaves the server; a phone only learns which ones are its own.</summary>
    private static object PhotoView(Photo photo, string? voter = null) => new
    {
        photo.Id, photo.RoomId, photo.TalkId, photo.At, photo.Alt, photo.Likes, photo.Status,
        mine = voter != null && photo.Voter == voter,
    };

    private static string PhotoUrl(Photo photo) => $"/photos/{photo.Id}.jpg";

    /// <summary>Approved photos go to every screen and phone; anything else leaves them.</summary>
    public static Task PhotoChanged(IHubContext<LiveHub> hub, Photo photo) => Task.WhenAll(
        photo.Status == "approved"
            ? hub.Clients.All.SendAsync("photo", PhotoView(photo))
            : hub.Clients.All.SendAsync("photoRemoved", photo.Id),
        hub.Clients.Group(LiveHub.OperatorGroup).SendAsync("photos"));

    public static void MapWallApi(this WebApplication app)
    {
        // ----- Reactions: anonymous taps, kept with the moment of the talk they belong to -----
        app.MapPost("/api/rooms/{room:long}/react", async (long room, ReactRequest request, HttpContext http, Store store, TalkService talks, IHubContext<LiveHub> hub) =>
        {
            if (!ValidVoter(request.Voter) || !Reports.ReactionKinds.Contains(request.Kind)) return Results.BadRequest();
            if (talks.Current(room) is not { } talk) return Results.Conflict(new { error = "not live" });
            if (!Allow("react:" + Remote.ClientKey(http), ReactionsPerMinute)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            var now = Environment.TickCount64;
            var key = $"{room}:{request.Voter}";
            if (LastReaction.TryGetValue(key, out var last) && now - last < ReactionGapMs) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            LastReaction[key] = now;
            store.AddReaction(talk.Id, talks.ElapsedMs(room), request.Kind!, request.Voter!);
            await hub.Clients.Group(LiveHub.RoomGroup(room)).SendAsync("reaction", new { kind = request.Kind });
            return Results.Ok();
        });

        app.MapGet("/api/talks/{id:long}/moments", (long id, HttpContext http, Store store, TalkService talks, Reports reports) =>
        {
            if (store.FindTalk(id) is not { } talk) return Results.NotFound();
            if (!CanRead(http, store, talks, talk)) return Locked();
            return Results.Ok(new { peaks = reports.Reactions(talk, store.MinimumParticipants())?.Peaks ?? [] });
        });

        // ----- The photo wall -----
        app.MapGet("/api/photos", (string? voter, Store store) =>
        {
            voter = ValidVoter(voter) ? voter : null;
            var photos = store.ListPhotos().Where(p => p.Status == "approved" || (voter != null && p.Voter == voter && p.Status != "rejected")).ToList();
            var talkIds = photos.Where(p => p.Status == "approved" && p.TalkId != null).Select(p => p.TalkId!.Value).ToHashSet();
            var liked = voter == null ? [] : store.LikedBy(voter).ToHashSet();
            return Results.Ok(new
            {
                photos = photos.Select(p => new { photo = PhotoView(p, voter), url = PhotoUrl(p) + (p.Status == "approved" ? "" : $"?v={voter}"), liked = liked.Contains(p.Id) }),
                talks = store.ListTalks().Where(t => talkIds.Contains(t.Id)).Select(t => new { t.Id, t.Title, t.Speaker }),
            });
        });

        // Approved photos are public. The sender and the operator can also see one that waits.
        app.MapGet("/photos/{id:long}.jpg", (long id, string? v, HttpContext http, Store store, OperatorAuth auth) =>
        {
            if (store.FindPhoto(id) is not { } photo || !File.Exists(PhotoWall.FilePath(id))) return Results.NotFound();
            var allowed = photo.Status == "approved" || auth.IsOperator(http) || (ValidVoter(v) && photo.Voter == v);
            return allowed ? Results.File(PhotoWall.FilePath(id), "image/jpeg") : Results.NotFound();
        });

        app.MapPost("/api/rooms/{room:long}/photos", async (long room, string? voter, HttpContext http, Store store, TalkService talks, PhotoWall wall, IHubContext<LiveHub> hub) =>
        {
            if (!ValidVoter(voter)) return Results.BadRequest(new { error = "voter" });
            if (store.ListRooms().All(r => r.Id != room)) return Results.NotFound();
            if (http.Request.ContentLength is not { } length || length > PhotoWall.MaxBytes) return Results.BadRequest(new { error = "size" });
            if (!Allow("photo:" + Remote.ClientKey(http), PhotoActionsPerMinute)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            if (store.CountPhotosSince(voter!, DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds()) >= PhotoWall.PerHour)
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            using var buffer = new MemoryStream();
            await http.Request.Body.CopyToAsync(buffer, http.RequestAborted);
            var bytes = buffer.ToArray();
            if (bytes.Length > PhotoWall.MaxBytes || !PhotoWall.IsJpeg(bytes)) return Results.BadRequest(new { error = "jpeg" });
            var photo = await wall.AddAsync(room, talks.Current(room)?.Id, voter!, bytes);
            await hub.Clients.Group(LiveHub.OperatorGroup).SendAsync("photos");
            return Results.Ok(new { photo = PhotoView(photo, voter), url = PhotoUrl(photo) + $"?v={voter}" });
        });

        app.MapPost("/api/photos/{id:long}/like", async (long id, VoterRequest request, HttpContext http, Store store, IHubContext<LiveHub> hub) =>
        {
            if (!ValidVoter(request.Voter)) return Results.BadRequest();
            if (!Allow("like:" + Remote.ClientKey(http), VotesPerMinute)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            if (store.FindPhoto(id) is not { Status: "approved" }) return Results.NotFound();
            var liked = store.ToggleLike(id, request.Voter!);
            var likes = store.FindPhoto(id)!.Likes;
            await hub.Clients.All.SendAsync("photoLikes", new { id, likes });
            return Results.Ok(new { liked, likes });
        });

        // Anyone in a photo can take it down at once. It waits for the operator, who can put it back.
        app.MapPost("/api/photos/{id:long}/report", async (long id, VoterRequest request, HttpContext http, Store store, IHubContext<LiveHub> hub) =>
        {
            if (!ValidVoter(request.Voter)) return Results.BadRequest();
            if (!Allow("report:" + Remote.ClientKey(http), PhotoActionsPerMinute)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            if (store.FindPhoto(id) is not { Status: "approved" }) return Results.NotFound();
            store.SetPhotoStatus(id, "reported");
            await PhotoChanged(hub, store.FindPhoto(id)!);
            return Results.Ok();
        });

        app.MapDelete("/api/photos/{id:long}", async (long id, string? voter, Store store, PhotoWall wall, IHubContext<LiveHub> hub) =>
        {
            if (!ValidVoter(voter) || store.FindPhoto(id) is not { } photo || photo.Voter != voter) return Results.NotFound();
            wall.Delete(id);
            await PhotoChanged(hub, photo with { Status = "deleted" });
            return Results.Ok();
        });

        app.MapGet("/api/photos.zip", async (HttpContext http, Store store) =>
        {
            http.Response.ContentType = "application/zip";
            http.Response.Headers.ContentDisposition = "attachment; filename=\"event-photos.zip\"";
            var titles = store.ListTalks().ToDictionary(t => t.Id, t => t.Title);
            // Written straight to the response: a big album never sits in memory.
            await using var body = http.Response.BodyWriter.AsStream();
            using var zip = new ZipArchive(body, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var photo in store.ListPhotos("approved").OrderBy(p => p.Id))
            {
                if (!File.Exists(PhotoWall.FilePath(photo.Id))) continue;
                var folder = photo.TalkId is { } talk && titles.TryGetValue(talk, out var title) ? SafeName(title) : "Event";
                var entry = zip.CreateEntry($"{folder}/{photo.Id}.jpg", CompressionLevel.NoCompression);
                await using var target = entry.Open();
                await using var source = File.OpenRead(PhotoWall.FilePath(photo.Id));
                await source.CopyToAsync(target, http.RequestAborted);
            }
        });

        // ----- The big screen: a QR to join this room, which anyone in the room can already see -----
        app.MapGet("/api/rooms/{room:long}/qr.svg", (long room, LanInfo lan) =>
            Results.Text(QrImage.Svg($"{lan.HttpUrl}/r/{room}"), "image/svg+xml"));
        app.MapGet("/api/rooms/{room:long}/join", (long room, LanInfo lan) => new { url = $"{lan.HttpUrl}/r/{room}" });

        app.MapGet("/screen/{room:long}", (long room, HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-cache";
            return Results.File(Path.Combine(app.Environment.WebRootPath, "screen.html"), "text/html");
        });
    }

    private static string SafeName(string title)
    {
        var clean = new string(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c).ToArray()).Trim();
        return clean.Length == 0 ? "Talk" : clean.Length > 60 ? clean[..60] : clean;
    }

    public static void MapWallOperatorApi(this RouteGroupBuilder op)
    {
        op.MapGet("/photos", (Store store) =>
        {
            var titles = store.ListTalks().ToDictionary(t => t.Id, t => t.Title);
            return store.ListPhotos().Where(p => p.Status != "rejected").Select(p => new
            {
                p.Id, p.Status, p.Alt, p.Flag, p.Likes, p.At, url = PhotoUrl(p),
                talk = p.TalkId is { } talk ? titles.GetValueOrDefault(talk) : null,
            });
        });

        op.MapPost("/photos/{id:long}/status", async (long id, PhotoStatusRequest request, Store store, IHubContext<LiveHub> hub) =>
        {
            if (request.Status is not ("approved" or "rejected")) return Results.BadRequest(new { error = "approved or rejected" });
            if (store.FindPhoto(id) is null) return Results.NotFound();
            store.SetPhotoStatus(id, request.Status);
            await PhotoChanged(hub, store.FindPhoto(id)!);
            return Results.Ok();
        });

        op.MapDelete("/photos/{id:long}", async (long id, Store store, PhotoWall wall, IHubContext<LiveHub> hub) =>
        {
            if (store.FindPhoto(id) is not { } photo) return Results.NotFound();
            wall.Delete(id);
            await PhotoChanged(hub, photo with { Status = "deleted" });
            return Results.Ok();
        });
    }
}
