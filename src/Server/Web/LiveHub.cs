using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using TalkLens.Core;
using TalkLens.Media;

namespace TalkLens.Web;

public sealed class LiveHub(Presence presence, OperatorAuth auth, AppOptions options, Store store) : Hub
{
    public const string OperatorGroup = "operators";
    public static string RoomGroup(long room) => $"room:{room}";
    public static string LanguageGroup(long room, string language) => $"room:{room}:lang:{language}";
    public static string SignGroup(long room) => $"room:{room}:signs";
    public static string ChatGroup(long talk) => $"chat:{talk}";

    public override async Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext();
        if (http != null && auth.IsOperator(http))
            await Groups.AddToGroupAsync(Context.ConnectionId, OperatorGroup);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        presence.Leave(Context.ConnectionId);
        return Task.CompletedTask;
    }

    /// <summary>Enters a room, or changes caption language in it. An empty language means the original.</summary>
    public async Task JoinRoom(long room, string language)
    {
        if (presence.Find(Context.ConnectionId) is { } before)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, RoomGroup(before.Room));
            if (before.Language.Length > 0)
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, LanguageGroup(before.Room, before.Language));
            if (before.Room != room)
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, SignGroup(before.Room));
                presence.WatchSigns(Context.ConnectionId, false);
            }
        }
        if (!options.Languages.Contains(language)) language = "";
        await Groups.AddToGroupAsync(Context.ConnectionId, RoomGroup(room));
        if (language.Length > 0) await Groups.AddToGroupAsync(Context.ConnectionId, LanguageGroup(room, language));
        presence.Join(Context.ConnectionId, room, language);
    }

    /// <summary>Whether this phone has the avatar on. Only a count: talks are only translated to signs while someone does.</summary>
    public async Task WatchSigns(bool on)
    {
        if (presence.Find(Context.ConnectionId) is not { } here) return;
        presence.WatchSigns(Context.ConnectionId, on);
        if (on) await Groups.AddToGroupAsync(Context.ConnectionId, SignGroup(here.Room));
        else await Groups.RemoveFromGroupAsync(Context.ConnectionId, SignGroup(here.Room));
    }

    /// <summary>For people who chose to register: lets their ticket record which talks they attended (RF-26).</summary>
    public void Identify(string ticket)
    {
        if (store.FindAttendeeByToken(ticket) is { } attendee) presence.Identify(Context.ConnectionId, attendee.Id);
    }

    public Task WatchChat(long talk) => Groups.AddToGroupAsync(Context.ConnectionId, ChatGroup(talk));

    /// <summary>For the operator panel: follows a room's captions without counting as an attendee.</summary>
    public async Task WatchRoom(long previous, long room)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, RoomGroup(previous));
        await Groups.AddToGroupAsync(Context.ConnectionId, RoomGroup(room));
    }
}

/// <summary>Pushes the operator panel once a second and a small public summary to attendees (RF-09).</summary>
public sealed class StatusBroadcaster(
    TalkService talks, Store store, MediaMtx media, Connectivity net, Presence presence, IHubContext<LiveHub> hub, ILogger<StatusBroadcaster> log) : BackgroundService
{
    private const int AttendanceEverySeconds = 15;
    private IReadOnlyDictionary<string, StreamInfo> _streams = new Dictionary<string, StreamInfo>();

    private TimeSpan _lastCpu;
    private DateTime _lastSample = DateTime.UtcNow;

    public object? LastStatus { get; private set; }

    public bool CameraLive(long room) => _streams.TryGetValue(MediaMtx.CameraPath(room), out var camera) && camera.Ready;

    public int Connected(long room) => presence.CountIn(room);

    /// <summary>A total only (RF-34 rule): people connected, plus those scanned at the door who are not also connected.</summary>
    public int Occupancy(long room)
    {
        var connected = presence.CountIn(room);
        return talks.Current(room) is { } talk ? connected + store.CountDoorOnly(talk.Id, presence.AttendeesIn(room)) : connected;
    }

    public bool IsFull(long room)
    {
        var limits = store.GetLimits(room);
        return limits.MarkedFull || (limits.Capacity > 0 && Occupancy(room) >= limits.Capacity);
    }

    public object Tech(long room)
    {
        var running = talks.DescribeRunning(room);
        return new { camera = CameraLive(room), captionMode = talks.CaptionMode(room), internet = net.Online, running = running != null, details = running };
    }

    /// <summary>What anyone can see about every room: the home page, which is how people choose between parallel talks.</summary>
    public object PublicSummary()
    {
        var hallways = store.ListTalks().Where(t => t.KeepAfter && t.EndedAtUnixMs is { } ended && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < ended + store.HallwayWindowHours() * 3600000L)
            .Select(t => new { t.Id, t.Title, t.Speaker }).ToList();
        var rooms = store.ListRooms().Select(room =>
        {
            var talk = talks.Latest(room.Id);
            return new
            {
                room.Id,
                room.Name,
                talk,
                live = talks.Current(room.Id) != null,
                elapsedMs = talks.ElapsedMs(room.Id),
                videoReady = CameraLive(room.Id),
                watching = presence.CountIn(room.Id),
                occupancy = Occupancy(room.Id),
                capacity = store.GetLimits(room.Id).Capacity,
                full = IsFull(room.Id),
                captionMode = talks.CaptionMode(room.Id),
            };
        }).ToList();
        return new { rooms, hallways, internet = net.Online, community = store.IsOn(Settings.Community) };
    }

    /// <summary>Sampled, so a dropped phone and a reconnect are one visit, not two.</summary>
    private void SampleAttendance()
    {
        var ms = AttendanceEverySeconds * 1000L;
        foreach (var room in store.ListRooms())
        {
            if (talks.Current(room.Id) is not { } talk) continue;
            store.AddTalkSample(talk.Id, presence.CountIn(room.Id), presence.CountIn(room.Id) * ms);
            foreach (var attendee in presence.AttendeesIn(room.Id)) store.AddWatchTime(attendee, talk.Id, ms);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var process = Process.GetCurrentProcess();
        var tick = 0;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                _streams = await media.GetStreamsAsync(stop);

                process.Refresh();
                var now = DateTime.UtcNow;
                var cpu = (process.TotalProcessorTime - _lastCpu).TotalSeconds / (now - _lastSample).TotalSeconds / Environment.ProcessorCount;
                _lastCpu = process.TotalProcessorTime;
                _lastSample = now;

                LastStatus = new
                {
                    internet = net.Online,
                    attendees = presence.Count,
                    mediaMtx = new { installed = media.Installed, running = media.Running },
                    rooms = store.ListRooms().Select(room => new
                    {
                        room.Id,
                        room.Name,
                        attendees = presence.CountIn(room.Id),
                        openQuestions = talks.Current(room.Id) is { } running ? store.CountOpenGroups(running.Id) : 0,
                        camera = _streams.GetValueOrDefault(MediaMtx.CameraPath(room.Id)),
                        slideStream = _streams.GetValueOrDefault(MediaMtx.SlidesPath(room.Id)),
                        talk = talks.DescribeRunning(room.Id),
                    }).ToList(),
                    server = new { cpuPercent = Math.Round(cpu * 100, 1), memoryMb = process.WorkingSet64 / (1024 * 1024) },
                };
                await hub.Clients.Group(LiveHub.OperatorGroup).SendAsync("status", LastStatus, stop);

                if (tick % AttendanceEverySeconds == 0) SampleAttendance();
                if (tick++ % 3 == 0)
                {
                    await hub.Clients.All.SendAsync("rooms", PublicSummary(), stop);
                }
                await Task.Delay(1000, stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                // A status glitch must never take a live talk down with it.
                log.LogError(e, "Status update failed");
                try { await Task.Delay(1000, stop); } catch (OperationCanceledException) { return; }
            }
        }
    }
}
