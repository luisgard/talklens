using Microsoft.Data.Sqlite;

namespace TalkLens.Core;

/// <summary>A request from one person to another, and what became of it. Each step is kept, so a report can tell interest from a real meeting.</summary>
/// <param name="FromUseful">After meeting, each side says whether it helped: 0 no, 1 a little, 2 yes. Null until they answer.</param>
public sealed record Connection(
    long Id, long FromId, long ToId, string Reason, string Status, string Spot, long CreatedAt, long? AnsweredAt, long? MetAt, int? FromUseful, int? ToUseful)
{
    public long Other(long me) => me == FromId ? ToId : FromId;
    public int? UsefulBy(long me) => me == FromId ? FromUseful : ToUseful;
}

public static class ConnectionStatus
{
    public const string Requested = "requested";
    public const string Accepted = "accepted";
    public const string Declined = "declined";
    public const string Met = "met";
}

public sealed partial class Store
{
    private void CreateConnectionTables() => Exec("""
        CREATE TABLE IF NOT EXISTS connections(
          id INTEGER PRIMARY KEY, from_id INTEGER NOT NULL, to_id INTEGER NOT NULL, reason TEXT NOT NULL, status TEXT NOT NULL,
          spot TEXT NOT NULL DEFAULT '', created_at INTEGER NOT NULL, answered_at INTEGER, met_at INTEGER, from_useful INTEGER, to_useful INTEGER,
          UNIQUE(from_id, to_id));
        CREATE TABLE IF NOT EXISTS profile_vectors(
          attendee_id INTEGER NOT NULL, field TEXT NOT NULL, model TEXT NOT NULL, text TEXT NOT NULL, vector BLOB NOT NULL,
          PRIMARY KEY(attendee_id, field));
        """);

    private const string ConnectionColumns = "id,from_id,to_id,reason,status,spot,created_at,answered_at,met_at,from_useful,to_useful";

    private static Connection ReadConnection(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt64(6),
        r.IsDBNull(7) ? null : r.GetInt64(7), r.IsDBNull(8) ? null : r.GetInt64(8), r.IsDBNull(9) ? null : r.GetInt32(9), r.IsDBNull(10) ? null : r.GetInt32(10));

    /// <summary>One link per pair, whoever asked first. Null when the pair already has one.</summary>
    public Connection? RequestConnection(long from, long to, string reason)
    {
        lock (_gate)
        {
            if (FindConnectionBetween(from, to) != null) return null;
            return FindConnection(Insert("INSERT INTO connections(from_id,to_id,reason,status,created_at) VALUES($a,$b,$c,$d,$e)",
                from, to, reason, ConnectionStatus.Requested, Now()));
        }
    }

    public Connection? FindConnection(long id) => Query($"SELECT {ConnectionColumns} FROM connections WHERE id=$a", ReadConnection, id).FirstOrDefault();

    public Connection? FindConnectionBetween(long a, long b) =>
        Query($"SELECT {ConnectionColumns} FROM connections WHERE (from_id=$a AND to_id=$b) OR (from_id=$b AND to_id=$a)", ReadConnection, a, b).FirstOrDefault();

    public IReadOnlyList<Connection> ListConnectionsOf(long attendee) =>
        Query($"SELECT {ConnectionColumns} FROM connections WHERE from_id=$a OR to_id=$a ORDER BY created_at DESC", ReadConnection, attendee);

    public IReadOnlyList<Connection> ListConnections() => Query($"SELECT {ConnectionColumns} FROM connections ORDER BY created_at", ReadConnection);

    public int CountPendingFrom(long attendee) =>
        Query("SELECT count(*) FROM connections WHERE from_id=$a AND status=$b", r => r.GetInt32(0), attendee, ConnectionStatus.Requested)[0];

    /// <summary>Only the person asked can answer, and only once.</summary>
    public bool AnswerConnection(long id, long answeredBy, bool accept, string spot) =>
        Change("UPDATE connections SET status=$a, spot=$b, answered_at=$c WHERE id=$d AND to_id=$e AND status=$f",
            accept ? ConnectionStatus.Accepted : ConnectionStatus.Declined, accept ? spot : "", Now(), id, answeredBy, ConnectionStatus.Requested);

    /// <summary>Called when one scans the other's code in person. Only an accepted request becomes a meeting.</summary>
    public bool MarkMet(long a, long b) =>
        Change("UPDATE connections SET status=$a, met_at=$b WHERE ((from_id=$c AND to_id=$d) OR (from_id=$d AND to_id=$c)) AND status=$e",
            ConnectionStatus.Met, Now(), a, b, ConnectionStatus.Accepted);

    public bool SetUseful(long id, long by, int answer) =>
        Change("UPDATE connections SET from_useful=$a WHERE id=$b AND from_id=$c AND status=$d", answer, id, by, ConnectionStatus.Met)
        || Change("UPDATE connections SET to_useful=$a WHERE id=$b AND to_id=$c AND status=$d", answer, id, by, ConnectionStatus.Met);

    private bool Change(string sql, params object[] args)
    {
        lock (_gate)
        {
            using var cmd = Prepare(sql, args);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    // ----- What people are after and offer, by meaning -----
    public IReadOnlyDictionary<(long Attendee, string Field), (string Model, string Text, byte[] Vector)> ReadProfileVectors() => Query(
        "SELECT attendee_id,field,model,text,vector FROM profile_vectors",
        r => (Key: (r.GetInt64(0), r.GetString(1)), Value: (r.GetString(2), r.GetString(3), (byte[])r[4])))
        .ToDictionary(row => row.Key, row => row.Value);

    public void SetProfileVector(long attendee, string field, string model, string text, byte[] vector) => Exec(
        "INSERT OR REPLACE INTO profile_vectors(attendee_id,field,model,text,vector) SELECT $a,$b,$c,$d,$e WHERE EXISTS(SELECT 1 FROM attendees WHERE id=$a)",
        attendee, field, model, text, vector);

    /// <summary>The organizer's list, one per line. Empty means the phones offer a few of their own.</summary>
    public IReadOnlyList<string> MeetingSpots() =>
        GetSetting(Settings.MeetingSpots).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
