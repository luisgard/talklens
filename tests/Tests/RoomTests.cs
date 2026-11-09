using Microsoft.Data.Sqlite;
using TalkLens.Core;
using Xunit;

namespace TalkLens.Tests;

public sealed class RoomTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"talklens-rooms-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    [Fact]
    public void A_new_event_starts_with_one_room()
    {
        using var store = new Store(_dbPath);
        Assert.Equal("Room 1", Assert.Single(store.ListRooms()).Name);
    }

    [Fact]
    public void Talks_remember_their_room_and_outlive_it()
    {
        using var store = new Store(_dbPath);
        var second = store.AddRoom("Main hall");
        var talk = store.CreateTalk("Demo", "Ada", "es", true, second.Id);

        store.DeleteRoom(second.Id);

        Assert.Equal(second.Id, Assert.Single(store.ListTalks()).RoomId);
        Assert.Equal(talk.Id, store.ListTalks()[0].Id);
    }

    [Fact]
    public void Renaming_changes_only_that_room()
    {
        using var store = new Store(_dbPath);
        var second = store.AddRoom("Room 2");
        store.RenameRoom(second.Id, "Workshop");

        Assert.Equal(["Room 1", "Workshop"], store.ListRooms().Select(r => r.Name));
    }

    [Fact]
    public void A_database_from_before_rooms_keeps_its_talks_in_the_first_room()
    {
        using (var old = new SqliteConnection($"Data Source={_dbPath}"))
        {
            old.Open();
            using var cmd = old.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE talks(id INTEGER PRIMARY KEY, title TEXT NOT NULL, speaker TEXT NOT NULL, language TEXT NOT NULL,
                  started_at INTEGER NOT NULL, ended_at INTEGER, keep_after INTEGER NOT NULL);
                INSERT INTO talks(title,speaker,language,started_at,ended_at,keep_after) VALUES('Old','Ada','es',1,2,1);
                """;
            cmd.ExecuteNonQuery();
        }

        using var store = new Store(_dbPath);

        var talk = Assert.Single(store.ListTalks());
        Assert.Equal("Old", talk.Title);
        Assert.Equal(store.ListRooms()[0].Id, talk.RoomId);
    }

    [Fact]
    public void Presence_counts_and_languages_stay_within_a_room()
    {
        var presence = new Presence();
        presence.Join("a", 1, "en");
        presence.Join("b", 1, "");
        presence.Join("c", 2, "es");

        Assert.Equal(3, presence.Count);
        Assert.Equal(2, presence.CountIn(1));
        Assert.Equal(["en"], presence.ActiveLanguages(1));
        Assert.Equal(["es"], presence.ActiveLanguages(2));

        // Moving to another room takes the person out of the first one.
        presence.Join("a", 2, "");
        Assert.Equal(1, presence.CountIn(1));
        Assert.Empty(presence.ActiveLanguages(1));

        presence.Leave("c");
        Assert.Empty(presence.ActiveLanguages(2));
    }
}
