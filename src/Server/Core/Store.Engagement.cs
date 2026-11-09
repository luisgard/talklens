namespace TalkLens.Core;

public sealed record Poll(long Id, long TalkId, string Question, IReadOnlyList<string> Options, long OpenedAt, long? ClosedAt)
{
    public bool Open => ClosedAt == null;
}

public sealed record Rating(long TalkId, int Stars, string Comment, long At);

/// <summary>Polls, ratings, and what the AI made from a finished talk, kept so it is made once.</summary>
public sealed partial class Store
{
    private void CreateEngagementTables() => Exec("""
        CREATE TABLE IF NOT EXISTS polls(
          id INTEGER PRIMARY KEY, talk_id INTEGER NOT NULL, question TEXT NOT NULL, options TEXT NOT NULL, opened_at INTEGER NOT NULL, closed_at INTEGER);
        CREATE TABLE IF NOT EXISTS poll_votes(poll_id INTEGER NOT NULL, voter TEXT NOT NULL, choice INTEGER NOT NULL, PRIMARY KEY(poll_id, voter));
        CREATE TABLE IF NOT EXISTS ratings(
          talk_id INTEGER NOT NULL, voter TEXT NOT NULL, stars INTEGER NOT NULL, comment TEXT NOT NULL, at INTEGER NOT NULL, PRIMARY KEY(talk_id, voter));
        CREATE TABLE IF NOT EXISTS talk_extras(
          talk_id INTEGER NOT NULL, kind TEXT NOT NULL, language TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY(talk_id, kind, language));
        """);

    private void DeleteTalkEngagement(long talkId)
    {
        Exec("DELETE FROM poll_votes WHERE poll_id IN (SELECT id FROM polls WHERE talk_id=$a)", talkId);
        Exec("DELETE FROM polls WHERE talk_id=$a", talkId);
        Exec("DELETE FROM ratings WHERE talk_id=$a", talkId);
        Exec("DELETE FROM talk_extras WHERE talk_id=$a", talkId);
    }

    // ----- Polls. One open poll per talk: opening another closes the last -----
    public Poll OpenPoll(long talkId, string question, IReadOnlyList<string> options)
    {
        var now = Now();
        Exec("UPDATE polls SET closed_at=$a WHERE talk_id=$b AND closed_at IS NULL", now, talkId);
        var id = Insert("INSERT INTO polls(talk_id,question,options,opened_at) VALUES($a,$b,$c,$d)",
            talkId, question, System.Text.Json.JsonSerializer.Serialize(options), now);
        return new Poll(id, talkId, question, options, now, null);
    }

    public void ClosePoll(long pollId) => Exec("UPDATE polls SET closed_at=$a WHERE id=$b AND closed_at IS NULL", Now(), pollId);

    public Poll? FindPoll(long pollId) => Query("SELECT id,talk_id,question,options,opened_at,closed_at FROM polls WHERE id=$a", ReadPoll, pollId).FirstOrDefault();

    public IReadOnlyList<Poll> ListPolls(long talkId) =>
        Query("SELECT id,talk_id,question,options,opened_at,closed_at FROM polls WHERE talk_id=$a ORDER BY id", ReadPoll, talkId);

    private static Poll ReadPoll(Microsoft.Data.Sqlite.SqliteDataReader r) => new(r.GetInt64(0), r.GetInt64(1), r.GetString(2),
        System.Text.Json.JsonSerializer.Deserialize<List<string>>(r.GetString(3)) ?? [], r.GetInt64(4), r.IsDBNull(5) ? null : r.GetInt64(5));

    /// <summary>A phone can change its vote while the poll is open; it still counts once.</summary>
    public void Vote(long pollId, string voter, int choice) =>
        Exec("INSERT INTO poll_votes(poll_id,voter,choice) VALUES($a,$b,$c) ON CONFLICT(poll_id,voter) DO UPDATE SET choice=$c", pollId, voter, choice);

    public int[] PollCounts(Poll poll)
    {
        var counts = new int[poll.Options.Count];
        foreach (var choice in Query("SELECT choice FROM poll_votes WHERE poll_id=$a", r => r.GetInt32(0), poll.Id))
            if (choice >= 0 && choice < counts.Length) counts[choice]++;
        return counts;
    }

    public int? VoteOf(long pollId, string voter) =>
        Query("SELECT choice FROM poll_votes WHERE poll_id=$a AND voter=$b", r => (int?)r.GetInt32(0), pollId, voter).FirstOrDefault();

    // ----- Ratings: one per phone per talk, which it can change -----
    public void Rate(long talkId, string voter, int stars, string comment) =>
        Exec("INSERT INTO ratings(talk_id,voter,stars,comment,at) VALUES($a,$b,$c,$d,$e) ON CONFLICT(talk_id,voter) DO UPDATE SET stars=$c, comment=$d, at=$e",
            talkId, voter, stars, comment, Now());

    public IReadOnlyList<Rating> ListRatings(long talkId) =>
        Query("SELECT talk_id,stars,comment,at FROM ratings WHERE talk_id=$a ORDER BY at", r => new Rating(r.GetInt64(0), r.GetInt32(1), r.GetString(2), r.GetInt64(3)), talkId);

    public Rating? RatingOf(long talkId, string voter) =>
        Query("SELECT talk_id,stars,comment,at FROM ratings WHERE talk_id=$a AND voter=$b", r => new Rating(r.GetInt64(0), r.GetInt32(1), r.GetString(2), r.GetInt64(3)), talkId, voter)
            .FirstOrDefault();

    // ----- Made by the AI from a finished talk -----
    public string? GetExtra(long talkId, string kind, string language) =>
        Query("SELECT json FROM talk_extras WHERE talk_id=$a AND kind=$b AND language=$c", r => r.GetString(0), talkId, kind, language).FirstOrDefault();

    public void SetExtra(long talkId, string kind, string language, string json) =>
        Exec("INSERT OR REPLACE INTO talk_extras(talk_id,kind,language,json) VALUES($a,$b,$c,$d)", talkId, kind, language, json);
}
