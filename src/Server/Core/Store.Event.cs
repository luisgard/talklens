using Microsoft.Data.Sqlite;

namespace TalkLens.Core;

public sealed record AgendaItem(long Id, long RoomId, string Title, string Speaker, long StartsAt, long EndsAt, string About);
public sealed record RoomLimits(long RoomId, int Capacity, bool MarkedFull);
/// <param name="Source">self | manual | csv | luma</param>
public sealed record Attendee(
    long Id, string Name, string Email, string Token, long? CheckedInAt, string Source, string LumaGuestId, string LumaQr,
    string Interests, bool Networking, long CreatedAt, string ContactCode, long ContactCodeUntil, string Seeking = "", string Offering = "", bool WantsSummary = false);
public sealed record Attendance(long AttendeeId, long TalkId, long FirstSeen, long LastSeen, long WatchMs);
public sealed record TalkStats(long TalkId, int Peak, long WatchMs);
/// <param name="TalkId">0 is the event-wide chat (RF-32)</param>
public sealed record ChatMessage(long Id, long TalkId, long At, string Author, string Text);
public sealed record ContactRow(long Id, long A, long B, string Context, long At);
public sealed record Sponsor(long Id, string Name, string Keywords, string Code, string Key);
public sealed record SponsorLead(long SponsorId, long AttendeeId, string Name, string Email, long At);
/// <param name="Status">queued | rendering | ready | failed</param>
public sealed record ClipRow(long Id, long TalkId, long T, long LengthMs, string Reason, string Status, string File);
public sealed record MailItem(long Id, string To, string Subject, string Body, string QrText, int Attempts);
public sealed record AnsweredQuestion(long GroupId, string Text, int Count, string Status, string Answer);

/// <summary>The event layer: agenda, people who chose to register, conversations and what they consented to share.</summary>
public sealed partial class Store
{
    private void CreateEventTables() => Exec($$"""
        CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS agenda(
          id INTEGER PRIMARY KEY, room_id INTEGER NOT NULL, title TEXT NOT NULL, speaker TEXT NOT NULL,
          starts_at INTEGER NOT NULL, ends_at INTEGER NOT NULL, about TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS room_limits(room_id INTEGER PRIMARY KEY, capacity INTEGER NOT NULL, marked_full INTEGER NOT NULL);
        {{AttendeesTable("attendees")}}
        CREATE TABLE IF NOT EXISTS attendance(
          attendee_id INTEGER NOT NULL, talk_id INTEGER NOT NULL, first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL,
          watch_ms INTEGER NOT NULL, PRIMARY KEY(attendee_id, talk_id));
        CREATE TABLE IF NOT EXISTS door_scans(attendee_id INTEGER NOT NULL, room_id INTEGER NOT NULL, talk_id INTEGER NOT NULL, at INTEGER NOT NULL, PRIMARY KEY(attendee_id, talk_id));
        CREATE TABLE IF NOT EXISTS talk_stats(talk_id INTEGER PRIMARY KEY, peak INTEGER NOT NULL, watch_ms INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS catchup_log(talk_id INTEGER NOT NULL, t INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS chat(id INTEGER PRIMARY KEY, talk_id INTEGER NOT NULL, at INTEGER NOT NULL, author TEXT NOT NULL, text TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_chat ON chat(talk_id, id);
        CREATE TABLE IF NOT EXISTS contacts(id INTEGER PRIMARY KEY, a INTEGER NOT NULL, b INTEGER NOT NULL, context TEXT NOT NULL, at INTEGER NOT NULL, UNIQUE(a, b));
        CREATE TABLE IF NOT EXISTS sponsors(id INTEGER PRIMARY KEY, name TEXT NOT NULL, keywords TEXT NOT NULL, code TEXT NOT NULL UNIQUE, key TEXT NOT NULL UNIQUE);
        CREATE TABLE IF NOT EXISTS sponsor_leads(sponsor_id INTEGER NOT NULL, attendee_id INTEGER NOT NULL, at INTEGER NOT NULL, PRIMARY KEY(sponsor_id, attendee_id));
        CREATE TABLE IF NOT EXISTS clips(id INTEGER PRIMARY KEY, talk_id INTEGER NOT NULL, t INTEGER NOT NULL, length_ms INTEGER NOT NULL, reason TEXT NOT NULL, status TEXT NOT NULL, file TEXT NOT NULL DEFAULT '');
        CREATE TABLE IF NOT EXISTS mail_outbox(id INTEGER PRIMARY KEY, recipient TEXT NOT NULL, subject TEXT NOT NULL, body TEXT NOT NULL, qr_text TEXT NOT NULL, attempts INTEGER NOT NULL DEFAULT 0, sent_at INTEGER);
        """);

    /// <summary>Email may be empty: a profile made in the room is enough to meet people, and a ticket can come later.</summary>
    private static string AttendeesTable(string name) => $"""
        CREATE TABLE IF NOT EXISTS {name}(
          id INTEGER PRIMARY KEY, name TEXT NOT NULL, email TEXT UNIQUE COLLATE NOCASE, token TEXT NOT NULL UNIQUE,
          checked_in_at INTEGER, source TEXT NOT NULL, luma_guest_id TEXT NOT NULL DEFAULT '', luma_qr TEXT NOT NULL DEFAULT '',
          interests TEXT NOT NULL DEFAULT '', networking INTEGER NOT NULL DEFAULT 0, created_at INTEGER NOT NULL,
          contact_code TEXT NOT NULL DEFAULT '', contact_code_until INTEGER NOT NULL DEFAULT 0,
          seeking TEXT NOT NULL DEFAULT '', offering TEXT NOT NULL DEFAULT '', wants_summary INTEGER NOT NULL DEFAULT 0);
        """;

    private void MigrateEventColumns()
    {
        // Before profiles, every attendee had an email. SQLite cannot relax a column, so the table is copied once.
        if (Query("SELECT \"notnull\" FROM pragma_table_info('attendees') WHERE name='email'", r => r.GetInt64(0)).FirstOrDefault() == 1)
        {
            Exec($"""
                BEGIN;
                {AttendeesTable("attendees_new")}
                INSERT INTO attendees_new(id,name,email,token,checked_in_at,source,luma_guest_id,luma_qr,interests,networking,created_at,contact_code,contact_code_until)
                  SELECT id,name,NULLIF(email,''),token,checked_in_at,source,luma_guest_id,luma_qr,interests,networking,created_at,contact_code,contact_code_until FROM attendees;
                DROP TABLE attendees;
                ALTER TABLE attendees_new RENAME TO attendees;
                COMMIT;
                """);
        }
        if (Query("SELECT name FROM pragma_table_info('speaker_questions') WHERE name='answer'", r => r.GetString(0)).Count == 0)
            Exec("ALTER TABLE speaker_questions ADD COLUMN answer TEXT NOT NULL DEFAULT ''");
    }

    private void DeleteTalkEventData(long talkId)
    {
        foreach (var table in new[] { "chat", "attendance", "door_scans", "talk_stats", "catchup_log", "clips" })
            Exec($"DELETE FROM {table} WHERE talk_id=$a", talkId);
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ----- Settings -----
    public string GetSetting(string key, string fallback = "") =>
        Query("SELECT value FROM settings WHERE key=$a", r => r.GetString(0), key).FirstOrDefault() ?? fallback;

    public void SetSetting(string key, string value) =>
        Exec("INSERT INTO settings(key,value) VALUES($a,$b) ON CONFLICT(key) DO UPDATE SET value=$b", key, value);

    // ----- Agenda and rooms (RF-22, RF-34) -----
    private static AgendaItem ReadAgenda(SqliteDataReader r) => new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetInt64(5), r.GetString(6));

    public IReadOnlyList<AgendaItem> ListAgenda() => Query("SELECT id,room_id,title,speaker,starts_at,ends_at,about FROM agenda ORDER BY starts_at,room_id", ReadAgenda);

    public AgendaItem AddAgenda(long room, string title, string speaker, long starts, long ends, string about) =>
        new(Insert("INSERT INTO agenda(room_id,title,speaker,starts_at,ends_at,about) VALUES($a,$b,$c,$d,$e,$f)", room, title, speaker, starts, ends, about), room, title, speaker, starts, ends, about);

    public void DeleteAgenda(long id)
    {
        Exec("DELETE FROM agenda WHERE id=$a", id);
        Exec("DELETE FROM agenda_vectors WHERE agenda_id=$a", id);
    }

    public RoomLimits GetLimits(long room) => Query("SELECT room_id,capacity,marked_full FROM room_limits WHERE room_id=$a",
        r => new RoomLimits(r.GetInt64(0), r.GetInt32(1), r.GetInt64(2) == 1), room).FirstOrDefault() ?? new RoomLimits(room, 0, false);

    public void SetLimits(long room, int capacity, bool markedFull) => Exec(
        "INSERT INTO room_limits(room_id,capacity,marked_full) VALUES($a,$b,$c) ON CONFLICT(room_id) DO UPDATE SET capacity=$b, marked_full=$c",
        room, capacity, markedFull ? 1 : 0);

    // ----- Attendees (RF-23 to RF-25) -----
    private const string AttendeeColumns = "id,name,email,token,checked_in_at,source,luma_guest_id,luma_qr,interests,networking,created_at,contact_code,contact_code_until,seeking,offering,wants_summary";

    private static Attendee ReadAttendee(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetInt64(4), r.GetString(5), r.GetString(6), r.GetString(7),
        r.GetString(8), r.GetInt64(9) == 1, r.GetInt64(10), r.GetString(11), r.GetInt64(12), r.GetString(13), r.GetString(14), r.GetInt64(15) == 1);

    public static string NewToken() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <returns>The attendee, and whether this call created them.</returns>
    public (Attendee Attendee, bool Created) UpsertAttendee(string name, string email, string source, string lumaGuestId = "", string lumaQr = "", long? checkedInAt = null)
    {
        email = email.Trim().ToLowerInvariant();
        lock (_gate)
        {
            if (FindAttendeeByEmail(email) is { } existing)
            {
                if (lumaGuestId.Length > 0)
                    Exec("UPDATE attendees SET luma_guest_id=$a, luma_qr=$b WHERE id=$c", lumaGuestId, lumaQr, existing.Id);
                if (checkedInAt != null && existing.CheckedInAt == null) Exec("UPDATE attendees SET checked_in_at=$a WHERE id=$b", checkedInAt.Value, existing.Id);
                return (FindAttendee(existing.Id)!, false);
            }
            var id = Insert("INSERT INTO attendees(name,email,token,checked_in_at,source,luma_guest_id,luma_qr,created_at) VALUES($a,$b,$c,$d,$e,$f,$g,$h)",
                name, email, NewToken(), checkedInAt is { } at ? at : DBNull.Value, source, lumaGuestId, lumaQr, Now());
            return (FindAttendee(id)!, true);
        }
    }

    public Attendee? FindAttendee(long id) => Query($"SELECT {AttendeeColumns} FROM attendees WHERE id=$a", ReadAttendee, id).FirstOrDefault();
    public Attendee? FindAttendeeByEmail(string email) => email.Trim().Length == 0 ? null : Query($"SELECT {AttendeeColumns} FROM attendees WHERE lower(email)=$a", ReadAttendee, email.Trim().ToLowerInvariant()).FirstOrDefault();
    public Attendee? FindAttendeeByToken(string token) => token.Length == 0 ? null : Query($"SELECT {AttendeeColumns} FROM attendees WHERE token=$a", ReadAttendee, token).FirstOrDefault();
    public Attendee? FindAttendeeByLuma(string qrOrGuest) => qrOrGuest.Length == 0 ? null :
        Query($"SELECT {AttendeeColumns} FROM attendees WHERE luma_qr=$a OR luma_guest_id=$a", ReadAttendee, qrOrGuest).FirstOrDefault();
    public IReadOnlyList<Attendee> ListAttendees() => Query($"SELECT {AttendeeColumns} FROM attendees ORDER BY name COLLATE NOCASE", ReadAttendee);

    /// <summary>Someone who wants to meet people before, or without, a ticket. Only the phone that made it gets the token.</summary>
    public Attendee AddProfile(string name) =>
        FindAttendee(Insert("INSERT INTO attendees(name,email,token,source,created_at) VALUES($a,NULL,$b,'profile',$c)", name, NewToken(), Now()))!;

    /// <returns>False when the email belongs to someone else, which is never merged: anyone can type anyone's email.</returns>
    public bool SetProfile(long attendee, string name, string seeking, string offering, bool networking, string email, bool wantsSummary)
    {
        email = email.Trim().ToLowerInvariant();
        lock (_gate)
        {
            if (email.Length > 0 && FindAttendeeByEmail(email) is { } owner && owner.Id != attendee) return false;
            Exec("UPDATE attendees SET name=$a, seeking=$b, offering=$c, networking=$d, email=$e, wants_summary=$f WHERE id=$g",
                name, seeking, offering, networking ? 1 : 0, email.Length > 0 ? email : DBNull.Value, wantsSummary ? 1 : 0, attendee);
            return true;
        }
    }

    public void SetCheckedIn(long attendee, long? at) => Exec("UPDATE attendees SET checked_in_at=$a WHERE id=$b", at is { } value ? value : DBNull.Value, attendee);

    public void SetNetworking(long attendee, bool networking, string interests) =>
        Exec("UPDATE attendees SET networking=$a, interests=$b WHERE id=$c", networking ? 1 : 0, interests, attendee);

    public void SetContactCode(long attendee, string code, long until) =>
        Exec("UPDATE attendees SET contact_code=$a, contact_code_until=$b WHERE id=$c", code, until, attendee);

    public Attendee? FindAttendeeByContactCode(string code) => code.Length == 0 ? null :
        Query($"SELECT {AttendeeColumns} FROM attendees WHERE contact_code=$a AND contact_code_until>$b", ReadAttendee, code, Now()).FirstOrDefault();

    /// <summary>Removes the person and everything linked to them, including contacts and consent to sponsors.</summary>
    public void DeleteAttendee(long id)
    {
        Exec("DELETE FROM attendance WHERE attendee_id=$a", id);
        Exec("DELETE FROM door_scans WHERE attendee_id=$a", id);
        Exec("DELETE FROM contacts WHERE a=$a OR b=$a", id);
        Exec("DELETE FROM sponsor_leads WHERE attendee_id=$a", id);
        Exec("DELETE FROM connections WHERE from_id=$a OR to_id=$a", id);
        Exec("DELETE FROM profile_vectors WHERE attendee_id=$a", id);
        Exec("DELETE FROM offer_requests WHERE attendee_id=$a", id);
        Exec("DELETE FROM mail_outbox WHERE recipient=(SELECT email FROM attendees WHERE id=$a)", id);
        Exec("DELETE FROM attendees WHERE id=$a", id);
    }

    public void DeleteAllAttendees()
    {
        foreach (var table in new[] { "attendance", "door_scans", "contacts", "sponsor_leads", "connections", "profile_vectors", "offer_requests", "attendees", "mail_outbox" })
            Exec($"DELETE FROM {table}");
    }

    // ----- Attendance (RF-26) -----
    public void AddWatchTime(long attendee, long talk, long ms)
    {
        var now = Now();
        Exec("""
            INSERT INTO attendance(attendee_id,talk_id,first_seen,last_seen,watch_ms) VALUES($a,$b,$c,$c,$d)
            ON CONFLICT(attendee_id,talk_id) DO UPDATE SET last_seen=$c, watch_ms=watch_ms+$d
            """, attendee, talk, now, ms);
    }

    public void AddDoorScan(long attendee, long room, long talk) =>
        Exec("INSERT OR IGNORE INTO door_scans(attendee_id,room_id,talk_id,at) VALUES($a,$b,$c,$d)", attendee, room, talk, Now());

    public int CountDoorOnly(long talk, IReadOnlyCollection<long> connected)
    {
        var scanned = Query("SELECT attendee_id FROM door_scans WHERE talk_id=$a", r => r.GetInt64(0), talk);
        return scanned.Count(id => !connected.Contains(id));
    }

    public IReadOnlyList<Attendance> ListAttendance() => Query("SELECT attendee_id,talk_id,first_seen,last_seen,watch_ms FROM attendance ORDER BY first_seen",
        r => new Attendance(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4)));

    public IReadOnlyList<Attendance> ListAttendanceOf(long attendee) => ListAttendance().Where(a => a.AttendeeId == attendee).ToList();

    /// <summary>A door scan counts as attending even for someone who never opened the stream.</summary>
    public IReadOnlyList<long> ListDoorTalksOf(long attendee) => Query("SELECT talk_id FROM door_scans WHERE attendee_id=$a", r => r.GetInt64(0), attendee);

    public void AddTalkSample(long talk, int watching, long ms) => Exec("""
        INSERT INTO talk_stats(talk_id,peak,watch_ms) VALUES($a,$b,$c)
        ON CONFLICT(talk_id) DO UPDATE SET peak=MAX(peak,$b), watch_ms=watch_ms+$c
        """, talk, watching, ms);

    public TalkStats GetTalkStats(long talk) => Query("SELECT talk_id,peak,watch_ms FROM talk_stats WHERE talk_id=$a",
        r => new TalkStats(r.GetInt64(0), r.GetInt32(1), r.GetInt64(2)), talk).FirstOrDefault() ?? new TalkStats(talk, 0, 0);

    /// <summary>A count at a moment of the talk, with no record of who pressed it.</summary>
    public void LogCatchUp(long talk, long t) => Exec("INSERT INTO catchup_log(talk_id,t) VALUES($a,$b)", talk, t);
    public IReadOnlyList<long> ListCatchUps(long talk) => Query("SELECT t FROM catchup_log WHERE talk_id=$a", r => r.GetInt64(0), talk);

    // ----- Chat (RF-30, RF-32, RF-35) -----
    public ChatMessage AddChat(long talk, string author, string text)
    {
        var at = Now();
        return new ChatMessage(Insert("INSERT INTO chat(talk_id,at,author,text) VALUES($a,$b,$c,$d)", talk, at, author, text), talk, at, author, text);
    }

    public IReadOnlyList<ChatMessage> ListChat(long talk, int limit = 200) => Query(
        "SELECT id,talk_id,at,author,text FROM (SELECT * FROM chat WHERE talk_id=$a ORDER BY id DESC LIMIT $b) ORDER BY id",
        r => new ChatMessage(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetString(4)), talk, limit);

    public void DeleteChat(long id) => Exec("DELETE FROM chat WHERE id=$a", id);

    // ----- Speaker answers after the talk (RF-35) -----
    public void AnswerGroup(long talk, long group, string answer) =>
        Exec("UPDATE speaker_questions SET answer=$a, status='answered' WHERE talk_id=$b AND group_id=$c", answer, talk, group);

    /// <summary>What the hallway shows: questions still open, and those the speaker answered in writing. Dismissed ones never appear.</summary>
    public IReadOnlyList<AnsweredQuestion> ListHallQuestions(long talk) => Query(
        "SELECT group_id,text,status,answer FROM speaker_questions WHERE talk_id=$a AND status IN ('open','shown','answered') ORDER BY id",
        r => (Group: r.GetInt64(0), Text: r.GetString(1), Status: r.GetString(2), Answer: r.GetString(3)), talk)
        .GroupBy(q => q.Group)
        .Select(g => new AnsweredQuestion(g.Key, g.First().Text, g.Count(), g.First().Status, g.First().Answer))
        .Where(q => q.Status != "answered" || q.Answer.Length > 0)
        .OrderByDescending(q => q.Count).ToList();

    // ----- Contacts (RF-36) -----
    public void AddContact(long a, long b, string context)
    {
        var (low, high) = a < b ? (a, b) : (b, a);
        Exec("INSERT OR IGNORE INTO contacts(a,b,context,at) VALUES($a,$b,$c,$d)", low, high, context, Now());
    }

    public IReadOnlyList<ContactRow> ListContacts(long attendee) => Query("SELECT id,a,b,context,at FROM contacts WHERE a=$a OR b=$a ORDER BY at",
        r => new ContactRow(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetInt64(4)), attendee);

    public void DeleteContact(long attendee, long id) => Exec("DELETE FROM contacts WHERE id=$a AND (a=$b OR b=$b)", id, attendee);

    // ----- Sponsors (RF-16, RF-37) -----
    private static Sponsor ReadSponsor(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4));

    public Sponsor AddSponsor(string name, string keywords)
    {
        var (code, key) = (NewToken()[..12], NewToken());
        return new Sponsor(Insert("INSERT INTO sponsors(name,keywords,code,key) VALUES($a,$b,$c,$d)", name, keywords, code, key), name, keywords, code, key);
    }

    public IReadOnlyList<Sponsor> ListSponsors() => Query("SELECT id,name,keywords,code,key FROM sponsors ORDER BY name COLLATE NOCASE", ReadSponsor);
    public Sponsor? FindSponsorByCode(string code) => Query("SELECT id,name,keywords,code,key FROM sponsors WHERE code=$a", ReadSponsor, code).FirstOrDefault();
    public Sponsor? FindSponsorByKey(string key) => Query("SELECT id,name,keywords,code,key FROM sponsors WHERE key=$a", ReadSponsor, key).FirstOrDefault();

    public void DeleteSponsor(long id)
    {
        Exec("DELETE FROM sponsor_leads WHERE sponsor_id=$a", id);
        Exec("DELETE FROM offer_requests WHERE sponsor_id=$a", id);
        Exec("DELETE FROM sponsor_offers WHERE sponsor_id=$a", id);
        Exec("DELETE FROM sponsors WHERE id=$a", id);
    }

    public void AddLead(long sponsor, long attendee) => Exec("INSERT OR IGNORE INTO sponsor_leads(sponsor_id,attendee_id,at) VALUES($a,$b,$c)", sponsor, attendee, Now());
    public void DeleteLead(long sponsor, long attendee) => Exec("DELETE FROM sponsor_leads WHERE sponsor_id=$a AND attendee_id=$b", sponsor, attendee);

    public IReadOnlyList<SponsorLead> ListLeads(long sponsor) => Query("""
        SELECT l.sponsor_id,l.attendee_id,a.name,a.email,l.at FROM sponsor_leads l JOIN attendees a ON a.id=l.attendee_id WHERE l.sponsor_id=$a ORDER BY l.at
        """, r => new SponsorLead(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetInt64(4)), sponsor);

    public IReadOnlyList<long> ListLeadSponsorsOf(long attendee) => Query("SELECT sponsor_id FROM sponsor_leads WHERE attendee_id=$a", r => r.GetInt64(0), attendee);

    // ----- Clips (RF-17) -----
    private static ClipRow ReadClip(SqliteDataReader r) => new(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetString(4), r.GetString(5), r.GetString(6));

    public ClipRow AddClip(long talk, long t, long length, string reason) =>
        new(Insert("INSERT INTO clips(talk_id,t,length_ms,reason,status) VALUES($a,$b,$c,$d,'queued')", talk, t, length, reason), talk, t, length, reason, "queued", "");

    public void SetClip(long id, string status, string file) => Exec("UPDATE clips SET status=$a, file=$b WHERE id=$c", status, file, id);
    public ClipRow? FindClip(long id) => Query("SELECT id,talk_id,t,length_ms,reason,status,file FROM clips WHERE id=$a", ReadClip, id).FirstOrDefault();
    public IReadOnlyList<ClipRow> ListClips() => Query("SELECT id,talk_id,t,length_ms,reason,status,file FROM clips ORDER BY id DESC", ReadClip);
    public void DeleteClip(long id) => Exec("DELETE FROM clips WHERE id=$a", id);

    // ----- Mail (RF-24, RF-36) -----
    public void QueueMail(string to, string subject, string body, string qrText) =>
        Exec("INSERT INTO mail_outbox(recipient,subject,body,qr_text) VALUES($a,$b,$c,$d)", to, subject, body, qrText);

    public IReadOnlyList<MailItem> PendingMail(int maxAttempts) => Query(
        "SELECT id,recipient,subject,body,qr_text,attempts FROM mail_outbox WHERE sent_at IS NULL AND attempts<$a ORDER BY id LIMIT 20",
        r => new MailItem(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5)), maxAttempts);

    public void MarkMailSent(long id) => Exec("UPDATE mail_outbox SET sent_at=$a WHERE id=$b", Now(), id);
    public void MarkMailFailed(long id) => Exec("UPDATE mail_outbox SET attempts=attempts+1 WHERE id=$a", id);
    public (int Pending, int Sent) MailCounts() => Query("SELECT COUNT(*) FILTER (WHERE sent_at IS NULL), COUNT(*) FILTER (WHERE sent_at IS NOT NULL) FROM mail_outbox",
        r => (r.GetInt32(0), r.GetInt32(1)))[0];

    // ----- Search across talks (RF-12, RF-19) -----
    public IReadOnlyList<(string Kind, long RefId, long TalkId)> SearchAll(string ftsQuery, int limit) => ftsQuery.Length == 0 ? [] : Query(
        "SELECT kind,ref_id,talk_id FROM search WHERE search MATCH $a ORDER BY bm25(search) LIMIT $b",
        r => (r.GetString(0), r.GetInt64(1), r.GetInt64(2)), ftsQuery, limit);

    public Talk? FindTalk(long id) => ListTalks().FirstOrDefault(t => t.Id == id);
}
