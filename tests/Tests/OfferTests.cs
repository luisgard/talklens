using Microsoft.Data.Sqlite;
using TalkLens.Ai;
using TalkLens.Core;
using Xunit;

namespace TalkLens.Tests;

public sealed class OfferTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"talklens-test-{Guid.NewGuid():N}.db");
    private readonly Store _store;

    public OfferTests() => _store = new Store(_dbPath);

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    private Attendee Person(string name, string seeking = "")
    {
        var person = _store.AddProfile(name);
        _store.SetProfile(person.Id, name, seeking, "", true, "", false);
        return _store.FindAttendee(person.Id)!;
    }

    private Sponsor SponsorWith(string name, string title, params string[] slots)
    {
        var sponsor = _store.AddSponsor(name, "");
        _store.SetOffer(sponsor.Id, OfferKind.Chat, title, "", slots);
        return sponsor;
    }

    [Fact]
    public void An_offer_is_saved_with_its_slots_and_an_empty_title_takes_it_down()
    {
        var acme = SponsorWith("Acme", "Review your cluster", " 11:00 ", "", "11:15", "11:00");
        Assert.Equal(["11:00", "11:15"], _store.FindOffer(acme.Id)!.Slots);

        _store.SetOffer(acme.Id, OfferKind.Demo, "", "", []);
        Assert.Null(_store.FindOffer(acme.Id));
    }

    [Fact]
    public void One_request_per_person_and_one_person_per_slot()
    {
        var acme = SponsorWith("Acme", "Review", "11:00", "11:15");
        var (ada, alan) = (Person("Ada"), Person("Alan"));

        Assert.Null(_store.RequestOffer(acme.Id, ada.Id, "", false, ""));       // a slot is needed
        Assert.Null(_store.RequestOffer(acme.Id, ada.Id, "12:00", false, "")); // and must be one of theirs
        Assert.NotNull(_store.RequestOffer(acme.Id, ada.Id, "11:00", true, "Hi"));
        Assert.Null(_store.RequestOffer(acme.Id, ada.Id, "11:15", false, ""));
        Assert.Null(_store.RequestOffer(acme.Id, alan.Id, "11:00", false, ""));
        Assert.NotNull(_store.RequestOffer(acme.Id, alan.Id, "11:15", false, ""));
        Assert.Equal(new HashSet<string> { "11:00", "11:15" }, _store.TakenSlots(acme.Id));
    }

    [Fact]
    public void Without_slots_anyone_can_ask_once()
    {
        var acme = SponsorWith("Acme", "Beta access");
        var request = _store.RequestOffer(acme.Id, Person("Ada").Id, "", false, "")!;
        Assert.NotNull(_store.RequestOffer(acme.Id, Person("Alan").Id, "", false, ""));
        Assert.Equal(("", false), (request.Slot, request.ShareEmail));
    }

    [Fact]
    public void Relevant_only_after_attended_and_only_by_that_sponsor()
    {
        var (acme, other) = (SponsorWith("Acme", "Review", []), SponsorWith("Other", "Demo"));
        var request = _store.RequestOffer(acme.Id, Person("Ada").Id, "", false, "")!;

        Assert.False(_store.SetRelevant(request.Id, acme.Id, true));
        Assert.False(_store.MarkAttended(request.Id, other.Id, true));
        Assert.True(_store.MarkAttended(request.Id, acme.Id, true));
        Assert.True(_store.SetRelevant(request.Id, acme.Id, true));
        Assert.Equal(true, _store.FindOfferRequest(request.Id)!.Relevant);

        // Undoing attended clears relevant too: it only means something after a conversation.
        _store.MarkAttended(request.Id, acme.Id, false);
        var undone = _store.FindOfferRequest(request.Id)!;
        Assert.Equal((null, null), (undone.AttendedAt, undone.Relevant));
    }

    [Fact]
    public void A_person_can_take_back_a_request_until_it_is_attended()
    {
        var acme = SponsorWith("Acme", "Review", "11:00");
        var ada = Person("Ada");
        var first = _store.RequestOffer(acme.Id, ada.Id, "11:00", false, "")!;
        Assert.True(_store.CancelOfferRequest(first.Id, ada.Id));
        Assert.Empty(_store.TakenSlots(acme.Id));

        var second = _store.RequestOffer(acme.Id, ada.Id, "11:00", false, "")!;
        _store.MarkAttended(second.Id, acme.Id, true);
        Assert.False(_store.CancelOfferRequest(second.Id, ada.Id));
    }

    [Fact]
    public void Deleting_a_person_or_a_sponsor_takes_the_requests_with_them()
    {
        var acme = SponsorWith("Acme", "Review");
        var other = SponsorWith("Other", "Demo");
        var (ada, alan) = (Person("Ada"), Person("Alan"));
        _store.RequestOffer(acme.Id, ada.Id, "", false, "");
        _store.RequestOffer(other.Id, alan.Id, "", false, "");

        _store.DeleteAttendee(ada.Id);
        Assert.Empty(_store.ListOfferRequests(acme.Id));

        _store.DeleteSponsor(other.Id);
        Assert.Empty(_store.ListAllOfferRequests());
        Assert.Null(_store.FindOffer(other.Id));
    }

    [Fact]
    public void Offers_that_fit_what_you_seek_come_first_and_Elasticsearch_order_counts()
    {
        var k8s = SponsorWith("CloudCo", "Free review of your Kubernetes cluster");
        var db = SponsorWith("DataCo", "Postgres tuning in 10 minutes");
        var offers = new[] { k8s, db }.Select(s => (_store.FindOffer(s.Id)!, s)).ToList();

        var local = Matchmaker.RankOffers("Ayuda con Kubernetes", offers, null);
        Assert.Equal(["CloudCo", "DataCo"], local.Select(m => m.Sponsor.Name));
        Assert.Equal([true, false], local.Select(m => m.Fits));

        // Elasticsearch found DataCo (say, by a synonym) and nothing else: both fit now.
        var fused = Matchmaker.RankOffers("Ayuda con Kubernetes", offers, [db.Id]);
        Assert.All(fused, m => Assert.True(m.Fits));
    }
}
