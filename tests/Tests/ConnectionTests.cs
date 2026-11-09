using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using TalkLens.Ai;
using TalkLens.Core;
using Xunit;

namespace TalkLens.Tests;

public sealed class ConnectionTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"talklens-test-{Guid.NewGuid():N}.db");
    private readonly Store _store;

    public ConnectionTests() => _store = new Store(_dbPath);

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    private Attendee Person(string name, string seeking = "", string offering = "")
    {
        var person = _store.AddProfile(name);
        _store.SetProfile(person.Id, name, seeking, offering, true, "", false);
        return _store.FindAttendee(person.Id)!;
    }

    // ----- Requests -----
    [Fact]
    public void A_pair_has_one_link_whoever_asks()
    {
        var (ada, alan) = (Person("Ada"), Person("Alan"));
        Assert.NotNull(_store.RequestConnection(ada.Id, alan.Id, "Kubernetes"));
        Assert.Null(_store.RequestConnection(ada.Id, alan.Id, "again"));
        Assert.Null(_store.RequestConnection(alan.Id, ada.Id, "the other way"));
        Assert.Equal(1, _store.CountPendingFrom(ada.Id));
    }

    [Fact]
    public void Only_the_person_asked_answers_and_only_once()
    {
        var (ada, alan) = (Person("Ada"), Person("Alan"));
        var link = _store.RequestConnection(ada.Id, alan.Id, "")!;

        Assert.False(_store.AnswerConnection(link.Id, ada.Id, true, "Coffee"));
        Assert.True(_store.AnswerConnection(link.Id, alan.Id, true, "Coffee"));
        Assert.False(_store.AnswerConnection(link.Id, alan.Id, false, ""));

        var answered = _store.FindConnection(link.Id)!;
        Assert.Equal((ConnectionStatus.Accepted, "Coffee"), (answered.Status, answered.Spot));
        Assert.Equal(0, _store.CountPendingFrom(ada.Id));
    }

    [Fact]
    public void Only_an_accepted_request_becomes_a_meeting_and_only_then_is_it_rated()
    {
        var (ada, alan, grace) = (Person("Ada"), Person("Alan"), Person("Grace"));
        var link = _store.RequestConnection(ada.Id, alan.Id, "")!;
        var declined = _store.RequestConnection(ada.Id, grace.Id, "")!;
        _store.AnswerConnection(declined.Id, grace.Id, false, "");

        Assert.False(_store.MarkMet(ada.Id, alan.Id));
        Assert.False(_store.SetUseful(link.Id, ada.Id, 2));
        _store.AnswerConnection(link.Id, alan.Id, true, "");
        Assert.True(_store.MarkMet(alan.Id, ada.Id));
        Assert.False(_store.MarkMet(ada.Id, grace.Id));

        Assert.True(_store.SetUseful(link.Id, ada.Id, 2));
        Assert.True(_store.SetUseful(link.Id, alan.Id, 1));
        Assert.False(_store.SetUseful(link.Id, grace.Id, 0));
        var met = _store.FindConnection(link.Id)!;
        Assert.Equal((ConnectionStatus.Met, 2, 1), (met.Status, met.UsefulBy(ada.Id), met.UsefulBy(alan.Id)));
        Assert.NotNull(met.MetAt);
    }

    [Fact]
    public void Deleting_a_person_takes_their_links_and_vectors_with_them()
    {
        var (ada, alan) = (Person("Ada", "Go"), Person("Alan"));
        _store.RequestConnection(ada.Id, alan.Id, "");
        _store.SetProfileVector(ada.Id, Matchmaker.Seeking, "m", "Go", [1, 2, 3, 4]);

        _store.DeleteAttendee(ada.Id);

        Assert.Empty(_store.ListConnectionsOf(alan.Id));
        Assert.Empty(_store.ReadProfileVectors());
    }

    [Fact]
    public void Meeting_spots_are_one_per_line()
    {
        _store.SetSetting(Settings.MeetingSpots, "Coffee area\n\n  Sponsor tables \n");
        Assert.Equal(["Coffee area", "Sponsor tables"], _store.MeetingSpots());
    }

    // ----- Matching -----
    [Theory]
    [InlineData("Ayuda para migrar a Kubernetes", "Migrating teams to Kubernetes in production", true)]
    [InlineData("Un trabajo de backend", "Hiring backend developers", true)]
    [InlineData("Diseño de interfaces", "Postgres tuning", false)]
    [InlineData("", "Anything", false)]
    public void Words_match_across_word_endings_accents_and_languages(string seeking, string offering, bool matches) =>
        Assert.Equal(matches, Matchmaker.WordMatch(seeking, offering) >= 0.34);

    private static IReadOnlySet<long> None(long _) => new HashSet<long>();

    [Fact]
    public void Who_offers_what_you_seek_and_seeks_what_you_offer_comes_first()
    {
        var me = Person("Me", "Help moving to Kubernetes", "Postgres in production");
        var both = Person("Both", "Postgres tuning help", "Kubernetes migrations");
        var half = Person("Half", "Design feedback", "Kubernetes operators");
        var none = Person("None", "Marketing", "Sales");

        var ranked = Matchmaker.Rank(me, [none, half, both], _store.ReadProfileVectors(), "", None, new Dictionary<long, string>());

        Assert.Equal(["Both", "Half", "None"], ranked.Select(m => m.Person.Name));
        Assert.True(ranked[0].OffersWhatYouSeek && ranked[0].SeeksWhatYouOffer);
        Assert.True(ranked[1].OffersWhatYouSeek && !ranked[1].SeeksWhatYouOffer);
        Assert.False(ranked[2].OffersWhatYouSeek || ranked[2].SeeksWhatYouOffer);
    }

    [Fact]
    public void Meaning_finds_a_match_with_no_word_in_common()
    {
        var me = Person("Me", "Someone to review my cluster", "");
        var other = Person("Other", "", "Ten years as an SRE");
        float[] close = [0.8f, 0.6f];  // 0.96 together: well above the threshold
        float[] alsoClose = [0.6f, 0.8f];
        _store.SetProfileVector(me.Id, Matchmaker.Seeking, "m", me.Seeking, MemoryMarshal.AsBytes(close.AsSpan()).ToArray());
        _store.SetProfileVector(other.Id, Matchmaker.Offering, "m", other.Offering, MemoryMarshal.AsBytes(alsoClose.AsSpan()).ToArray());

        var match = Assert.Single(Matchmaker.Rank(me, [other], _store.ReadProfileVectors(), "m", None, new Dictionary<long, string>()));

        Assert.True(match.OffersWhatYouSeek);
        Assert.True(match.ByMeaning);
        // A vector from another model, or for text since changed, is never compared.
        Assert.False(Matchmaker.Rank(me, [other], _store.ReadProfileVectors(), "other-model", None, new Dictionary<long, string>())[0].OffersWhatYouSeek);
    }

    [Fact]
    public void A_shared_talk_is_a_reason_and_a_small_push()
    {
        var me = Person("Me", "Rust", "");
        var (first, second) = (Person("Ana", "", "Gardening"), Person("Bea", "", "Cooking"));
        var talks = new Dictionary<long, IReadOnlySet<long>> { [me.Id] = new HashSet<long> { 7 }, [second.Id] = new HashSet<long> { 7 } };

        var ranked = Matchmaker.Rank(me, [first, second], _store.ReadProfileVectors(), "", id => talks.GetValueOrDefault(id) ?? new HashSet<long>(),
            new Dictionary<long, string> { [7] = "Async Rust" });

        Assert.Equal("Bea", ranked[0].Person.Name);
        Assert.Equal(["Async Rust"], ranked[0].SharedTalks);
    }

    [Fact]
    public void Elasticsearch_and_the_local_order_are_fused_by_rank()
    {
        var people = new[] { Person("A"), Person("B"), Person("C") };
        var local = people.Select(p => new PersonMatch(p, 0, false, false, [], false)).ToList();
        // Elasticsearch puts C first and B second. Found by both, they pass A, which only the local order knew.
        var fused = Matchmaker.Fuse(local, [people[2].Id, people[1].Id]);
        Assert.Equal(["C", "B", "A"], fused.Select(m => m.Person.Name));
    }
}
