using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using TalkLens.Core;

namespace TalkLens.Ai;

public interface IEmbeddingModel
{
    /// <summary>Includes anything that changes the vectors, so vectors from another model are never compared.</summary>
    string Name { get; }
    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct);
}

public sealed class OpenAiEmbeddings(HttpClient http, string baseUrl, string apiKey, string model, int dimensions) : IEmbeddingModel
{
    public string Name => $"{model}/{dimensions}";

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/embeddings")
        {
            Content = JsonContent.Create(new { model, input = texts, dimensions }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Embeddings returned {(int)response.StatusCode}");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("data").EnumerateArray()
            .OrderBy(item => item.GetProperty("index").GetInt32())
            .Select(item => item.GetProperty("embedding").EnumerateArray().Select(n => n.GetSingle()).ToArray())
            .ToArray();
    }
}

/// <summary>A stretch of consecutive captions, or one slide. Captions alone are too short to carry their meaning.</summary>
public sealed record TextChunk(long FirstId, long LastId, long T, string Text);

public sealed record VectorHit(long TalkId, string Kind, long FirstId, long LastId, double Score);

/// <summary>
/// Finds moments by meaning, so a question in other words or another language still reaches them. Vectors are made once
/// and kept in SQLite. Elasticsearch answers when it is running (<see cref="ElasticIndex"/>); if not, every search compares
/// them all here: a whole event is a few thousand vectors, so a laptop can do without it.
/// Without a model or internet, search carries on with words alone (<see cref="Hybrid"/>).
/// </summary>
public sealed class VectorIndex(Store store, IEmbeddingModel? model, Func<bool> online, ILogger log, ElasticIndex? elastic = null) : BackgroundService
{
    private const int ChunkChars = 600;
    private const int BatchSize = 64;
    /// <summary>Below this a nearest neighbour is only the least unrelated text, not an answer. Measured on text-embedding-3-small, see docs/POC.md.</summary>
    public const double MinScore = 0.3;
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(2);

    /// <param name="final">The talk ended, so the last short stretch will not grow any more.</param>
    public static IReadOnlyList<TextChunk> Chunk(IReadOnlyList<Segment> segments, bool final)
    {
        var chunks = new List<TextChunk>();
        var open = new List<Segment>();
        void Close()
        {
            chunks.Add(new TextChunk(open[0].Id, open[^1].Id, open[0].T, string.Join(' ', open.Select(s => s.Text))));
            open.Clear();
        }
        foreach (var segment in segments)
        {
            open.Add(segment);
            if (open.Sum(s => s.Text.Length) >= ChunkChars) Close();
        }
        if (final && open.Count > 0) Close();
        return chunks;
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (model == null) return;
        using var timer = new PeriodicTimer(Tick);
        try
        {
            do
            {
                try { await IndexPendingAsync(stop); }
                catch (Exception e) when (!stop.IsCancellationRequested)
                {
                    log.LogWarning("Vector index will retry: {Type}", e.GetType().Name);
                }
            }
            while (await timer.WaitForNextTickAsync(stop));
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Only talks the speaker agreed to keep. Anything missed while offline is picked up here later.</summary>
    public async Task IndexPendingAsync(CancellationToken ct)
    {
        if (model == null || !online()) return;
        await IndexAgendaAsync(ct);
        foreach (var talk in store.ListTalks().Where(t => t.KeepAfter))
        {
            var pending = Chunk(store.GetSegmentsAfter(talk.Id, store.LastVectorSegment(talk.Id, model.Name)), talk.EndedAtUnixMs != null)
                .Select(c => (Kind: "segment", Chunk: c))
                .Concat(store.SlidesWithoutVector(talk.Id, model.Name).Select(s => (Kind: "slide", Chunk: new TextChunk(s.Id, s.Id, s.T, s.Text))))
                .ToList();
            foreach (var batch in pending.Chunk(BatchSize))
            {
                var vectors = await model.EmbedAsync(batch.Select(p => p.Chunk.Text).ToList(), ct);
                for (var i = 0; i < batch.Length; i++)
                    store.AddVector(talk.Id, batch[i].Kind, batch[i].Chunk.FirstId, batch[i].Chunk.LastId, model.Name, ToBytes(Normalized(vectors[i])));
            }
        }
    }

    /// <summary>Never fails and never holds up an answer for long: on any trouble, words alone will have to do.</summary>
    public async Task<IReadOnlyList<VectorHit>> SearchAsync(string query, long? talkId, int limit, CancellationToken ct)
    {
        if (model == null || !online() || query.Length == 0) return [];
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(QueryTimeout);
            var wanted = Normalized((await model.EmbedAsync([query], timeout.Token))[0]);
            if (elastic != null && await elastic.SearchVectorsAsync(wanted, model.Name, talkId, limit, ct) is { } found) return found;
            return store.ReadVectors(model.Name, talkId)
                .Select(row => new VectorHit(row.TalkId, row.Kind, row.FirstId, row.LastId, Dot(wanted, row.Vector)))
                .Where(hit => hit.Score >= MinScore)
                .OrderByDescending(hit => hit.Score)
                .Take(limit)
                .ToList();
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // The exception type only: a message could echo the private question.
            log.LogWarning("Search by meaning failed, using words alone: {Type}", e.GetType().Name);
            return [];
        }
    }

    /// <summary>What was said and shown, by words. Elasticsearch also forgives typos and word endings.</summary>
    public async Task<IReadOnlyList<(string Kind, long RefId, long TalkId)>> WordsAsync(string query, int limit, CancellationToken ct)
    {
        if (elastic != null && query.Length > 0 && await elastic.SearchWordsAsync(query, limit, ct) is { } found) return found;
        return store.SearchAll(Retriever.BuildQuery(query), limit);
    }

    // ----- Find the right talk: the schedule, searched by what a talk is about -----
    public static string SessionText(AgendaItem item) =>
        string.Join(". ", new[] { item.Title, item.Speaker, item.About }.Where(part => part.Length > 0));

    private async Task IndexAgendaAsync(CancellationToken ct)
    {
        var known = store.ReadAgendaVectors();
        var pending = store.ListAgenda().Where(a => !known.TryGetValue(a.Id, out var row) || row.Model != model!.Name || row.Text != SessionText(a)).ToList();
        foreach (var batch in pending.Chunk(BatchSize))
        {
            var vectors = await model!.EmbedAsync(batch.Select(SessionText).ToList(), ct);
            for (var i = 0; i < batch.Length; i++) store.SetAgendaVector(batch[i].Id, model.Name, SessionText(batch[i]), ToBytes(Normalized(vectors[i])));
        }
    }

    /// <summary>Best first. Never fails: with no model, no internet and no Elasticsearch, matching words still find a talk.</summary>
    public async Task<IReadOnlyList<SessionHit>> SessionsAsync(string query, bool useMeaning, int limit, CancellationToken ct)
    {
        if (query.Length == 0) return [];
        var words = elastic == null ? null : await elastic.SearchSessionWordsAsync(query, limit, ct);
        words ??= SessionWordsHere(query, limit);
        return Hybrid.FuseSessions(words, useMeaning ? await SessionMeaningAsync(query, limit, ct) : [], limit);
    }

    private IReadOnlyList<long> SessionWordsHere(string query, int limit)
    {
        var terms = Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(term => term.Length >= 3).ToList();
        return store.ListAgenda()
            .Select(item => (item.Id, Matches: terms.Count(term => Fold(SessionText(item)).Contains(term))))
            .Where(row => row.Matches > 0)
            .OrderByDescending(row => row.Matches)
            .Take(limit)
            .Select(row => row.Id)
            .ToList();
    }

    private async Task<IReadOnlyList<long>> SessionMeaningAsync(string query, int limit, CancellationToken ct)
    {
        if (model == null || !online()) return [];
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(QueryTimeout);
            var wanted = Normalized((await model.EmbedAsync([query], timeout.Token))[0]);
            if (elastic != null && await elastic.SearchSessionVectorsAsync(wanted, model.Name, limit, ct) is { } found) return found;
            return store.ReadAgendaVectors()
                .Where(row => row.Value.Model == model.Name)
                .Select(row => (Id: row.Key, Score: Dot(wanted, row.Value.Vector)))
                .Where(row => row.Score >= MinScore)
                .OrderByDescending(row => row.Score)
                .Take(limit)
                .Select(row => row.Id)
                .ToList();
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Search by meaning failed, using words alone: {Type}", e.GetType().Name);
            return [];
        }
    }

    private static string Fold(string text) => new string(text.Normalize(System.Text.NormalizationForm.FormD)
        .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
        .ToArray()).ToLowerInvariant();

    private static float[] Normalized(float[] vector)
    {
        var length = MathF.Sqrt(vector.Sum(n => n * n));
        return length == 0 ? vector : vector.Select(n => n / length).ToArray();
    }

    private static byte[] ToBytes(float[] vector) => MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();

    private static double Dot(float[] wanted, byte[] stored)
    {
        var other = MemoryMarshal.Cast<byte, float>(stored);
        if (other.Length != wanted.Length) return 0;
        var sum = 0f;
        for (var i = 0; i < wanted.Length; i++) sum += wanted[i] * other[i];
        return sum;
    }
}

/// <param name="ByMeaning">Found only by meaning: the words themselves were not said.</param>
public sealed record SearchMoment(long TalkId, string Kind, long FirstId, long LastId, bool ByMeaning);

/// <param name="ByMeaning">Found only by meaning: none of the words are in its title or description.</param>
public sealed record SessionHit(long Id, bool ByMeaning);

/// <summary>Word search finds names and exact terms, vectors find what was meant. Each covers the other's blind spot.</summary>
public static class Hybrid
{
    /// <summary>Reciprocal rank fusion: scores from the two searches cannot be compared, but their rankings can.</summary>
    private const double RankOffset = 60;
    private const int MaxTextChars = 1200;

    public static IReadOnlyList<SearchMoment> Fuse(IReadOnlyList<(string Kind, long RefId, long TalkId)> words, IReadOnlyList<VectorHit> meaning, int limit)
    {
        var entries = new List<(SearchMoment Moment, double Score)>();
        for (var i = 0; i < meaning.Count; i++)
        {
            var hit = meaning[i];
            entries.Add((new SearchMoment(hit.TalkId, hit.Kind, hit.FirstId, hit.LastId, true), 1 / (RankOffset + i)));
        }
        for (var i = 0; i < words.Count; i++)
        {
            var (kind, refId, talkId) = words[i];
            var score = 1 / (RankOffset + i);
            var both = entries.FindIndex(e => e.Moment.ByMeaning && e.Moment.TalkId == talkId && e.Moment.Kind == kind && e.Moment.FirstId <= refId && refId <= e.Moment.LastId);
            // Both agree. The exact sentence is the better place to land than the start of its stretch.
            if (both >= 0) entries[both] = (new SearchMoment(talkId, kind, refId, refId, false), entries[both].Score + score);
            else entries.Add((new SearchMoment(talkId, kind, refId, refId, false), score));
        }
        return entries.OrderByDescending(e => e.Score).Take(limit).Select(e => e.Moment).ToList();
    }

    public static IReadOnlyList<SessionHit> FuseSessions(IReadOnlyList<long> words, IReadOnlyList<long> meaning, int limit)
    {
        double Rank(IReadOnlyList<long> list, long id)
        {
            for (var i = 0; i < list.Count; i++) if (list[i] == id) return 1 / (RankOffset + i);
            return 0;
        }
        return words.Union(meaning)
            .OrderByDescending(id => Rank(words, id) + Rank(meaning, id))
            .Take(limit)
            .Select(id => new SessionHit(id, !words.Contains(id)))
            .ToList();
    }

    public static IReadOnlyList<(SearchMoment Moment, long T, string Text)> Resolve(Store store, IEnumerable<SearchMoment> moments)
    {
        var segments = new Dictionary<long, IReadOnlyList<Segment>>();
        var slides = new Dictionary<long, IReadOnlyList<Slide>>();
        var found = new List<(SearchMoment, long, string)>();
        foreach (var moment in moments)
        {
            if (moment.Kind == "slide")
            {
                if (!slides.TryGetValue(moment.TalkId, out var talkSlides)) slides[moment.TalkId] = talkSlides = store.GetSlides(moment.TalkId);
                if (talkSlides.FirstOrDefault(s => s.Id == moment.FirstId) is { } slide) found.Add((moment, slide.T, slide.Text));
                continue;
            }
            if (!segments.TryGetValue(moment.TalkId, out var talkSegments)) segments[moment.TalkId] = talkSegments = store.GetSegments(moment.TalkId);
            var stretch = talkSegments.Where(s => moment.FirstId <= s.Id && s.Id <= moment.LastId).ToList();
            if (stretch.Count == 0) continue;
            var text = string.Join(' ', stretch.Select(s => s.Text));
            found.Add((moment, stretch.Min(s => s.T), text.Length <= MaxTextChars ? text : text[..MaxTextChars]));
        }
        return found;
    }
}
