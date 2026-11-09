using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using TalkLens.Core;

namespace TalkLens.Ai;

/// <param name="OffersWhatYouSeek">Their "I can help with" answers your "I am looking for".</param>
/// <param name="SeeksWhatYouOffer">Their "I am looking for" is what you can help with.</param>
/// <param name="ByMeaning">The match came from meaning alone: no word in common.</param>
/// <param name="Fits">The offer answers what the person is looking for, by Elasticsearch or by shared words.</param>
public sealed record OfferMatch(SponsorOffer Offer, Sponsor Sponsor, bool Fits);

public sealed record PersonMatch(Attendee Person, double Score, bool OffersWhatYouSeek, bool SeeksWhatYouOffer, IReadOnlyList<string> SharedTalks, bool ByMeaning);

/// <summary>
/// Who here should meet whom. What one person is looking for is compared with what the other can offer, both ways,
/// by words and by meaning. Elasticsearch does it when it runs (<see cref="ElasticIndex"/>); otherwise this laptop does,
/// since an event is a few hundred people at most. Only people who chose to be seen are ever compared or shown.
/// </summary>
public sealed class Matchmaker(Store store, IEmbeddingModel? model, Func<bool> online, ILogger log, ElasticIndex? elastic = null) : BackgroundService
{
    public const string Seeking = "seeking";
    public const string Offering = "offering";
    /// <summary>
    /// Measured on text-embedding-3-small with one-line profiles: real matches across languages scored 0.58 to 0.66,
    /// two different technical topics (Kubernetes against Postgres) 0.44, and unrelated ones below 0.25.
    /// </summary>
    private const double MeaningThreshold = 0.5;
    private const double WordsThreshold = 0.34;
    private const double SharedTalkBonus = 0.1;
    private const int MaxSharedTalks = 3;
    private const int FusionK = 60;
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (model == null) return;
        using var timer = new PeriodicTimer(Tick);
        try
        {
            do
            {
                try { await EmbedPendingAsync(stop); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogWarning("Could not prepare profiles for matching by meaning: {Type}", e.GetType().Name);
                }
            }
            while (await timer.WaitForNextTickAsync(stop));
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Only what changed is sent, so a profile costs one small request when it is saved and nothing after.</summary>
    public async Task EmbedPendingAsync(CancellationToken ct)
    {
        if (model == null || !online()) return;
        var known = store.ReadProfileVectors();
        var pending = store.ListAttendees().Where(a => a.Networking)
            .SelectMany(a => new[] { (a.Id, Field: Seeking, Text: a.Seeking), (a.Id, Field: Offering, Text: a.Offering) })
            .Where(p => p.Text.Length > 0 && !(known.TryGetValue((p.Id, p.Field), out var row) && row.Model == model.Name && row.Text == p.Text))
            .ToList();
        foreach (var batch in pending.Chunk(64))
        {
            var vectors = await model.EmbedAsync(batch.Select(p => p.Text).ToList(), ct);
            for (var i = 0; i < batch.Length; i++)
                store.SetProfileVector(batch[i].Id, batch[i].Field, model.Name, batch[i].Text, ToBytes(Normalized(vectors[i])));
        }
    }

    /// <summary>Everyone visible, best match first. Never fails: without Elasticsearch, a model or internet, words still match.</summary>
    public async Task<IReadOnlyList<PersonMatch>> FindAsync(Attendee me, CancellationToken ct)
    {
        var others = store.ListAttendees().Where(a => a.Networking && a.Id != me.Id).ToList();
        if (others.Count == 0) return [];
        var titles = store.ListTalks().ToDictionary(t => t.Id, t => t.Title);
        var attended = store.ListAttendance().GroupBy(a => a.AttendeeId).ToDictionary(g => g.Key, g => (IReadOnlySet<long>)g.Select(a => a.TalkId).ToHashSet());
        var vectors = store.ReadProfileVectors();
        var local = Rank(me, others, vectors, model?.Name ?? "", id => attended.GetValueOrDefault(id) ?? new HashSet<long>(), titles);

        float[]? VectorOf(string field) => model != null && vectors.TryGetValue((me.Id, field), out var row) && row.Model == model.Name
            ? MemoryMarshal.Cast<byte, float>(row.Vector).ToArray() : null;
        var found = elastic == null ? null
            : await elastic.SearchPeopleAsync(me.Id, me.Seeking, me.Offering, VectorOf(Seeking), VectorOf(Offering), model?.Name ?? "", others.Count, ct);
        return found == null ? local : Fuse(local, found);
    }

    /// <summary>Every sponsor's offer, the ones that fit what the person is looking for first.</summary>
    public async Task<IReadOnlyList<OfferMatch>> FindOffersAsync(Attendee me, CancellationToken ct)
    {
        var sponsors = store.ListSponsors().ToDictionary(s => s.Id);
        var offers = store.ListOffers().Where(o => sponsors.ContainsKey(o.SponsorId)).Select(o => (Offer: o, Sponsor: sponsors[o.SponsorId])).ToList();
        if (offers.Count == 0) return [];
        var found = elastic == null ? null : await elastic.SearchOffersAsync(me.Seeking, offers.Count, ct);
        return RankOffers(me.Seeking, offers, found);
    }

    public static IReadOnlyList<OfferMatch> RankOffers(string seeking, IReadOnlyList<(SponsorOffer Offer, Sponsor Sponsor)> offers, IReadOnlyList<long>? elastic)
    {
        var local = offers
            .Select(o => (o, score: WordMatch(seeking, $"{o.Offer.Title} {o.Offer.Details} {o.Sponsor.Name} {o.Sponsor.Keywords}")))
            .OrderByDescending(p => p.score)
            .ThenBy(p => p.o.Sponsor.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var fromElastic = (elastic ?? []).Select((id, rank) => (id, rank)).ToDictionary(p => p.id, p => p.rank);
        return local
            .Select((p, rank) => (p, fused: (p.score >= WordsThreshold ? 1.0 / (FusionK + rank) : 0) + (fromElastic.TryGetValue(p.o.Sponsor.Id, out var r) ? 1.0 / (FusionK + r) : 0)))
            .OrderByDescending(p => p.fused)
            .ThenByDescending(p => p.p.score)
            .Select(p => new OfferMatch(p.p.o.Offer, p.p.o.Sponsor, p.fused > 0))
            .ToList();
    }

    /// <summary>Reciprocal rank fusion: Elasticsearch's scores and these cannot be compared, but their orders can.</summary>
    public static IReadOnlyList<PersonMatch> Fuse(IReadOnlyList<PersonMatch> local, IReadOnlyList<long> elastic)
    {
        var fromElastic = elastic.Select((id, rank) => (id, rank)).ToDictionary(p => p.id, p => p.rank);
        return local
            .Select((match, rank) => (match, score: 1.0 / (FusionK + rank) + (fromElastic.TryGetValue(match.Person.Id, out var r) ? 1.0 / (FusionK + r) : 0)))
            .OrderByDescending(p => p.score)
            .Select(p => p.match)
            .ToList();
    }

    /// <param name="talksOf">The talks each person was at.</param>
    public static IReadOnlyList<PersonMatch> Rank(Attendee me, IEnumerable<Attendee> others,
        IReadOnlyDictionary<(long Attendee, string Field), (string Model, string Text, byte[] Vector)> vectors, string modelName,
        Func<long, IReadOnlySet<long>> talksOf, IReadOnlyDictionary<long, string> titles)
    {
        var mine = talksOf(me.Id);
        return others.Select(other =>
        {
            var (offers, offersByMeaning, offersScore) = Side(me.Id, Seeking, me.Seeking, other.Id, Offering, other.Offering, vectors, modelName);
            var (seeks, seeksByMeaning, seeksScore) = Side(me.Id, Offering, me.Offering, other.Id, Seeking, other.Seeking, vectors, modelName);
            var shared = talksOf(other.Id).Where(mine.Contains).Where(titles.ContainsKey).Select(id => titles[id]).Take(MaxSharedTalks).ToList();
            var score = offersScore + seeksScore + SharedTalkBonus * shared.Count;
            return new PersonMatch(other, score, offers, seeks, shared, (offers && offersByMeaning) || (seeks && seeksByMeaning));
        })
        .OrderByDescending(m => m.Score)
        .ThenBy(m => m.Person.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToList();
    }

    private static (bool Matches, bool ByMeaningOnly, double Score) Side(long me, string myField, string myText, long other, string theirField, string theirText,
        IReadOnlyDictionary<(long, string), (string Model, string Text, byte[] Vector)> vectors, string modelName)
    {
        if (myText.Length == 0 || theirText.Length == 0) return (false, false, 0);
        var words = WordMatch(myText, theirText);
        var meaning = vectors.TryGetValue((me, myField), out var a) && vectors.TryGetValue((other, theirField), out var b)
            && a.Model == modelName && b.Model == modelName && a.Text == myText && b.Text == theirText ? Dot(a.Vector, b.Vector) : 0;
        var byWords = words >= WordsThreshold;
        var byMeaning = meaning >= MeaningThreshold;
        return (byWords || byMeaning, byMeaning && !byWords, Math.Max(words, meaning));
    }

    // ----- Words, for when there is no model or no internet -----
    private static readonly HashSet<string> StopWords =
    [
        "the", "and", "for", "with", "about", "from", "into", "that", "this", "your", "you", "our", "who", "how", "what", "can", "help", "looking", "someone", "people", "years", "year",
        "los", "las", "una", "uno", "unos", "con", "para", "por", "que", "del", "sobre", "como", "alguien", "gente", "busco", "ayuda", "ayudar", "puedo", "años", "mis", "sus", "tus", "entre", "desde",
    ];

    /// <summary>Shared word stems over the shorter side, so a one-line need is not drowned by a long offer. "migrar" meets "migrating".</summary>
    public static double WordMatch(string a, string b)
    {
        var left = Stems(a);
        var right = Stems(b);
        if (left.Count == 0 || right.Count == 0) return 0;
        return (double)left.Count(right.Contains) / Math.Min(left.Count, right.Count);
    }

    private const int StemLength = 5;

    private static HashSet<string> Stems(string text) => new string(Fold(text).Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Where(word => word.Length >= 3 && !StopWords.Contains(word))
        .Select(word => word.Length > StemLength ? word[..StemLength] : word)
        .ToHashSet();

    private static string Fold(string text) => new string(text.Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant();

    private static float[] Normalized(float[] vector)
    {
        var length = MathF.Sqrt(vector.Sum(n => n * n));
        return length == 0 ? vector : vector.Select(n => n / length).ToArray();
    }

    private static byte[] ToBytes(float[] vector) => MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();

    private static double Dot(byte[] a, byte[] b)
    {
        var x = MemoryMarshal.Cast<byte, float>(a);
        var y = MemoryMarshal.Cast<byte, float>(b);
        if (x.Length != y.Length) return 0;
        var sum = 0f;
        for (var i = 0; i < x.Length; i++) sum += x[i] * y[i];
        return sum;
    }
}
