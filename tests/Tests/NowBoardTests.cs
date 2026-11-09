using Microsoft.Data.Sqlite;
using TalkLens.Core;
using Xunit;

namespace TalkLens.Tests;

public sealed class NowBoardTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"talklens-test-{Guid.NewGuid():N}.db");
    private readonly Store _store;
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static readonly long Later = (long)TimeSpan.FromMinutes(20).TotalMilliseconds;

    public NowBoardTests() => _store = new Store(_dbPath);

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    private Attendee Person(string name, bool visible = true)
    {
        var person = _store.AddProfile(name);
        _store.SetProfile(person.Id, name, "", "", visible, "", false);
        return person;
    }

    [Fact]
    public void Only_an_agreed_or_real_meeting_counts_as_connected_and_hidden_people_are_left_out()
    {
        var (ada, alan, grace, linus) = (Person("Ada"), Person("Alan"), Person("Grace"), Person("Linus"));
        Person("Hidden", visible: false);
        var link = _store.RequestConnection(ada.Id, alan.Id, "")!;
        _store.RequestConnection(grace.Id, linus.Id, "");  // asked, not answered: still alone
        _store.AnswerConnection(link.Id, alan.Id, true, "Coffee");

        var now = NowBoard.Compute(_store, Now);

        Assert.Equal(4, now.Visible);
        Assert.Equal(["Grace", "Linus"], now.Alone);
        var meeting = Assert.Single(now.NotConfirmed);
        Assert.Equal(("Ada", "Alan", "Coffee"), (meeting.From, meeting.To, meeting.Spot));

        _store.MarkMet(ada.Id, alan.Id);
        Assert.Empty(NowBoard.Compute(_store, Now).NotConfirmed);
    }

    [Fact]
    public void A_sponsor_request_shows_once_it_waited_15_minutes_and_leaves_when_attended()
    {
        var sponsor = _store.AddSponsor("Acme", "");
        _store.SetOffer(sponsor.Id, OfferKind.Demo, "Demo", "", []);
        var request = _store.RequestOffer(sponsor.Id, Person("Ada").Id, "", false, "")!;

        Assert.Empty(NowBoard.Compute(_store, Now).SponsorsWaiting);
        var waiting = Assert.Single(NowBoard.Compute(_store, Now + Later).SponsorsWaiting);
        Assert.Equal(("Acme", "Ada", sponsor.Key), (waiting.Sponsor, waiting.Person, waiting.SponsorKey));
        Assert.True(waiting.Minutes >= 19);

        _store.MarkAttended(request.Id, sponsor.Id, true);
        Assert.Empty(NowBoard.Compute(_store, Now + Later).SponsorsWaiting);
    }

    [Fact]
    public void An_announcement_lasts_five_minutes_or_until_stopped()
    {
        var announcements = new Announcements();
        announcements.Start("intro-round", 1000);
        Assert.Equal("intro-round", announcements.Current(1000 + 60_000)?.Kind);
        Assert.Null(announcements.Current(1000 + (long)Announcements.Lasts.TotalMilliseconds));

        announcements.Start("intro-round", 1000);
        announcements.Stop();
        Assert.Null(announcements.Current(2000));
    }
}
