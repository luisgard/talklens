using Microsoft.Data.Sqlite;

namespace TalkLens.Core;

/// <summary>A sponsor's one concrete thing to offer, such as "Bring a problem and we review it for 10 minutes".</summary>
/// <param name="Slots">Optional times. Each one can be taken by one person.</param>
public sealed record SponsorOffer(long SponsorId, string Kind, string Title, string Details, IReadOnlyList<string> Slots, long UpdatedAt);

/// <summary>
/// Someone asked for an offer. The sponsor then says whether it happened and whether it was relevant, so a report
/// counts real conversations, never visits.
/// </summary>
/// <param name="ShareEmail">The person chose to give their email. Without it the sponsor sees only their name and what they are after.</param>
/// <param name="Relevant">Null until the sponsor answers, and only after the request was attended.</param>
public sealed record OfferRequest(
    long Id, long SponsorId, long AttendeeId, string Slot, bool ShareEmail, string Note, long CreatedAt, long? AttendedAt, bool? Relevant);

public static class OfferKind
{
    public const string Demo = "demo";
    public const string Beta = "beta";
    public const string Chat = "chat";
    public const string Info = "info";
    public static readonly IReadOnlyList<string> All = [Demo, Beta, Chat, Info];
}

public sealed partial class Store
{
    private void CreateOfferTables() => Exec("""
        CREATE TABLE IF NOT EXISTS sponsor_offers(
          sponsor_id INTEGER PRIMARY KEY, kind TEXT NOT NULL, title TEXT NOT NULL, details TEXT NOT NULL, slots TEXT NOT NULL, updated_at INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS offer_requests(
          id INTEGER PRIMARY KEY, sponsor_id INTEGER NOT NULL, attendee_id INTEGER NOT NULL, slot TEXT NOT NULL, share_email INTEGER NOT NULL,
          note TEXT NOT NULL, created_at INTEGER NOT NULL, attended_at INTEGER, relevant INTEGER,
          UNIQUE(sponsor_id, attendee_id));
        """);

    private static SponsorOffer ReadOffer(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
        r.GetString(4).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), r.GetInt64(5));

    private const string OfferColumns = "sponsor_id,kind,title,details,slots,updated_at";

    /// <summary>An empty title takes the offer down. People who already asked keep their request.</summary>
    public void SetOffer(long sponsor, string kind, string title, string details, IEnumerable<string> slots)
    {
        if (title.Length == 0) { Exec("DELETE FROM sponsor_offers WHERE sponsor_id=$a", sponsor); return; }
        var lines = string.Join('\n', slots.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct());
        Exec($"INSERT OR REPLACE INTO sponsor_offers({OfferColumns}) VALUES($a,$b,$c,$d,$e,$f)", sponsor, kind, title, details, lines, Now());
    }

    public SponsorOffer? FindOffer(long sponsor) => Query($"SELECT {OfferColumns} FROM sponsor_offers WHERE sponsor_id=$a", ReadOffer, sponsor).FirstOrDefault();
    public IReadOnlyList<SponsorOffer> ListOffers() => Query($"SELECT {OfferColumns} FROM sponsor_offers ORDER BY updated_at", ReadOffer);

    private const string RequestColumns = "id,sponsor_id,attendee_id,slot,share_email,note,created_at,attended_at,relevant";

    private static OfferRequest ReadOfferRequest(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetInt64(4) == 1, r.GetString(5), r.GetInt64(6),
        r.IsDBNull(7) ? null : r.GetInt64(7), r.IsDBNull(8) ? null : r.GetInt64(8) == 1);

    /// <summary>
    /// One request per person and sponsor, and one person per slot. Null when the offer is gone, the slot is not one
    /// of the offer's or is taken, or the person already asked.
    /// </summary>
    public OfferRequest? RequestOffer(long sponsor, long attendee, string slot, bool shareEmail, string note)
    {
        lock (_gate)
        {
            if (FindOffer(sponsor) is not { } offer) return null;
            if (offer.Slots.Count > 0 ? !offer.Slots.Contains(slot) || TakenSlots(sponsor).Contains(slot) : slot.Length > 0) return null;
            if (ListOfferRequestsOf(attendee).Any(r => r.SponsorId == sponsor)) return null;
            return FindOfferRequest(Insert("INSERT INTO offer_requests(sponsor_id,attendee_id,slot,share_email,note,created_at) VALUES($a,$b,$c,$d,$e,$f)",
                sponsor, attendee, slot, shareEmail ? 1 : 0, note, Now()));
        }
    }

    public IReadOnlySet<string> TakenSlots(long sponsor) =>
        Query("SELECT slot FROM offer_requests WHERE sponsor_id=$a AND slot<>''", r => r.GetString(0), sponsor).ToHashSet();

    public OfferRequest? FindOfferRequest(long id) => Query($"SELECT {RequestColumns} FROM offer_requests WHERE id=$a", ReadOfferRequest, id).FirstOrDefault();
    public IReadOnlyList<OfferRequest> ListOfferRequests(long sponsor) =>
        Query($"SELECT {RequestColumns} FROM offer_requests WHERE sponsor_id=$a ORDER BY created_at", ReadOfferRequest, sponsor);
    public IReadOnlyList<OfferRequest> ListOfferRequestsOf(long attendee) =>
        Query($"SELECT {RequestColumns} FROM offer_requests WHERE attendee_id=$a ORDER BY created_at", ReadOfferRequest, attendee);
    public IReadOnlyList<OfferRequest> ListAllOfferRequests() => Query($"SELECT {RequestColumns} FROM offer_requests ORDER BY created_at", ReadOfferRequest);

    /// <summary>The person can take it back until the sponsor has attended them.</summary>
    public bool CancelOfferRequest(long id, long attendee) =>
        Change("DELETE FROM offer_requests WHERE id=$a AND attendee_id=$b AND attended_at IS NULL", id, attendee);

    /// <summary>Undoing "attended" also clears "relevant", which only means something after a conversation.</summary>
    public bool MarkAttended(long id, long sponsor, bool attended) => attended
        ? Change("UPDATE offer_requests SET attended_at=coalesce(attended_at,$a) WHERE id=$b AND sponsor_id=$c", Now(), id, sponsor)
        : Change("UPDATE offer_requests SET attended_at=NULL, relevant=NULL WHERE id=$a AND sponsor_id=$b", id, sponsor);

    public bool SetRelevant(long id, long sponsor, bool? relevant) =>
        Change("UPDATE offer_requests SET relevant=$a WHERE id=$b AND sponsor_id=$c AND attended_at IS NOT NULL",
            relevant is { } value ? (value ? 1 : 0) : DBNull.Value, id, sponsor);
}
