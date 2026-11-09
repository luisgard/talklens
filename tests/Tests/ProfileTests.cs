using Microsoft.Data.Sqlite;
using TalkLens.Core;
using Xunit;

namespace TalkLens.Tests;

public sealed class ProfileTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"talklens-test-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    [Fact]
    public void Many_people_can_have_a_profile_without_an_email()
    {
        using var store = new Store(_dbPath);
        var ada = store.AddProfile("Ada");
        var alan = store.AddProfile("Alan");

        Assert.NotEqual(ada.Token, alan.Token);
        Assert.Equal(("", "profile"), (ada.Email, ada.Source));
        Assert.Null(store.FindAttendeeByEmail(""));
        // A ticket made later by email is still its own person.
        Assert.True(store.UpsertAttendee("Grace", "grace@example.com", "self").Created);
    }

    [Fact]
    public void A_profile_keeps_what_the_person_is_after_and_offers()
    {
        using var store = new Store(_dbPath);
        var ada = store.AddProfile("Ada");

        Assert.True(store.SetProfile(ada.Id, "Ada L", "Help moving to Kubernetes", "Postgres in production", true, "Ada@Example.com", true));

        var saved = store.FindAttendee(ada.Id)!;
        Assert.Equal(("Ada L", "Help moving to Kubernetes", "Postgres in production", true, "ada@example.com", true),
            (saved.Name, saved.Seeking, saved.Offering, saved.Networking, saved.Email, saved.WantsSummary));
    }

    [Fact]
    public void Someone_elses_email_is_never_taken_over()
    {
        using var store = new Store(_dbPath);
        var (grace, _) = store.UpsertAttendee("Grace", "grace@example.com", "csv");
        var stranger = store.AddProfile("Stranger");

        Assert.False(store.SetProfile(stranger.Id, "Stranger", "", "", false, "GRACE@example.com", false));
        Assert.Equal("", store.FindAttendee(stranger.Id)!.Email);
        Assert.Equal(grace.Id, store.FindAttendeeByEmail("grace@example.com")!.Id);
    }

    [Fact]
    public void An_email_can_be_removed_again()
    {
        using var store = new Store(_dbPath);
        var ada = store.AddProfile("Ada");
        store.SetProfile(ada.Id, "Ada", "", "", false, "ada@example.com", true);
        store.SetProfile(ada.Id, "Ada", "", "", false, "", false);
        Assert.Equal("", store.FindAttendee(ada.Id)!.Email);
        Assert.True(store.AddProfile("Alan").Id > 0);
    }

    [Fact]
    public void An_older_database_keeps_its_people_and_accepts_profiles()
    {
        // The table as it was before profiles, when every attendee needed an email.
        using (var old = new SqliteConnection($"Data Source={_dbPath}"))
        {
            old.Open();
            using var create = old.CreateCommand();
            create.CommandText = """
                CREATE TABLE attendees(
                  id INTEGER PRIMARY KEY, name TEXT NOT NULL, email TEXT NOT NULL UNIQUE COLLATE NOCASE, token TEXT NOT NULL UNIQUE,
                  checked_in_at INTEGER, source TEXT NOT NULL, luma_guest_id TEXT NOT NULL DEFAULT '', luma_qr TEXT NOT NULL DEFAULT '',
                  interests TEXT NOT NULL DEFAULT '', networking INTEGER NOT NULL DEFAULT 0, created_at INTEGER NOT NULL,
                  contact_code TEXT NOT NULL DEFAULT '', contact_code_until INTEGER NOT NULL DEFAULT 0);
                INSERT INTO attendees(name,email,token,source,interests,networking,created_at) VALUES('Ada','ada@example.com','tok-ada','self','go, k8s',1,1);
                """;
            create.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using var store = new Store(_dbPath);
        var ada = store.FindAttendeeByToken("tok-ada")!;
        Assert.Equal(("Ada", "ada@example.com", "go, k8s", true), (ada.Name, ada.Email, ada.Interests, ada.Networking));
        Assert.Equal("", store.AddProfile("Alan").Email);
        Assert.Equal("", store.AddProfile("Grace").Email);
    }
}
