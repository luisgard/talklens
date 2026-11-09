using Microsoft.Data.Sqlite;

namespace TalkLens.Core;

public sealed record Talk(long Id, string Title, string Speaker, string Language, long StartedAtUnixMs, long? EndedAtUnixMs, bool KeepAfter, long RoomId);
public sealed record Room(long Id, string Name);
/// <param name="GroupId">The id of the first question that asks the same thing</param>
/// <param name="Status">open | shown | answered | dismissed</param>
public sealed record SpeakerQuestion(long Id, long TalkId, long T, string Text, long GroupId, string Status);
public sealed record Segment(long Id, long TalkId, long T, long End, string Text, string Source);
/// <param name="TextStatus">pending | cloud | ocr</param>
public sealed record Slide(long Id, long TalkId, long T, string Text, string TextStatus);
public sealed record SearchHit(string Kind, long RefId, double Rank);

/// <summary>SQLite index of what was said and shown. A single locked connection is plenty at caption rates.</summary>
public sealed partial class Store : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _gate = new();

    public Store(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _db = new SqliteConnection($"Data Source={databasePath}");
        _db.Open();
        Exec("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS talks(
              id INTEGER PRIMARY KEY, title TEXT NOT NULL, speaker TEXT NOT NULL, language TEXT NOT NULL,
              started_at INTEGER NOT NULL, ended_at INTEGER, keep_after INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS segments(
              id INTEGER PRIMARY KEY, talk_id INTEGER NOT NULL, t INTEGER NOT NULL, end_t INTEGER NOT NULL,
              text TEXT NOT NULL, source TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_segments ON segments(talk_id, t);
            CREATE TABLE IF NOT EXISTS slides(
              id INTEGER PRIMARY KEY, talk_id INTEGER NOT NULL, t INTEGER NOT NULL,
              text TEXT NOT NULL, text_status TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_slides ON slides(talk_id, t);
            CREATE VIRTUAL TABLE IF NOT EXISTS search USING fts5(
              kind UNINDEXED, ref_id UNINDEXED, talk_id UNINDEXED, text, tokenize='unicode61 remove_diacritics 2');
            UPDATE talks SET ended_at = started_at WHERE ended_at IS NULL;
            CREATE TABLE IF NOT EXISTS rooms(id INTEGER PRIMARY KEY, name TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS speaker_questions(
              id INTEGER PRIMARY KEY, talk_id INTEGER NOT NULL, t INTEGER NOT NULL, text TEXT NOT NULL,
              group_id INTEGER NOT NULL, status TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_speaker_questions ON speaker_questions(talk_id, group_id);
            CREATE TABLE IF NOT EXISTS signs(talk_id INTEGER NOT NULL, t INTEGER NOT NULL, json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_signs ON signs(talk_id, t);
            """);
        // Talks saved before rooms existed all happened in the one room there was.
        if (Query("SELECT name FROM pragma_table_info('talks') WHERE name='room_id'", r => r.GetString(0)).Count == 0)
            Exec("ALTER TABLE talks ADD COLUMN room_id INTEGER NOT NULL DEFAULT 1");
        if (ListRooms().Count == 0) Exec("INSERT INTO rooms(id,name) VALUES(1,'Room 1')");
        CreateEventTables();
        MigrateEventColumns();
        CreateEngagementTables();
        CreateWallTables();
        CreateConnectionTables();
        CreateOfferTables();
        Exec("""
            CREATE TABLE IF NOT EXISTS vectors(
              id INTEGER PRIMARY KEY, talk_id INTEGER NOT NULL, kind TEXT NOT NULL, first_id INTEGER NOT NULL, last_id INTEGER NOT NULL,
              model TEXT NOT NULL, vector BLOB NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_vectors ON vectors(talk_id, model, kind);
            CREATE TABLE IF NOT EXISTS agenda_vectors(
              agenda_id INTEGER PRIMARY KEY, model TEXT NOT NULL, text TEXT NOT NULL, vector BLOB NOT NULL);
            """);
    }

    public IReadOnlyList<Room> ListRooms() => Query("SELECT id,name FROM rooms ORDER BY id", r => new Room(r.GetInt64(0), r.GetString(1)));

    public Room AddRoom(string name) => new(Insert("INSERT INTO rooms(name) VALUES($a)", name), name);

    public void RenameRoom(long id, string name) => Exec("UPDATE rooms SET name=$a WHERE id=$b", name, id);

    /// <summary>Past talks stay, so removing a room never loses a transcript.</summary>
    public void DeleteRoom(long id) => Exec("DELETE FROM rooms WHERE id=$a", id);

    public Talk CreateTalk(string title, string speaker, string language, bool keepAfter, long roomId = 1)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var id = Insert("INSERT INTO talks(title,speaker,language,started_at,keep_after,room_id) VALUES($a,$b,$c,$d,$e,$f)",
            title, speaker, language, now, keepAfter ? 1 : 0, roomId);
        return new Talk(id, title, speaker, language, now, null, keepAfter, roomId);
    }

    public void EndTalk(long talkId) =>
        Exec("UPDATE talks SET ended_at=$a WHERE id=$b", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), talkId);

    public void DeleteTalk(long talkId)
    {
        Exec("DELETE FROM segments WHERE talk_id=$a", talkId);
        Exec("DELETE FROM slides WHERE talk_id=$a", talkId);
        Exec("DELETE FROM search WHERE talk_id=$a", talkId);
        Exec("DELETE FROM vectors WHERE talk_id=$a", talkId);
        Exec("DELETE FROM speaker_questions WHERE talk_id=$a", talkId);
        Exec("DELETE FROM signs WHERE talk_id=$a", talkId);
        DeleteTalkEventData(talkId);
        DeleteTalkEngagement(talkId);
        DeleteTalkWall(talkId);
        Exec("DELETE FROM talks WHERE id=$a", talkId);
    }

    public IReadOnlyList<Talk> ListTalks() => Query(
        "SELECT id,title,speaker,language,started_at,ended_at,keep_after,room_id FROM talks ORDER BY id DESC",
        r => new Talk(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4),
            r.IsDBNull(5) ? null : r.GetInt64(5), r.GetInt64(6) == 1, r.GetInt64(7)));

    public Segment AddSegment(long talkId, long t, long end, string text, string source)
    {
        lock (_gate)
        {
            var id = Insert("INSERT INTO segments(talk_id,t,end_t,text,source) VALUES($a,$b,$c,$d,$e)", talkId, t, end, text, source);
            Exec("INSERT INTO search(kind,ref_id,talk_id,text) VALUES('segment',$a,$b,$c)", id, talkId, text);
            return new Segment(id, talkId, t, end, text, source);
        }
    }

    /// <summary>What the avatar signed live, as the JSON the phones got, at the talk time it was said.</summary>
    public void AddSign(long talkId, long t, string json) => Exec("INSERT INTO signs(talk_id,t,json) VALUES($a,$b,$c)", talkId, t, json);

    public IReadOnlyList<(long T, string Json)> GetSigns(long talkId) =>
        Query("SELECT t,json FROM signs WHERE talk_id=$a ORDER BY t", r => (r.GetInt64(0), r.GetString(1)), talkId);

    public Slide AddSlide(long talkId, long t)
    {
        var id = Insert("INSERT INTO slides(talk_id,t,text,text_status) VALUES($a,$b,'','pending')", talkId, t);
        return new Slide(id, talkId, t, "", "pending");
    }

    public void SetSlideText(long slideId, long talkId, string text, string status)
    {
        lock (_gate)
        {
            Exec("UPDATE slides SET text=$a, text_status=$b WHERE id=$c", text, status, slideId);
            Exec("DELETE FROM search WHERE kind='slide' AND ref_id=$a", slideId);
            Exec("DELETE FROM vectors WHERE kind='slide' AND first_id=$a", slideId);
            if (text.Length > 0)
                Exec("INSERT INTO search(kind,ref_id,talk_id,text) VALUES('slide',$a,$b,$c)", slideId, talkId, text);
        }
    }

    public IReadOnlyList<Segment> GetSegments(long talkId, long fromMs = 0, long toMs = long.MaxValue) => Query(
        "SELECT id,talk_id,t,end_t,text,source FROM segments WHERE talk_id=$a AND t>=$b AND t<$c ORDER BY t",
        r => new Segment(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetString(4), r.GetString(5)),
        talkId, fromMs, toMs);

    public IReadOnlyList<Segment> GetSegmentsAfter(long talkId, long afterId) => Query(
        "SELECT id,talk_id,t,end_t,text,source FROM segments WHERE talk_id=$a AND id>$b ORDER BY id",
        r => new Segment(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetString(4), r.GetString(5)),
        talkId, afterId);

    public long LastVectorSegment(long talkId, string model) => Query(
        "SELECT COALESCE(MAX(last_id),0) FROM vectors WHERE talk_id=$a AND model=$b AND kind='segment'", r => r.GetInt64(0), talkId, model)[0];

    public IReadOnlyList<Slide> SlidesWithoutVector(long talkId, string model) => Query(
        "SELECT id,talk_id,t,text,text_status FROM slides s WHERE talk_id=$a AND text<>'' AND NOT EXISTS(SELECT 1 FROM vectors v WHERE v.kind='slide' AND v.first_id=s.id AND v.model=$b) ORDER BY t",
        r => new Slide(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetString(4)), talkId, model);

    /// <summary>Checks the talk still exists: it may have been deleted while its text was out being embedded.</summary>
    public void AddVector(long talkId, string kind, long firstId, long lastId, string model, byte[] vector) => Exec(
        "INSERT INTO vectors(talk_id,kind,first_id,last_id,model,vector) SELECT $a,$b,$c,$d,$e,$f WHERE EXISTS(SELECT 1 FROM talks WHERE id=$a)",
        talkId, kind, firstId, lastId, model, vector);

    public IReadOnlyList<(long TalkId, string Kind, long FirstId, long LastId, byte[] Vector)> ReadVectors(string model, long? talkId) => Query(
        "SELECT talk_id,kind,first_id,last_id,vector FROM vectors WHERE model=$a AND ($b=0 OR talk_id=$b)",
        r => (r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetFieldValue<byte[]>(4)), model, talkId ?? 0);

    public IReadOnlyList<(long Id, long TalkId, string Kind, long FirstId, long LastId, string Model, byte[] Vector)> ReadVectorsAfter(long afterId, int limit) => Query(
        "SELECT id,talk_id,kind,first_id,last_id,model,vector FROM vectors WHERE id>$a ORDER BY id LIMIT $b",
        r => (r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4), r.GetString(5), r.GetFieldValue<byte[]>(6)), afterId, limit);

    public IReadOnlyDictionary<long, (string Model, string Text, byte[] Vector)> ReadAgendaVectors() => Query(
        "SELECT agenda_id,model,text,vector FROM agenda_vectors",
        r => (Id: r.GetInt64(0), Row: (r.GetString(1), r.GetString(2), r.GetFieldValue<byte[]>(3)))).ToDictionary(x => x.Id, x => x.Row);

    public void SetAgendaVector(long agendaId, string model, string text, byte[] vector) => Exec(
        "INSERT OR REPLACE INTO agenda_vectors(agenda_id,model,text,vector) SELECT $a,$b,$c,$d WHERE EXISTS(SELECT 1 FROM agenda WHERE id=$a)",
        agendaId, model, text, vector);

    public IReadOnlyList<Slide> GetSlides(long talkId) => Query(
        "SELECT id,talk_id,t,text,text_status FROM slides WHERE talk_id=$a ORDER BY t",
        r => new Slide(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetString(4)), talkId);

    public IReadOnlyList<Slide> GetPendingSlides(long talkId) =>
        GetSlides(talkId).Where(s => s.TextStatus == "pending").ToList();

    public IReadOnlyList<SearchHit> Search(long talkId, string ftsQuery, int limit) => ftsQuery.Length == 0 ? [] : Query(
        "SELECT kind,ref_id,bm25(search) FROM search WHERE search MATCH $a AND talk_id=$b ORDER BY bm25(search) LIMIT $c",
        r => new SearchHit(r.GetString(0), r.GetInt64(1), r.GetDouble(2)), ftsQuery, talkId, limit);

    /// <summary>Holds no name, address or device: the queue cannot tell who asked what.</summary>
    public SpeakerQuestion AddQuestion(long talkId, long t, string text)
    {
        lock (_gate)
        {
            var id = Insert("INSERT INTO speaker_questions(talk_id,t,text,group_id,status) VALUES($a,$b,$c,0,'open')", talkId, t, text);
            Exec("UPDATE speaker_questions SET group_id=id WHERE id=$a", id);
            return new SpeakerQuestion(id, talkId, t, text, id, "open");
        }
    }

    public IReadOnlyList<SpeakerQuestion> ListQuestions(long talkId) => Query(
        "SELECT id,talk_id,t,text,group_id,status FROM speaker_questions WHERE talk_id=$a ORDER BY id",
        r => new SpeakerQuestion(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetInt64(4), r.GetString(5)), talkId);

    public void AssignGroup(long id, long groupId, string status) =>
        Exec("UPDATE speaker_questions SET group_id=$a, status=$b WHERE id=$c", groupId, status, id);

    /// <summary>The speaker screen shows one question, so showing a new one puts the previous back in the queue.</summary>
    public void SetGroupStatus(long talkId, long groupId, string status)
    {
        lock (_gate)
        {
            if (status == "shown") Exec("UPDATE speaker_questions SET status='open' WHERE talk_id=$a AND status='shown'", talkId);
            Exec("UPDATE speaker_questions SET status=$a WHERE talk_id=$b AND group_id=$c", status, talkId, groupId);
        }
    }

    public int CountOpenGroups(long talkId) => Query(
        "SELECT COUNT(DISTINCT group_id) FROM speaker_questions WHERE talk_id=$a AND status IN ('open','shown')", r => r.GetInt32(0), talkId)[0];

    public void Dispose() => _db.Dispose();

    private SqliteCommand Prepare(string sql, object[] args)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        for (var i = 0; i < args.Length; i++)
            cmd.Parameters.AddWithValue("$" + (char)('a' + i), args[i]);
        return cmd;
    }

    private void Exec(string sql, params object[] args)
    {
        lock (_gate)
        {
            using var cmd = Prepare(sql, args);
            cmd.ExecuteNonQuery();
        }
    }

    private long Insert(string sql, params object[] args)
    {
        lock (_gate)
        {
            using var cmd = Prepare(sql + "; SELECT last_insert_rowid();", args);
            return (long)cmd.ExecuteScalar()!;
        }
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params object[] args)
    {
        lock (_gate)
        {
            using var cmd = Prepare(sql, args);
            using var reader = cmd.ExecuteReader();
            var rows = new List<T>();
            while (reader.Read()) rows.Add(map(reader));
            return rows;
        }
    }
}
