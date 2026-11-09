using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using QRCoder;

namespace TalkLens.Core;

public sealed class MailOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string User { get; set; } = "";
    public string From { get; set; } = "";
    public bool UseSsl { get; set; } = true;
    /// <summary>From the environment only, like the API keys.</summary>
    public static string? Password => Environment.GetEnvironmentVariable("MAIL_PASSWORD");
}

/// <summary>Named settings the organizer changes while setting up the event.</summary>
public static class Settings
{
    public const string EventName = "event_name";
    public const string Venue = "event_venue";
    public const string Dates = "event_dates";
    public const string About = "event_about";
    public const string MinParticipants = "min_participants";
    public const string HallwayHours = "hallway_hours";
    public const string LibraryCode = "library_code";
    public const string Community = "community_enabled";
    public const string RemoteEnabled = "remote_enabled";
    public const string RemoteVideo = "remote_video";
    public const string RemoteUrl = "remote_url";
    public const string ApiKey = "api_key";
    public const string LumaEventId = "luma_event_id";
    public const string SignAlways = "sign_always";
    public const string PhotosAuto = "photos_auto";
    public const string MeetingSpots = "meeting_spots";

    public const int DefaultMinParticipants = 5;
    public const int DefaultHallwayHours = 24;

    public static int MinimumParticipants(this Store store) =>
        int.TryParse(store.GetSetting(MinParticipants), out var value) && value >= 1 ? value : DefaultMinParticipants;

    public static int HallwayWindowHours(this Store store) =>
        int.TryParse(store.GetSetting(HallwayHours), out var value) && value >= 0 ? value : DefaultHallwayHours;

    public static bool IsOn(this Store store, string key) => store.GetSetting(key) == "1";
}

/// <summary>Tickets and event notices only (the spec rules out campaigns). They wait in an outbox, so an event with no internet still registers people.</summary>
public sealed class Mailer(Store store, MailOptions options, Connectivity net, ILogger<Mailer> log) : BackgroundService
{
    private const int MaxAttempts = 5;
    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(30);

    public bool Configured => options.Host.Length > 0 && options.From.Length > 0;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (Configured && net.Online)
                    foreach (var mail in store.PendingMail(MaxAttempts))
                        await SendAsync(mail, stop);
                await Task.Delay(Pause, stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task SendAsync(MailItem item, CancellationToken stop)
    {
        try
        {
            using var message = Build(item, options.From);
            using var smtp = new SmtpClient(options.Host, options.Port) { EnableSsl = options.UseSsl };
            if (options.User.Length > 0) smtp.Credentials = new NetworkCredential(options.User, MailOptions.Password);
            await smtp.SendMailAsync(message, stop);
            store.MarkMailSent(item.Id);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The type only: the message can contain the address.
            log.LogWarning("Sending mail failed: {Type}", e.GetType().Name);
            store.MarkMailFailed(item.Id);
        }
    }

    /// <summary>The QR goes inside the mail as an image, because the ticket link only opens on the event network.</summary>
    public static MailMessage Build(MailItem item, string from)
    {
        var message = new MailMessage(from, item.To, item.Subject, item.Body) { BodyEncoding = Encoding.UTF8, SubjectEncoding = Encoding.UTF8 };
        if (item.QrText.Length == 0) return message;
        var html = $"""<p style="white-space:pre-line">{WebUtility.HtmlEncode(item.Body)}</p><img src="cid:ticket" alt="Your ticket QR code" width="280" height="280">""";
        var view = AlternateView.CreateAlternateViewFromString(html, Encoding.UTF8, "text/html");
        view.LinkedResources.Add(new LinkedResource(new MemoryStream(QrImage.Png(item.QrText)), "image/png") { ContentId = "ticket" });
        message.AlternateViews.Add(view);
        return message;
    }
}

public static class QrImage
{
    public static string Svg(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(data).GetGraphic(8);
    }

    public static byte[] Png(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(10);
    }
}

public sealed record LumaGuest(string Id, string Name, string Email, string Qr, bool Approved, long? CheckedInAt);

/// <summary>RF-27. Reads from the Luma API, which needs a Luma Plus plan. Everyone else imports the CSV Luma exports.</summary>
public sealed class LumaClient(HttpClient http, string baseUrl = "https://public-api.luma.com")
{
    public static string? ApiKey => Environment.GetEnvironmentVariable("LUMA_API_KEY") is { Length: > 0 } key ? key : null;

    public async Task<(string Name, string Venue, string Dates)> GetEventAsync(string eventId, string key, CancellationToken ct)
    {
        using var doc = await GetAsync($"/v1/events/get?event_id={Uri.EscapeDataString(eventId)}", key, ct);
        var root = doc.RootElement.TryGetProperty("event", out var nested) ? nested : doc.RootElement;
        var venue = root.TryGetProperty("geo_address_json", out var geo) && geo.ValueKind == JsonValueKind.Object && geo.TryGetProperty("full_address", out var address)
            ? address.GetString() ?? "" : "";
        return (Text(root, "name"), venue, Text(root, "start_at"));
    }

    public async Task<IReadOnlyList<LumaGuest>> GetGuestsAsync(string eventId, string key, CancellationToken ct)
    {
        var guests = new List<LumaGuest>();
        string? cursor = null;
        do
        {
            var url = $"/v1/events/guests/list?event_id={Uri.EscapeDataString(eventId)}&approval_status=approved"
                + (cursor == null ? "" : $"&pagination_cursor={Uri.EscapeDataString(cursor)}");
            using var doc = await GetAsync(url, key, ct);
            guests.AddRange(doc.RootElement.GetProperty("entries").EnumerateArray().Select(ParseGuest));
            cursor = doc.RootElement.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True ? Text(doc.RootElement, "next_cursor") : null;
        } while (!string.IsNullOrEmpty(cursor));
        return guests;
    }

    public static LumaGuest ParseGuest(JsonElement entry)
    {
        // Older responses nest the guest; current ones do not.
        var guest = entry.TryGetProperty("guest", out var nested) ? nested : entry;
        long? arrived = null;
        if (guest.TryGetProperty("event_tickets", out var tickets) && tickets.ValueKind == JsonValueKind.Array)
            foreach (var ticket in tickets.EnumerateArray())
                if (Text(ticket, "checked_in_at") is { Length: > 0 } at && DateTimeOffset.TryParse(at, out var parsed))
                    arrived = parsed.ToUnixTimeMilliseconds();
        return new LumaGuest(Text(guest, "id"), Text(guest, "user_name"), Text(guest, "user_email"), Text(guest, "check_in_qr_code"), Text(guest, "approval_status") is "approved" or "", arrived);
    }

    private async Task<JsonDocument> GetAsync(string path, string key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
        request.Headers.Add("x-luma-api-key", key);
        request.Headers.Add("accept", "application/json");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Luma answered {(int)response.StatusCode}. Check the event ID and that the account has Luma Plus.");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}

public sealed record ImportedPerson(string Name, string Email, string LumaId, string LumaQr, long? CheckedInAt = null);

/// <param name="NotComing">Rows the tool itself says are not coming: declined, waitlisted, waiting for approval.</param>
public sealed record PeopleFile(IReadOnlyList<ImportedPerson> People, int NoEmail, int NotComing, bool HasEmailColumn);

public static class Csv
{
    /// <summary>A leading =, +, - or @ makes a spreadsheet run the cell as a formula, and attendees choose their own names.</summary>
    public static string Cell(object? value)
    {
        var text = value?.ToString() ?? "";
        if (text.Length > 0 && "=+-@\t\r".Contains(text[0])) text = "'" + text;
        return text.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{text.Replace("\"", "\"\"")}\"" : text;
    }

    public static string Row(params object?[] cells) => string.Join(",", cells.Select(Cell));

    private static readonly string[] NotComingStatuses = ["declined", "rejected", "waitlist", "waitlisted", "pending_approval", "invited", "not_going", "cancelled", "canceled"];

    /// <summary>A sheet saved from Excel may be in the old Windows encoding, and "José" must still read as José.</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    /// <summary>Excel uses semicolons where the comma is the decimal mark, as in much of Latin America and Europe.</summary>
    private static char Delimiter(string text)
    {
        var end = text.IndexOf('\n');
        var first = end < 0 ? text : text[..end];
        return new[] { ',', ';', '\t' }.OrderByDescending(d => first.Count(c => c == d)).First();
    }

    public static List<List<string>> Parse(string text) => Parse(text, ',');

    private static List<List<string>> Parse(string text, char delimiter)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c != '"') cell.Append(c);
                else if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else quoted = false;
            }
            else if (c == '"') quoted = true;
            else if (c == delimiter) { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n')
            {
                row.Add(cell.ToString().TrimEnd('\r'));
                cell.Clear();
                if (row.Any(v => v.Length > 0)) rows.Add(row);
                row = [];
            }
            else cell.Append(c);
        }
        row.Add(cell.ToString().TrimEnd('\r'));
        if (row.Any(v => v.Length > 0)) rows.Add(row);
        return rows;
    }

    /// <summary>Reads attendee lists from any tool, including Luma's guest export, by the names of their columns.</summary>
    public static IReadOnlyList<ImportedPerson> ParsePeople(string text) => ReadPeople(text).People;

    public static PeopleFile ReadPeople(string text)
    {
        text = text.TrimStart('﻿');
        var rows = Parse(text, Delimiter(text));
        if (rows.Count == 0) return new PeopleFile([], 0, 0, false);
        var header = rows[0].Select(h => h.Trim().ToLowerInvariant().Replace(' ', '_').Replace("ó", "o")).ToList();
        int Find(params string[] names) => names.Select(n => header.IndexOf(n)).FirstOrDefault(i => i >= 0, -1);
        var (email, name) = (Find("email", "e-mail", "mail", "user_email", "email_address", "correo", "correo_electronico"), Find("name", "user_name", "full_name", "nombre", "nombre_completo"));
        var (first, last) = (Find("first_name"), Find("last_name", "apellido", "apellidos"));
        var (id, qr, status, arrived) = (Find("api_id", "guest_id", "id"), Find("qr_code_url", "check_in_qr_code"), Find("approval_status", "status"), Find("checked_in_at"));
        if (email < 0) return new PeopleFile([], 0, 0, false);
        string At(List<string> row, int i) => i >= 0 && i < row.Count ? row[i].Trim() : "";
        var (noEmail, notComing) = (0, 0);
        var people = new List<ImportedPerson>();
        foreach (var row in rows.Skip(1))
        {
            if (NotComingStatuses.Contains(At(row, status).ToLowerInvariant())) { notComing++; continue; }
            var address = At(row, email).ToLowerInvariant();
            if (!IsEmail(address)) { noEmail++; continue; }
            var full = At(row, name);
            if (full.Length == 0) full = $"{At(row, first)} {At(row, last)}".Trim();
            // "Nombre" next to "Apellido" is only the first name.
            else if (last >= 0 && first < 0) full = $"{full} {At(row, last)}".Trim();
            if (full.Length == 0) full = address.Split('@')[0];
            long? at = DateTimeOffset.TryParse(At(row, arrived), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) ? when.ToUnixTimeMilliseconds() : null;
            people.Add(new ImportedPerson(full, address, At(row, id), At(row, qr), at));
        }
        return new PeopleFile(people, noEmail, notComing, true);
    }

    public static bool IsEmail(string email) =>
        email.Length is > 2 and <= 254 && email.IndexOf('@') > 0 && email.IndexOf('@') == email.LastIndexOf('@') && email.IndexOf('@') < email.Length - 1 && !email.Any(char.IsWhiteSpace);
}
