using TalkLens.Ai;
using TalkLens.Core;

namespace TalkLens.Web;

public sealed record OfferAskRequest(string? Slot, bool ShareEmail, string? Note);
public sealed record OfferEditRequest(string? Kind, string? Title, string? Details, string? Slots);
public sealed record OfferMarkRequest(bool? Attended, bool? Relevant, bool ClearRelevant = false);

/// <summary>
/// Sponsor offers: one concrete thing each sponsor gives, asked for by people who see exactly what is shared first,
/// and marked by the sponsor as attended and relevant. Those two marks, not visits, are what a sponsor report counts.
/// </summary>
public static partial class Endpoints
{
    private const int MaxOfferTitleChars = 90;
    private const int MaxOfferDetailsChars = 300;
    private const int MaxOfferSlots = 24;
    private const int MaxNoteChars = 160;
    private const int OfferAsksPerMinute = 6;

    public static void MapOffersApi(this WebApplication app)
    {
        // ----- For attendees -----
        app.MapGet("/api/offers", async (HttpContext http, Store store, Matchmaker matchmaker, CancellationToken ct) =>
        {
            if (Me(http, store) is not { } me) return Results.Unauthorized();
            var mine = store.ListOfferRequestsOf(me.Id).ToDictionary(r => r.SponsorId);
            var offers = (await matchmaker.FindOffersAsync(me, ct)).Select(m =>
            {
                var taken = store.TakenSlots(m.Sponsor.Id);
                return new
                {
                    sponsorId = m.Sponsor.Id,
                    sponsor = m.Sponsor.Name,
                    m.Offer.Kind,
                    m.Offer.Title,
                    m.Offer.Details,
                    slots = m.Offer.Slots.Select(s => new { label = s, taken = taken.Contains(s) }),
                    m.Fits,
                    request = mine.TryGetValue(m.Sponsor.Id, out var r) ? new { r.Id, r.Slot, attended = r.AttendedAt != null } : null,
                };
            });
            // Exactly what a sponsor receives, so the sheet can show it before anything is sent.
            return Results.Ok(new { offers, share = new { me.Name, me.Seeking, me.Email } });
        });

        app.MapPost("/api/offers/{sponsor:long}/request", (long sponsor, OfferAskRequest request, HttpContext http, Store store) =>
        {
            if (Me(http, store) is not { } me) return Results.Unauthorized();
            var note = (request.Note ?? "").Trim();
            if (note.Length > MaxNoteChars) return Results.BadRequest(new { error = "note" });
            if (!Allow("offer:" + me.Id, OfferAsksPerMinute)) return Results.Json(new { error = "rate_limited" }, statusCode: 429);
            if (store.FindOffer(sponsor) == null) return Results.NotFound();
            var made = store.RequestOffer(sponsor, me.Id, (request.Slot ?? "").Trim(), request.ShareEmail && me.Email.Length > 0, note);
            return made == null ? Results.Conflict(new { error = "taken" }) : Results.Ok(new { made.Id, made.Slot });
        });

        app.MapDelete("/api/offers/requests/{id:long}", (long id, HttpContext http, Store store) =>
        {
            if (Me(http, store) is not { } me) return Results.Unauthorized();
            return store.CancelOfferRequest(id, me.Id) ? Results.Ok() : Results.NotFound();
        });

        // ----- For the sponsor, behind their private link -----
        app.MapGet("/api/sponsor/{key}/offer", (string key, Store store) =>
        {
            if (store.FindSponsorByKey(key) is not { } sponsor) return Results.NotFound();
            var offer = store.FindOffer(sponsor.Id);
            var requests = store.ListOfferRequests(sponsor.Id).Select(r =>
            {
                var person = store.FindAttendee(r.AttendeeId)!;
                return new
                {
                    r.Id,
                    person.Name,
                    person.Seeking,
                    email = r.ShareEmail ? person.Email : "",
                    r.Slot,
                    r.Note,
                    at = r.CreatedAt,
                    attended = r.AttendedAt != null,
                    r.Relevant,
                };
            });
            return Results.Ok(new
            {
                sponsor.Name,
                offer = offer == null ? null : new { offer.Kind, offer.Title, offer.Details, slots = string.Join('\n', offer.Slots) },
                requests,
            });
        });

        app.MapPut("/api/sponsor/{key}/offer", (string key, OfferEditRequest request, Store store) =>
        {
            if (store.FindSponsorByKey(key) is not { } sponsor) return Results.NotFound();
            var kind = OfferKind.All.Contains(request.Kind ?? "") ? request.Kind! : OfferKind.Chat;
            var title = (request.Title ?? "").Trim();
            var details = (request.Details ?? "").Trim();
            var slots = (request.Slots ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (title.Length > MaxOfferTitleChars || details.Length > MaxOfferDetailsChars || slots.Length > MaxOfferSlots || slots.Any(s => s.Length > MaxSpotChars))
                return Results.BadRequest(new { error = "too_long" });
            store.SetOffer(sponsor.Id, kind, title, details, slots);
            return Results.Ok();
        });

        app.MapPost("/api/sponsor/{key}/requests/{id:long}", (string key, long id, OfferMarkRequest request, Store store) =>
        {
            if (store.FindSponsorByKey(key) is not { } sponsor) return Results.NotFound();
            var changed = true;
            if (request.Attended is { } attended) changed &= store.MarkAttended(id, sponsor.Id, attended);
            if (request.Relevant != null || request.ClearRelevant) changed &= store.SetRelevant(id, sponsor.Id, request.ClearRelevant ? null : request.Relevant);
            return changed ? Results.Ok() : Results.NotFound();
        });
    }
}
