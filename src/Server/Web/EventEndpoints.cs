using System.Text;
using System.Text.RegularExpressions;
using TalkLens.Ai;
using TalkLens.Core;
using TalkLens.Media;

namespace TalkLens.Web;

public sealed record RegisterRequest(string Name, string Email);
public sealed record NetworkingRequest(bool Networking, string? Interests);
public sealed record ProfileRequest(string? Name, string? Seeking, string? Offering, bool Networking, string? Email, bool WantsSummary);
public sealed record ContactRequest(string Code, long? Room);
public sealed record EventRequest(string? Name, string? Venue, string? Dates, string? About, int? MinParticipants, int? HallwayHours, string? LibraryCode,
    bool? Community, bool? RemoteEnabled, bool? RemoteVideo, string? RemoteUrl, bool? SignAlways = null, bool? PhotosAuto = null, string? MeetingSpots = null);
public sealed record AgendaRequest(long RoomId, string Title, string? Speaker, long StartsAt, long EndsAt, string? About);
public sealed record LimitsRequest(int Capacity, bool MarkedFull);
public sealed record AttendeeRequest(string Name, string Email);
public sealed record CheckInRequest(string Code, long? Room);
public sealed record LumaRequest(string EventId);
public sealed record SponsorRequest(string Name, string? Keywords);

/// <summary>The event layer: agenda, optional registration, tickets, door check-in, contacts and sponsors (RF-22 to RF-27, RF-31, RF-36, RF-37).</summary>
public static partial class Endpoints
{
    private const string TicketHeader = "X-Ticket";
    private const int MaxNameChars = 80;
    private const int RegistrationsPerMinute = 10;
    private const int CodeScansPerMinute = 20;
    private static readonly TimeSpan ContactCodeLife = TimeSpan.FromMinutes(5);

    private static Attendee? Me(HttpContext http, Store store) => store.FindAttendeeByToken(http.Request.Headers[TicketHeader].ToString());

    private static string TicketUrl(LanInfo lan, Attendee attendee) => $"{lan.HttpUrl}/ticket/{attendee.Token}";

    private static string TicketMail(Store store, Attendee attendee) => $"""
        Hi {attendee.Name},

        You are registered for {store.GetSetting(Settings.EventName, "the event")}. Show this QR code at the door and at each talk.

        You can keep using the event without it. No account or password is needed.
        """;

    private static void QueueTicket(Store store, LanInfo lan, Attendee attendee) =>
        store.QueueMail(attendee.Email, $"Your ticket for {store.GetSetting(Settings.EventName, "the event")}", TicketMail(store, attendee), TicketUrl(lan, attendee));

    private static object Publicly(Attendee a) => new { a.Id, a.Name, a.Email, a.CheckedInAt, a.Networking, a.Interests, a.Seeking, a.Offering, a.WantsSummary };

    public static void MapEventApi(this WebApplication app)
    {
        app.MapGet("/api/event", (Store store, StatusBroadcaster status, Mailer mailer) => new
        {
            name = store.GetSetting(Settings.EventName),
            venue = store.GetSetting(Settings.Venue),
            dates = store.GetSetting(Settings.Dates),
            about = store.GetSetting(Settings.About),
            agenda = store.ListAgenda(),
            summary = status.PublicSummary(),
            community = store.IsOn(Settings.Community),
            libraryLocked = store.GetSetting(Settings.LibraryCode).Length > 0,
            mailConfigured = mailer.Configured,
        });

        // RF-23. Answering an existing email the same way as a wrong one would still say who registered, so it says only "check your mail".
        app.MapPost("/api/register", (RegisterRequest request, HttpContext http, Store store, LanInfo lan, Mailer mailer) =>
        {
            var (name, email) = (request.Name?.Trim() ?? "", request.Email?.Trim() ?? "");
            if (name.Length is 0 or > MaxNameChars) return Results.BadRequest(new { error = "name" });
            if (!Csv.IsEmail(email)) return Results.BadRequest(new { error = "email" });
            if (!Allow("register:" + Remote.ClientKey(http), RegistrationsPerMinute)) return Results.Json(new { error = "rate_limited" }, statusCode: 429);

            var (attendee, created) = store.UpsertAttendee(name, email, "self");
            QueueTicket(store, lan, attendee);
            // Only the person who just created the ticket gets its secret. Anyone can type someone else's email.
            return created
                ? Results.Ok(new { token = attendee.Token, mailed = mailer.Configured })
                : Results.Ok(new { existing = true, mailed = mailer.Configured });
        });

        app.MapGet("/api/me", (HttpContext http, Store store, TalkService talks, LanInfo lan) =>
        {
            if (Me(http, store) is not { } me) return Results.Unauthorized();
            var names = store.ListTalks().ToDictionary(t => t.Id, t => t.Title);
            return Results.Ok(new
            {
                me = Publicly(me),
                ticketUrl = TicketUrl(lan, me),
                ticketQr = QrImage.Svg(TicketUrl(lan, me)),
                talks = store.ListAttendanceOf(me.Id).Where(a => names.ContainsKey(a.TalkId)).Select(a => new { talkId = a.TalkId, title = names[a.TalkId], minutes = a.WatchMs / 60000 }),
                contacts = ContactList(store, me),
                sponsors = store.ListLeadSponsorsOf(me.Id).Select(id => store.ListSponsors().FirstOrDefault(s => s.Id == id)).OfType<Sponsor>().Select(s => new { s.Code, s.Name }),
            });
        });

        // The person can leave at any moment, taking their data with them.
        app.MapDelete("/api/me", (HttpContext http, Store store) =>
        {
            if (Me(http, store) is not { } me) return Results.Unauthorized();
            store.DeleteAttendee(me.Id);
            return Results.Ok();
        });

        app.MapPut("/api/me/networking", (NetworkingRequest request, HttpContext http, Store store) =>
        {
            if (Me(http, store) is not { } me) return Results.Unauthorized();
            store.SetNetworking(me.Id, request.Networking, (request.Interests ?? "").Trim()[..Math.Min((request.Interests ?? "").Trim().Length, 200)]);
            return Results.Ok();
        });

        // One entry for everyone: a phone without a ticket gets a profile on its first save, and keeps it with the token it is sent.
        app.MapPut("/api/me/profile", (ProfileRequest request, HttpContext http, Store store) =>
        {
            var name = request.Name?.Trim() ?? "";
            var email = request.Email?.Trim() ?? "";
            if (name.Length is 0 or > MaxNameChars) return Results.BadRequest(new { error = "name" });
            if (email.Length > 0 && !Csv.IsEmail(email)) return Results.BadRequest(new { error = "email" });
            var me = Me(http, store);
            if (me == null && !Allow("register:" + Remote.ClientKey(http), RegistrationsPerMinute)) return Results.Json(new { error = "rate_limited" }, statusCode: 429);
            me ??= store.AddProfile(name);
            var (seeking, offering) = (Shorten(request.Seeking), Shorten(request.Offering));
            // An email someone else already has is left as it was, and the rest is still saved.
            var taken = !store.SetProfile(me.Id, name, seeking, offering, request.Networking, email, request.WantsSummary);
            if (taken) store.SetProfile(me.Id, name, seeking, offering, request.Networking, me.Email, request.WantsSummary);
            return Results.Ok(new { token = me.Token, me = Publicly(store.FindAttendee(me.Id)!), emailTaken = taken });
        });

        // RF-36. A code of its own that expires, because the ticket QR opens the ticket and a photo of it should not.
        app.MapPost("/api/me/contact-code", (HttpContext http, Store store, LanInfo lan) =>
        {
            if (Me(http, store) is not { } me) return Results.Unauthorized();
            var code = Store.NewToken()[..16];
            store.SetContactCode(me.Id, code, DateTimeOffset.UtcNow.Add(ContactCodeLife).ToUnixTimeMilliseconds());
            var url = $"{lan.HttpUrl}/connect/{code}";
            return Results.Ok(new { code, url, svg = QrImage.Svg(url), seconds = (int)ContactCodeLife.TotalSeconds });
        });

        app.MapGet("/api/contacts/preview/{code}", (string code, HttpContext http, Store store) =>
        {
            if (Me(http, store) is null) return Results.Unauthorized();
            if (!Allow("code:" + Remote.ClientKey(http), CodeScansPerMinute)) return Results.Json(new { error = "rate_limited" }, statusCode: 429);
            return store.FindAttendeeByContactCode(code) is { } other ? Results.Ok(new { firstName = FirstName(other.Name) }) : Results.NotFound();
        });

        app.MapPost("/api/contacts", (ContactRequest request, HttpContext http, Store store, TalkService talks) =>
        {
            if (Me(http, store) is not { } me) return Results.Unauthorized();
            if (!Allow("code:" + Remote.ClientKey(http), CodeScansPerMinute)) return Results.Json(new { error = "rate_limited" }, statusCode: 429);
            if (store.FindAttendeeByContactCode(request.Code ?? "") is not { } other) return Results.NotFound();
            if (other.Id == me.Id) return Results.BadRequest(new { error = "self" });
            var talk = request.Room is { } room ? talks.Latest(room) : null;
            store.AddContact(me.Id, other.Id, talk?.Title ?? "");
            // One person per code: a photo of the screen must not keep working for the rest of its 5 minutes.
            store.SetContactCode(other.Id, "", 0);
            // Scanning each other in person is what turns an accepted request into a meeting.
            var met = store.MarkMet(me.Id, other.Id);
            return Results.Ok(new { contacts = ContactList(store, me), met });
        });

        app.MapDelete("/api/contacts/{id:long}", (long id, HttpContext http, Store store) =>
        {
            if (Me(http, store) is not { } me) return Results.Unauthorized();
            store.DeleteContact(me.Id, id);
            return Results.Ok();
        });

        // RF-37. Scanning only shows the question. Nothing is shared until the person accepts.
        app.MapGet("/api/stands/{code}", (string code, Store store) =>
            store.FindSponsorByCode(code) is { } sponsor ? Results.Ok(new { sponsor.Name }) : Results.NotFound());

        app.MapPost("/api/stands/{code}/share", (string code, HttpContext http, Store store) =>
        {
            if (Me(http, store) is not { } me) return Results.Unauthorized();
            if (store.FindSponsorByCode(code) is not { } sponsor) return Results.NotFound();
            store.AddLead(sponsor.Id, me.Id);
            return Results.Ok();
        });

        app.MapDelete("/api/stands/{code}/share", (string code, HttpContext http, Store store) =>
        {
            if (Me(http, store) is not { } me) return Results.Unauthorized();
            if (store.FindSponsorByCode(code) is not { } sponsor) return Results.NotFound();
            store.DeleteLead(sponsor.Id, me.Id);
            return Results.Ok();
        });

        // The sponsor's own link: only people who accepted, with the related talks they attended.
        app.MapGet("/api/sponsor/{key}", (string key, Store store, Reports reports) =>
        {
            if (store.FindSponsorByKey(key) is not { } sponsor) return Results.NotFound();
            var terms = Reports.Terms(sponsor.Keywords.Length > 0 ? sponsor.Keywords : sponsor.Name);
            var related = store.ListTalks().Where(t => reports.FindMentions(t, terms).Count > 0).ToDictionary(t => t.Id, t => t.Title);
            return Results.Ok(new
            {
                report = reports.SponsorReport(sponsor),
                leads = store.ListLeads(sponsor.Id).Select(l => new
                {
                    l.Name,
                    l.Email,
                    l.At,
                    talks = store.ListAttendanceOf(l.AttendeeId).Where(a => related.ContainsKey(a.TalkId)).Select(a => related[a.TalkId]),
                }),
            });
        });

        // RF-32. Only people who opted in appear, only to others who opted in, and without their email.
        app.MapGet("/api/community/people", (HttpContext http, Store store) =>
        {
            if (!store.IsOn(Settings.Community)) return Results.NotFound();
            if (Me(http, store) is not { } me) return Results.Unauthorized();
            if (!me.Networking) return Results.Ok(new { optedIn = false, people = Array.Empty<object>() });
            var mine = Interests(me.Interests);
            var people = store.ListAttendees()
                .Where(a => a.Networking && a.Id != me.Id)
                .Select(a => (Person: a, Shared: Interests(a.Interests).Where(mine.Contains).ToList()))
                .Where(p => p.Shared.Count > 0)
                .OrderByDescending(p => p.Shared.Count)
                .Select(p => new { name = FirstName(p.Person.Name), shared = p.Shared });
            return Results.Ok(new { optedIn = true, people });
        });
    }

    private const int MaxProfileChars = 200;

    private static string Shorten(string? text) => (text ?? "").Trim() is var t && t.Length > MaxProfileChars ? t[..MaxProfileChars] : t;

    private static string FirstName(string name) => name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? name;

    private static List<string> Interests(string text) =>
        text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(i => i.ToLowerInvariant()).Distinct().ToList();

    /// <summary>Context stays between the two people: nothing else lists it (RF-36 rule).</summary>
    private static object ContactList(Store store, Attendee me) => store.ListContacts(me.Id)
        .Select(c => (Row: c, Other: store.FindAttendee(c.A == me.Id ? c.B : c.A)))
        .Where(c => c.Other != null)
        .Select(c => new { c.Row.Id, name = c.Other!.Name, email = c.Other.Email, context = c.Row.Context, at = c.Row.At })
        .ToList();

    public static void MapEventOperatorApi(this RouteGroupBuilder op)
    {
        op.MapGet("/event", (Store store, Mailer mailer) => new
        {
            name = store.GetSetting(Settings.EventName),
            venue = store.GetSetting(Settings.Venue),
            dates = store.GetSetting(Settings.Dates),
            about = store.GetSetting(Settings.About),
            minParticipants = store.MinimumParticipants(),
            hallwayHours = store.HallwayWindowHours(),
            libraryCode = store.GetSetting(Settings.LibraryCode),
            community = store.IsOn(Settings.Community),
            remoteEnabled = store.IsOn(Settings.RemoteEnabled),
            remoteVideo = store.IsOn(Settings.RemoteVideo),
            remoteUrl = store.GetSetting(Settings.RemoteUrl),
            signAlways = store.IsOn(Settings.SignAlways),
            photosAuto = store.IsOn(Settings.PhotosAuto),
            meetingSpots = store.GetSetting(Settings.MeetingSpots),
            apiKey = store.GetSetting(Settings.ApiKey),
            lumaEventId = store.GetSetting(Settings.LumaEventId),
            lumaKey = LumaClient.ApiKey != null,
            mailConfigured = mailer.Configured,
            mail = new { pending = store.MailCounts().Pending, sent = store.MailCounts().Sent },
        });

        op.MapPut("/event", (EventRequest request, Store store) =>
        {
            void Text(string key, string? value) { if (value != null) store.SetSetting(key, value.Trim()); }
            void Flag(string key, bool? value) { if (value != null) store.SetSetting(key, value.Value ? "1" : "0"); }
            Text(Settings.EventName, request.Name);
            Text(Settings.Venue, request.Venue);
            Text(Settings.Dates, request.Dates);
            Text(Settings.About, request.About);
            Text(Settings.LibraryCode, request.LibraryCode);
            Text(Settings.RemoteUrl, request.RemoteUrl);
            Flag(Settings.Community, request.Community);
            Flag(Settings.RemoteEnabled, request.RemoteEnabled);
            Flag(Settings.RemoteVideo, request.RemoteVideo);
            Flag(Settings.SignAlways, request.SignAlways);
            Flag(Settings.PhotosAuto, request.PhotosAuto);
            Text(Settings.MeetingSpots, request.MeetingSpots);
            if (request.MinParticipants is { } minimum)
            {
                if (minimum < 1) return Results.BadRequest(new { error = "The minimum must be at least 1." });
                store.SetSetting(Settings.MinParticipants, minimum.ToString());
            }
            if (request.HallwayHours is { } hours)
            {
                if (hours < 0) return Results.BadRequest(new { error = "Hours cannot be negative." });
                store.SetSetting(Settings.HallwayHours, hours.ToString());
            }
            return Results.Ok();
        });

        op.MapDelete("/api-key", (Store store) =>
        {
            store.SetSetting(Settings.ApiKey, "");
            return Results.NoContent();
        });

        op.MapPost("/api-key", (Store store) =>
        {
            var key = Store.NewToken();
            store.SetSetting(Settings.ApiKey, key);
            return new { key };
        });

        op.MapPost("/agenda", (AgendaRequest request, Store store) =>
        {
            var title = request.Title?.Trim() ?? "";
            if (title.Length == 0) return Results.BadRequest(new { error = "Give the talk a title." });
            if (store.ListRooms().All(r => r.Id != request.RoomId)) return Results.BadRequest(new { error = "Pick a room." });
            if (request.EndsAt <= request.StartsAt) return Results.BadRequest(new { error = "The talk must end after it starts." });
            return Results.Ok(store.AddAgenda(request.RoomId, title, request.Speaker?.Trim() ?? "", request.StartsAt, request.EndsAt, request.About?.Trim() ?? ""));
        });
        op.MapDelete("/agenda/{id:long}", (long id, Store store) => { store.DeleteAgenda(id); return Results.Ok(); });

        op.MapPut("/rooms/{room:long}/limits", (long room, LimitsRequest request, Store store) =>
        {
            if (request.Capacity < 0) return Results.BadRequest(new { error = "Capacity cannot be negative." });
            store.SetLimits(room, request.Capacity, request.MarkedFull);
            return Results.Ok();
        });

        op.MapGet("/search-engine", (ElasticIndex elastic) => new { elastic = elastic.Ready, moments = elastic.MomentCount });

        op.MapGet("/attendees", (Store store) => store.ListAttendees().Select(a => new { a.Id, a.Name, a.Email, a.CheckedInAt, a.Source }));

        op.MapPost("/attendees", (AttendeeRequest request, Store store, LanInfo lan) =>
        {
            var (name, email) = (request.Name?.Trim() ?? "", request.Email?.Trim() ?? "");
            if (name.Length is 0 or > MaxNameChars || !Csv.IsEmail(email)) return Results.BadRequest(new { error = "Enter a name and a valid email." });
            var (attendee, created) = store.UpsertAttendee(name, email, "manual");
            if (created) QueueTicket(store, lan, attendee);
            return Results.Ok(new { attendee.Id, created });
        });

        // The organizer's own desk can show a ticket, for someone with no mail in the venue.
        op.MapGet("/attendees/{id:long}/ticket", (long id, Store store, LanInfo lan) =>
            store.FindAttendee(id) is { } attendee ? Results.Ok(new { url = TicketUrl(lan, attendee), svg = QrImage.Svg(TicketUrl(lan, attendee)) }) : Results.NotFound());

        op.MapDelete("/attendees/{id:long}", (long id, Store store) => { store.DeleteAttendee(id); return Results.Ok(); });
        op.MapDelete("/attendees", (Store store) => { store.DeleteAllAttendees(); return Results.Ok(); });

        op.MapPost("/import/csv", async (HttpContext http, Store store, LanInfo lan) =>
        {
            using var body = new MemoryStream();
            await http.Request.Body.CopyToAsync(body);
            var file = Csv.ReadPeople(Csv.Decode(body.ToArray()));
            if (!file.HasEmailColumn) return Results.BadRequest(new { error = "This file has no email column. Its first row should name the columns, like: name, email. You can download our template and copy your list into it." });
            if (file.People.Count == 0) return Results.BadRequest(new { error = "This file has no people with an email address." });
            var people = file.People;
            var created = 0;
            foreach (var person in people)
            {
                var (attendee, isNew) = store.UpsertAttendee(person.Name, person.Email, "csv", person.LumaId, person.LumaQr, person.CheckedInAt);
                if (!isNew) continue;
                created++;
                // People imported from Luma already got Luma's ticket, and its QR works at the door.
                if (person.LumaQr.Length == 0 && person.LumaId.Length == 0) QueueTicket(store, lan, attendee);
            }
            return Results.Ok(new { total = people.Count, created, noEmail = file.NoEmail, notComing = file.NotComing });
        });

        op.MapGet("/import/template.csv", () => Results.File(Encoding.UTF8.GetBytes("name,email\n"), "text/csv", "guest-list-template.csv"));

        op.MapPost("/import/luma", async (LumaRequest request, Store store, HttpClient http, CancellationToken ct) =>
        {
            var eventId = request.EventId?.Trim() ?? "";
            if (eventId.Length == 0) return Results.BadRequest(new { error = "Enter the Luma event ID." });
            if (LumaClient.ApiKey is not { } key) return Results.BadRequest(new { error = "Add LUMA_API_KEY to the .env file and restart. The API needs a Luma Plus plan; otherwise import Luma's CSV." });
            store.SetSetting(Settings.LumaEventId, eventId);
            try
            {
                var luma = new LumaClient(http, Environment.GetEnvironmentVariable("LUMA_BASE_URL") ?? "https://public-api.luma.com");
                var details = await luma.GetEventAsync(eventId, key, ct);
                if (details.Name.Length > 0) store.SetSetting(Settings.EventName, details.Name);
                if (details.Venue.Length > 0) store.SetSetting(Settings.Venue, details.Venue);
                if (details.Dates.Length > 0) store.SetSetting(Settings.Dates, details.Dates);
                var guests = await luma.GetGuestsAsync(eventId, key, ct);
                var created = guests.Where(g => Csv.IsEmail(g.Email)).Count(g => store.UpsertAttendee(g.Name.Length > 0 ? g.Name : g.Email.Split('@')[0], g.Email, "luma", g.Id, g.Qr, g.CheckedInAt).Created);
                return Results.Ok(new { total = guests.Count, created, eventName = details.Name });
            }
            catch (Exception e) when (e is InvalidOperationException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException)
            {
                return Results.BadRequest(new { error = e is InvalidOperationException ? e.Message : "Could not read from Luma. Check the internet connection and the event ID." });
            }
        });

        op.MapGet("/export/attendees.csv", (Store store) =>
        {
            var csv = new StringBuilder(Csv.Row("name", "email", "checked_in_at", "source") + "\n");
            foreach (var a in store.ListAttendees())
                csv.Append(Csv.Row(a.Name, a.Email, a.CheckedInAt is { } at ? DateTimeOffset.FromUnixTimeMilliseconds(at).ToString("u") : "", a.Source)).Append('\n');
            return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", "attendees.csv");
        });

        op.MapGet("/export/attendance.csv", (Store store) =>
        {
            var people = store.ListAttendees().ToDictionary(a => a.Id);
            var talks = store.ListTalks().ToDictionary(t => t.Id);
            var csv = new StringBuilder(Csv.Row("name", "email", "talk", "first_seen", "last_seen", "minutes_watching") + "\n");
            foreach (var row in store.ListAttendance())
                if (people.TryGetValue(row.AttendeeId, out var person) && talks.TryGetValue(row.TalkId, out var talk))
                    csv.Append(Csv.Row(person.Name, person.Email, talk.Title, DateTimeOffset.FromUnixTimeMilliseconds(row.FirstSeen).ToString("u"),
                        DateTimeOffset.FromUnixTimeMilliseconds(row.LastSeen).ToString("u"), row.WatchMs / 60000)).Append('\n');
            return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", "attendance.csv");
        });

        // RF-25. Reads only the local database, so it works with no internet.
        op.MapPost("/checkin", (CheckInRequest request, Store store, TalkService talks) =>
        {
            var attendee = FindByScan(store, request.Code ?? "");
            if (attendee == null) return Results.Ok(new { status = "unknown" });
            if (request.Room is { } room && room > 0)
            {
                if (attendee.CheckedInAt == null) store.SetCheckedIn(attendee.Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                if (talks.Current(room) is { } talk) store.AddDoorScan(attendee.Id, room, talk.Id);
                return Results.Ok(new { status = "ok", attendee.Id, attendee.Name });
            }
            if (attendee.CheckedInAt != null) return Results.Ok(new { status = "already", attendee.Id, attendee.Name, at = attendee.CheckedInAt });
            store.SetCheckedIn(attendee.Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return Results.Ok(new { status = "ok", attendee.Id, attendee.Name });
        });
        op.MapPost("/attendees/{id:long}/checkin", (long id, Store store) =>
        {
            if (store.FindAttendee(id) is not { } person) return Results.NotFound();
            // Two phones at one door: the first arrival stays the time of arrival.
            if (person.CheckedInAt != null) return Results.Ok(new { status = "already", person.Name });
            store.SetCheckedIn(id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return Results.Ok(new { status = "ok", person.Name });
        });
        op.MapDelete("/attendees/{id:long}/checkin", (long id, Store store) => { store.SetCheckedIn(id, null); return Results.Ok(); });

        op.MapGet("/sponsors", (Store store, LanInfo lan) => store.ListSponsors().Select(s => new
        {
            s.Id, s.Name, s.Keywords,
            standUrl = $"{lan.HttpUrl}/stand/{s.Code}",
            standQr = QrImage.Svg($"{lan.HttpUrl}/stand/{s.Code}"),
            portalUrl = $"{lan.HttpUrl}/sponsor/{s.Key}",
            leads = store.ListLeads(s.Id).Count,
        }));
        op.MapPost("/sponsors", (SponsorRequest request, Store store) =>
        {
            var name = request.Name?.Trim() ?? "";
            return name.Length is 0 or > MaxNameChars ? Results.BadRequest(new { error = "Give the sponsor a name." }) : Results.Ok(store.AddSponsor(name, request.Keywords?.Trim() ?? ""));
        });
        op.MapDelete("/sponsors/{id:long}", (long id, Store store) => { store.DeleteSponsor(id); return Results.Ok(); });

        // RF-36. Sends each person their own list, once the organizer says the event is over.
        op.MapPost("/mail/contact-lists", (Store store, Mailer mailer) =>
        {
            var sent = 0;
            foreach (var person in store.ListAttendees())
            {
                var lines = store.ListContacts(person.Id)
                    .Select(c => (Row: c, Other: store.FindAttendee(c.A == person.Id ? c.B : c.A)))
                    .Where(c => c.Other != null)
                    .Select(c => $"- {c.Other!.Name} ({c.Other.Email})" + (c.Row.Context.Length > 0 ? $": you met during \"{c.Row.Context}\"" : ""))
                    .ToList();
                if (lines.Count == 0) continue;
                store.QueueMail(person.Email, $"Your contacts from {store.GetSetting(Settings.EventName, "the event")}", $"Hi {person.Name},\n\nThe people you connected with:\n\n{string.Join("\n", lines)}\n", "");
                sent++;
            }
            return new { sent, mailConfigured = mailer.Configured };
        });

        // RF-38
        op.MapGet("/organizer", (Store store, StatusBroadcaster status, TalkService talks) =>
        {
            var people = store.ListAttendees();
            var rooms = store.ListRooms().Select(room =>
            {
                var talk = talks.Current(room.Id);
                var limits = store.GetLimits(room.Id);
                var open = talk != null ? store.CountOpenGroups(talk.Id) : 0;
                var catchUps = talk != null ? store.ListCatchUps(talk.Id).Count : 0;
                var occupancy = status.Occupancy(room.Id);
                return new
                {
                    room.Id, room.Name, talk = talk?.Title, speaker = talk?.Speaker,
                    occupancy, connected = status.Connected(room.Id), limits.Capacity, full = status.IsFull(room.Id),
                    pendingQuestions = open, catchUps,
                    interest = occupancy + open * 3 + catchUps,
                    tech = status.Tech(room.Id),
                };
            }).ToList();
            return new
            {
                rooms,
                mostInterest = rooms.Where(r => r.talk != null).OrderByDescending(r => r.interest).Select(r => new { r.Name, r.talk, r.interest }).Take(3),
                pendingQuestions = rooms.Sum(r => r.pendingQuestions),
                registered = people.Count,
                checkedIn = people.Count(p => p.CheckedInAt != null),
            };
        });

        // ----- Reports -----
        op.MapGet("/reports/speaker/{talk:long}", (long talk, Store store, Reports reports) =>
            store.FindTalk(talk) is { } found ? Results.Ok(reports.SpeakerFeedback(found)) : Results.NotFound());
        op.MapGet("/reports/sponsor/{id:long}", (long id, Store store, Reports reports) =>
            store.ListSponsors().FirstOrDefault(s => s.Id == id) is { } sponsor ? Results.Ok(reports.SponsorReport(sponsor)) : Results.NotFound());
        op.MapGet("/reports/overview", (Reports reports) => reports.Overview());
        op.MapGet("/reports/audience", async (bool? model, Reports reports, CancellationToken ct) => Results.Ok(await reports.AudienceAsync(model == true, ct)));

        // ----- Clips -----
        op.MapGet("/clips", (Store store) => store.ListClips());
        op.MapPost("/talks/{talk:long}/clips", (long talk, ClipRequest request, Store store, ClipMaker clips) =>
        {
            if (store.FindTalk(talk) is null) return Results.NotFound();
            if (Recorder.FindPart(talk, request.T) == null) return Results.BadRequest(new { error = "This talk was not recorded." });
            return Results.Ok(clips.Request(talk, Math.Max(0, request.T), Reports.ClipLengthMs, "chosen by hand"));
        });
        op.MapGet("/clips/{id:long}.mp4", (long id, Store store) =>
            store.FindClip(id) is { Status: "ready" } clip && File.Exists(ClipMaker.FilePath(clip.File))
                ? Results.File(ClipMaker.FilePath(clip.File), "video/mp4", $"clip-{id}.mp4", enableRangeProcessing: true)
                : Results.NotFound());
        op.MapDelete("/clips/{id:long}", (long id, Store store, ClipMaker clips) =>
        {
            if (store.FindClip(id) is { } clip) clips.Delete(clip);
            return Results.Ok();
        });

        op.MapDelete("/chat/{id:long}", async (long id, long talk, Store store, Microsoft.AspNetCore.SignalR.IHubContext<LiveHub> hub) =>
        {
            store.DeleteChat(id);
            await Microsoft.AspNetCore.SignalR.ClientProxyExtensions.SendAsync(hub.Clients.Group(LiveHub.ChatGroup(talk)), "chatRemoved", id);
            return Results.Ok();
        });

        // After the talk, the speaker can still answer what the time did not allow (RF-35).
        op.MapPost("/talks/{talk:long}/questions/{group:long}/answer", (long talk, long group, AnswerRequest request, Store store) =>
        {
            var answer = request.Answer?.Trim() ?? "";
            if (answer.Length is 0 or > 2000) return Results.BadRequest(new { error = "Write the answer first." });
            store.AnswerGroup(talk, group, answer);
            return Results.Ok();
        });
        op.MapGet("/rooms/{room:long}/last-talk", (long room, Store store, TalkService talks) =>
        {
            var talk = talks.Current(room) ?? store.ListTalks().FirstOrDefault(t => t.RoomId == room);
            return talk == null ? Results.Ok(new { talk = (Talk?)null, questions = Array.Empty<AnsweredQuestion>() }) : Results.Ok(new { talk, questions = store.ListHallQuestions(talk.Id) });
        });
    }

    /// <summary>Accepts our ticket link, a bare code, or a Luma ticket QR, whatever URL shape it comes in.</summary>
    public static Attendee? FindByScan(Store store, string scanned)
    {
        var code = scanned.Trim();
        if (code.Length == 0) return null;
        if (TicketInUrl().Match(code) is { Success: true } ticket && store.FindAttendeeByToken(ticket.Groups[1].Value) is { } byUrl) return byUrl;
        if (store.FindAttendeeByToken(code) is { } byToken) return byToken;
        if (store.FindAttendeeByLuma(code) is { } byLuma) return byLuma;
        return LumaGuestInUrl().Match(code) is { Success: true } guest ? store.FindAttendeeByLuma(guest.Groups[1].Value) : null;
    }

    [GeneratedRegex(@"/ticket/([0-9a-f]{32})\b")]
    private static partial Regex TicketInUrl();

    [GeneratedRegex(@"[?&]pk=([\w-]+)")]
    private static partial Regex LumaGuestInUrl();
}

public sealed record ClipRequest(long T);
public sealed record AnswerRequest(string Answer);
