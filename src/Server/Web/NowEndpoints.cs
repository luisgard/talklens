using Microsoft.AspNetCore.SignalR;
using TalkLens.Core;

namespace TalkLens.Web;

public sealed record AnnounceRequest(string? Kind);

/// <summary>The organizer's "Now" cards, and the one-tap announcement they can put on every screen and phone.</summary>
public static partial class Endpoints
{
    public const string IntroRound = "intro-round";
    private static readonly string[] AnnouncementKinds = [IntroRound];
    private const int MaxNamesShown = 8;

    public static void MapNowOperatorApi(this RouteGroupBuilder op)
    {
        op.MapGet("/now", (Store store, LanInfo lan, Announcements announcements) =>
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var state = NowBoard.Compute(store, now);
            return new
            {
                visible = state.Visible,
                alone = new { count = state.Alone.Count, names = state.Alone.Take(MaxNamesShown) },
                sponsorsWaiting = state.SponsorsWaiting.Select(w => new { w.Id, w.Sponsor, w.Person, w.Slot, w.Minutes, link = $"{lan.HttpUrl}/sponsor/{w.SponsorKey}" }),
                waitMinutes = (int)NowBoard.SponsorWait.TotalMinutes,
                notConfirmed = state.NotConfirmed,
                announcement = Describe(announcements.Current(now)),
            };
        });

        op.MapPost("/announce", async (AnnounceRequest request, Store store, Announcements announcements, IHubContext<LiveHub> hub) =>
        {
            if (!AnnouncementKinds.Contains(request.Kind)) return Results.BadRequest(new { error = "kind" });
            var started = announcements.Start(request.Kind!, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await Announce(store, hub, Describe(started));
            return Results.Ok(Describe(started));
        });

        op.MapDelete("/announce", async (Store store, Announcements announcements, IHubContext<LiveHub> hub) =>
        {
            announcements.Stop();
            await Announce(store, hub, null);
            return Results.Ok();
        });
    }

    public static void MapNowApi(this WebApplication app) =>
        // For a screen or phone that opens while an announcement is on.
        app.MapGet("/api/announcement", (Announcements announcements) =>
            Results.Ok(new { announcement = Describe(announcements.Current(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())) }));

    private static object? Describe((string Kind, long Until)? announcement) =>
        announcement is { } a ? new { kind = a.Kind, until = a.Until } : null;

    // Every room: screens and phones both listen to their room.
    private static Task Announce(Store store, IHubContext<LiveHub> hub, object? announcement) =>
        hub.Clients.Groups(store.ListRooms().Select(r => LiveHub.RoomGroup(r.Id)).ToList()).SendAsync("announcement", announcement);
}
