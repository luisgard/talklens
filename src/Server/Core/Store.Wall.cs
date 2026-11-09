namespace TalkLens.Core;

public sealed record Reaction(long T, string Kind);

/// <param name="Status">pending | approved | rejected | reported</param>
/// <param name="Flag">Why the AI held it back, empty if it did not.</param>
public sealed record Photo(long Id, long RoomId, long? TalkId, string Voter, long At, string Status, string Alt, string Flag, int Likes);

/// <summary>Reactions during a talk, and the photo wall. Who reacted or liked is kept only to count people, never shown.</summary>
public sealed partial class Store
{
    private void CreateWallTables() => Exec("""
        CREATE TABLE IF NOT EXISTS reactions(talk_id INTEGER NOT NULL, t INTEGER NOT NULL, kind TEXT NOT NULL, voter TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS reactions_talk ON reactions(talk_id, t);
        CREATE TABLE IF NOT EXISTS photos(
          id INTEGER PRIMARY KEY, room_id INTEGER NOT NULL, talk_id INTEGER, voter TEXT NOT NULL, at INTEGER NOT NULL,
          status TEXT NOT NULL, alt TEXT NOT NULL DEFAULT '', flag TEXT NOT NULL DEFAULT '');
        CREATE TABLE IF NOT EXISTS photo_likes(photo_id INTEGER NOT NULL, voter TEXT NOT NULL, PRIMARY KEY(photo_id, voter));
        """);

    /// <summary>Photos belong to the event, so they outlive a talk that is deleted.</summary>
    private void DeleteTalkWall(long talkId)
    {
        Exec("DELETE FROM reactions WHERE talk_id=$a", talkId);
        Exec("UPDATE photos SET talk_id=NULL WHERE talk_id=$a", talkId);
    }

    // ----- Reactions -----
    public void AddReaction(long talkId, long t, string kind, string voter) =>
        Exec("INSERT INTO reactions(talk_id,t,kind,voter) VALUES($a,$b,$c,$d)", talkId, t, kind, voter);

    public IReadOnlyList<Reaction> ListReactions(long talkId) =>
        Query("SELECT t,kind FROM reactions WHERE talk_id=$a ORDER BY t", r => new Reaction(r.GetInt64(0), r.GetString(1)), talkId);

    public int CountReactors(long talkId) =>
        Query("SELECT COUNT(DISTINCT voter) FROM reactions WHERE talk_id=$a", r => r.GetInt32(0), talkId)[0];

    // ----- Photos -----
    private const string PhotoColumns = "id,room_id,talk_id,voter,at,status,alt,flag,(SELECT COUNT(*) FROM photo_likes WHERE photo_id=photos.id)";

    private static Photo ReadPhoto(Microsoft.Data.Sqlite.SqliteDataReader r) => new(r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetInt64(2),
        r.GetString(3), r.GetInt64(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetInt32(8));

    public Photo AddPhoto(long roomId, long? talkId, string voter)
    {
        var now = Now();
        var id = Insert("INSERT INTO photos(room_id,talk_id,voter,at,status) VALUES($a,$b,$c,$d,'pending')", roomId, talkId is { } talk ? talk : DBNull.Value, voter, now);
        return new Photo(id, roomId, talkId, voter, now, "pending", "", "", 0);
    }

    public Photo? FindPhoto(long id) => Query($"SELECT {PhotoColumns} FROM photos WHERE id=$a", ReadPhoto, id).FirstOrDefault();

    /// <param name="status">One status, or null for all of them. Newest first.</param>
    public IReadOnlyList<Photo> ListPhotos(string? status = null, long? talkId = null) => Query(
        $"SELECT {PhotoColumns} FROM photos WHERE ($a IS NULL OR status=$a) AND ($b IS NULL OR talk_id=$b) ORDER BY id DESC", ReadPhoto,
        status ?? (object)DBNull.Value, talkId is { } talk ? talk : DBNull.Value);

    public void SetPhotoStatus(long id, string status) => Exec("UPDATE photos SET status=$a WHERE id=$b", status, id);

    public void SetPhotoReview(long id, string status, string alt, string flag) =>
        Exec("UPDATE photos SET status=$a, alt=$b, flag=$c WHERE id=$d", status, alt, flag, id);

    public int CountPhotosSince(string voter, long since) =>
        Query("SELECT COUNT(*) FROM photos WHERE voter=$a AND at>=$b", r => r.GetInt32(0), voter, since)[0];

    public void DeletePhoto(long id)
    {
        Exec("DELETE FROM photo_likes WHERE photo_id=$a", id);
        Exec("DELETE FROM photos WHERE id=$a", id);
    }

    /// <summary>A second like from the same phone takes it back.</summary>
    public bool ToggleLike(long photoId, string voter)
    {
        var had = Query("SELECT 1 FROM photo_likes WHERE photo_id=$a AND voter=$b", r => 1, photoId, voter).Count > 0;
        if (had) Exec("DELETE FROM photo_likes WHERE photo_id=$a AND voter=$b", photoId, voter);
        else Exec("INSERT INTO photo_likes(photo_id,voter) VALUES($a,$b)", photoId, voter);
        return !had;
    }

    public IReadOnlyList<long> LikedBy(string voter) =>
        Query("SELECT photo_id FROM photo_likes WHERE voter=$a", r => r.GetInt64(0), voter);
}
