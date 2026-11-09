using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TalkLens.Core;

namespace TalkLens.Ai;

/// <summary>
/// The search engine when Elasticsearch is running on this laptop: what was said, what was shown and the schedule, by words
/// and by meaning. Talks are written to SQLite first, so nothing is lost if Elasticsearch stops, and a wiped index fills
/// again without paying for a single new vector. While it is down every search returns null and SQLite answers instead.
/// </summary>
public sealed class ElasticIndex(HttpClient http, string baseUrl, string prefix, Store store, ILogger log) : BackgroundService
{
    private const int BulkDocs = 200;
    private const int MaxTalks = 10000;
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(2);

    private readonly Dictionary<long, long> _segmentCursor = [];
    private readonly Dictionary<long, string> _slides = [];
    private long _vectorCursor;
    private string _agendaState = "";
    private string _peopleState = "";
    private string _offersState = "";
    private volatile bool _ready;
    private long _moments;

    /// <summary>One index per database, so a test server on the same laptop can never mix its talks with the real ones.</summary>
    public static string PrefixFor(string databasePath) =>
        "talklens-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(databasePath))))[..8].ToLowerInvariant();

    private string Moments => $"{prefix}-moments";
    private string Sessions => $"{prefix}-sessions";
    private string People => $"{prefix}-people";
    private string Offers => $"{prefix}-offers";

    public bool Ready => _ready;
    public long MomentCount => Interlocked.Read(ref _moments);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (baseUrl.Length == 0) return;
        using var timer = new PeriodicTimer(Tick);
        try
        {
            do { await SyncAsync(stop); }
            while (await timer.WaitForNextTickAsync(stop));
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Safe to repeat: every document has a fixed id, so sending one twice replaces it.</summary>
    public async Task SyncAsync(CancellationToken ct)
    {
        try
        {
            var kept = store.ListTalks().Where(t => t.KeepAfter).Select(t => t.Id).ToHashSet();
            await RemoveTalksNoLongerKeptAsync(kept, ct);
            foreach (var talk in kept) await PushWordsAsync(talk, ct);
            await PushVectorsAsync(kept, ct);
            await PushAgendaAsync(ct);
            await PushPeopleAsync(ct);
            await PushOffersAsync(ct);
            if (!_ready) log.LogInformation("Search is using Elasticsearch");
            _ready = true;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            if (_ready) log.LogWarning("Elasticsearch stopped answering, search is using SQLite: {Type}", e.GetType().Name);
            _ready = false;
            Forget();
        }
    }

    private void Forget()
    {
        _segmentCursor.Clear();
        _slides.Clear();
        _vectorCursor = 0;
        _agendaState = "";
        _peopleState = "";
        _offersState = "";
    }

    private async Task RemoveTalksNoLongerKeptAsync(HashSet<long> kept, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync($"{baseUrl}/{Moments}/_search", new
        {
            size = 0,
            track_total_hits = true,
            query = new { term = new { doc = "words" } },
            aggs = new { talks = new { terms = new { field = "talk_id", size = MaxTalks } } },
        }, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // New or wiped: everything goes in again.
            Forget();
            await CreateIndicesAsync(ct);
            Interlocked.Exchange(ref _moments, 0);
            return;
        }
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Interlocked.Exchange(ref _moments, doc.RootElement.GetProperty("hits").GetProperty("total").GetProperty("value").GetInt64());
        var gone = doc.RootElement.GetProperty("aggregations").GetProperty("talks").GetProperty("buckets").EnumerateArray()
            .Select(bucket => bucket.GetProperty("key").GetInt64()).Where(id => !kept.Contains(id)).ToList();
        if (gone.Count == 0) return;
        using var removed = await http.PostAsJsonAsync($"{baseUrl}/{Moments}/_delete_by_query?refresh=true&conflicts=proceed",
            new { query = new { terms = new { talk_id = gone } } }, ct);
        removed.EnsureSuccessStatusCode();
        foreach (var talk in gone) _segmentCursor.Remove(talk);
        // In case the talk comes back: its vectors are still in SQLite and have to be sent again.
        _vectorCursor = 0;
    }

    private async Task CreateIndicesAsync(CancellationToken ct)
    {
        await CreateIndexAsync(Moments, new
        {
            talk_id = new { type = "long" },
            kind = new { type = "keyword" },
            doc = new { type = "keyword" },
            first_id = new { type = "long" },
            last_id = new { type = "long" },
            model = new { type = "keyword" },
            text = TextMapping(),
            vector = VectorMapping(),
        }, ct);
        await CreateIndexAsync(Sessions, new
        {
            id = new { type = "long" },
            model = new { type = "keyword" },
            text = TextMapping(),
            vector = VectorMapping(),
        }, ct);
    }

    private async Task CreateIndexAsync(string name, object properties, CancellationToken ct)
    {
        using var response = await http.PutAsJsonAsync($"{baseUrl}/{name}", new
        {
            settings = new
            {
                number_of_shards = 1,
                number_of_replicas = 0,
                analysis = new { analyzer = new { folded = new { tokenizer = "standard", filter = new[] { "lowercase", "asciifolding" } } } },
            },
            mappings = new { properties },
        }, ct);
        // Already there is fine: the moments index may be the only one that was wiped.
        if (response.StatusCode != HttpStatusCode.BadRequest) response.EnsureSuccessStatusCode();
    }

    /// <summary>Accents and capitals never matter, and each language's word endings do ("seguridad", "secure", "security").</summary>
    private static object TextMapping() => new
    {
        type = "text",
        analyzer = "folded",
        fields = new { en = new { type = "text", analyzer = "english" }, es = new { type = "text", analyzer = "spanish" } },
    };

    /// <summary>No dims: Elasticsearch takes them from the first vector.</summary>
    private static object VectorMapping() => new { type = "dense_vector", similarity = "cosine", index_options = new { type = "int8_hnsw" } };

    private async Task PushWordsAsync(long talk, CancellationToken ct)
    {
        var docs = new List<(string Id, object Doc)>();
        var segments = store.GetSegmentsAfter(talk, _segmentCursor.GetValueOrDefault(talk));
        docs.AddRange(segments.Select(s => ($"s-{s.Id}", (object)new { talk_id = talk, kind = "segment", doc = "words", first_id = s.Id, last_id = s.Id, text = s.Text })));
        var slides = store.GetSlides(talk).Where(s => s.Text.Length > 0 && _slides.GetValueOrDefault(s.Id) != s.Text).ToList();
        docs.AddRange(slides.Select(s => ($"d-{s.Id}", (object)new { talk_id = talk, kind = "slide", doc = "words", first_id = s.Id, last_id = s.Id, text = s.Text })));
        await BulkAsync(Moments, docs, ct);
        if (segments.Count > 0) _segmentCursor[talk] = segments[^1].Id;
        foreach (var slide in slides) _slides[slide.Id] = slide.Text;
    }

    private async Task PushVectorsAsync(HashSet<long> kept, CancellationToken ct)
    {
        while (true)
        {
            var rows = store.ReadVectorsAfter(_vectorCursor, BulkDocs);
            if (rows.Count == 0) return;
            await BulkAsync(Moments, rows.Where(r => kept.Contains(r.TalkId)).Select(r =>
                ($"v-{r.Kind}-{r.FirstId}", (object)new { talk_id = r.TalkId, kind = r.Kind, doc = "meaning", first_id = r.FirstId, last_id = r.LastId, model = r.Model, vector = Floats(r.Vector) })).ToList(), ct);
            _vectorCursor = rows[^1].Id;
        }
    }

    /// <summary>A schedule is a few dozen lines, so any change sends it again whole.</summary>
    private async Task PushAgendaAsync(CancellationToken ct)
    {
        var agenda = store.ListAgenda();
        var vectors = store.ReadAgendaVectors();
        var state = string.Join('\n', agenda.Select(a => $"{a.Id}|{VectorIndex.SessionText(a)}|{(vectors.TryGetValue(a.Id, out var v) ? $"{v.Model}|{v.Text}" : "")}"));
        if (state == _agendaState) return;
        using var cleared = await http.PostAsJsonAsync($"{baseUrl}/{Sessions}/_delete_by_query?refresh=true&conflicts=proceed", new { query = new { match_all = new { } } }, ct);
        cleared.EnsureSuccessStatusCode();
        await BulkAsync(Sessions, agenda.Select(a =>
        {
            var text = VectorIndex.SessionText(a);
            return ($"a-{a.Id}", vectors.TryGetValue(a.Id, out var row) && row.Text == text
                ? (object)new { id = a.Id, text, model = row.Model, vector = Floats(row.Vector) }
                : new { id = a.Id, text });
        }).ToList(), ct);
        _agendaState = state;
    }

    /// <summary>
    /// Only people who chose to be seen. Like the schedule, a change sends the list again whole: someone who hides or
    /// deletes their data is gone from the index on the next tick, never left behind.
    /// </summary>
    private async Task PushPeopleAsync(CancellationToken ct)
    {
        var people = store.ListAttendees().Where(a => a.Networking).ToList();
        var vectors = store.ReadProfileVectors();
        object? Vector(long id, string field, string text) =>
            vectors.TryGetValue((id, field), out var row) && row.Text == text ? Floats(row.Vector) : null;
        string ModelOf(long id) => vectors.Where(v => v.Key.Attendee == id).Select(v => v.Value.Model).FirstOrDefault() ?? "";
        var state = string.Join('\n', people.Select(a => $"{a.Id}|{a.Seeking}|{a.Offering}|{ModelOf(a.Id)}|{Vector(a.Id, Matchmaker.Seeking, a.Seeking) != null}|{Vector(a.Id, Matchmaker.Offering, a.Offering) != null}"));
        if (state == _peopleState) return;
        if (_peopleState.Length == 0)
            await CreateIndexAsync(People, new
            {
                id = new { type = "long" },
                model = new { type = "keyword" },
                seeking = TextMapping(),
                offering = TextMapping(),
                seeking_vector = VectorMapping(),
                offering_vector = VectorMapping(),
            }, ct);
        using var cleared = await http.PostAsJsonAsync($"{baseUrl}/{People}/_delete_by_query?refresh=true&conflicts=proceed", new { query = new { match_all = new { } } }, ct);
        cleared.EnsureSuccessStatusCode();
        await BulkAsync(People, people.Select(a =>
        {
            var doc = new JsonObject { ["id"] = a.Id, ["model"] = ModelOf(a.Id), ["seeking"] = a.Seeking, ["offering"] = a.Offering };
            if (Vector(a.Id, Matchmaker.Seeking, a.Seeking) is float[] seeking) doc["seeking_vector"] = Items(seeking.Select(n => (JsonNode)n).ToArray());
            if (Vector(a.Id, Matchmaker.Offering, a.Offering) is float[] offering) doc["offering_vector"] = Items(offering.Select(n => (JsonNode)n).ToArray());
            return ($"p-{a.Id}", (object)doc);
        }).ToList(), ct);
        _peopleState = state.Length == 0 ? "-" : state;
    }

    /// <summary>
    /// What I am looking for against what others offer, and what I offer against what they look for: words in both
    /// languages and, when there are vectors, meaning too. Elasticsearch adds the scores of all four. Null means ask SQLite.
    /// </summary>
    public async Task<IReadOnlyList<long>?> SearchPeopleAsync(long me, string seeking, string offering, float[]? seekingVector, float[]? offeringVector,
        string model, int limit, CancellationToken ct)
    {
        var should = new JsonArray();
        if (seeking.Length > 0) should.Add(PeopleWords(seeking, "offering"));
        if (offering.Length > 0) should.Add(PeopleWords(offering, "seeking"));
        var body = new JsonObject
        {
            ["size"] = limit,
            ["_source"] = Items("id"),
            ["query"] = new JsonObject
            {
                ["bool"] = new JsonObject
                {
                    ["should"] = should,
                    ["must_not"] = Items(new JsonObject { ["term"] = new JsonObject { ["id"] = me } }),
                },
            },
        };
        var knn = new JsonArray();
        if (seekingVector != null) knn.Add(PeopleKnn("offering_vector", seekingVector, limit, me, model));
        if (offeringVector != null) knn.Add(PeopleKnn("seeking_vector", offeringVector, limit, me, model));
        if (knn.Count > 0) body["knn"] = knn;
        if (should.Count == 0 && knn.Count == 0) return [];
        var hits = await SearchAsync(People, body, ct);
        return hits?.Select(hit => hit.GetProperty("_source").GetProperty("id").GetInt64()).ToList();
    }

    /// <summary>Each sponsor's offer, with their name and keywords. A few documents, so a change sends them all again.</summary>
    private async Task PushOffersAsync(CancellationToken ct)
    {
        var sponsors = store.ListSponsors().ToDictionary(s => s.Id);
        var offers = store.ListOffers().Where(o => sponsors.ContainsKey(o.SponsorId)).ToList();
        var state = string.Join('\n', offers.Select(o => $"{o.SponsorId}|{o.UpdatedAt}|{sponsors[o.SponsorId].Name}|{sponsors[o.SponsorId].Keywords}"));
        if (state == _offersState) return;
        if (_offersState.Length == 0)
            await CreateIndexAsync(Offers, new { id = new { type = "long" }, text = TextMapping() }, ct);
        using var cleared = await http.PostAsJsonAsync($"{baseUrl}/{Offers}/_delete_by_query?refresh=true&conflicts=proceed", new { query = new { match_all = new { } } }, ct);
        cleared.EnsureSuccessStatusCode();
        await BulkAsync(Offers, offers.Select(o =>
        {
            var sponsor = sponsors[o.SponsorId];
            return ($"o-{o.SponsorId}", (object)new { id = o.SponsorId, text = $"{o.Title}\n{o.Details}\n{sponsor.Name}\n{sponsor.Keywords}" });
        }).ToList(), ct);
        _offersState = state.Length == 0 ? "-" : state;
    }

    /// <summary>Sponsors whose offer fits what the person is looking for, best first. Null means ask SQLite.</summary>
    public async Task<IReadOnlyList<long>?> SearchOffersAsync(string seeking, int limit, CancellationToken ct)
    {
        if (seeking.Length == 0) return [];
        var body = new JsonObject
        {
            ["size"] = limit,
            ["_source"] = Items("id"),
            ["query"] = PeopleWords(seeking, "text"),
        };
        var hits = await SearchAsync(Offers, body, ct);
        return hits?.Select(hit => hit.GetProperty("_source").GetProperty("id").GetInt64()).ToList();
    }

    private static JsonObject PeopleWords(string query, string field) => new()
    {
        ["multi_match"] = new JsonObject
        {
            ["query"] = query,
            ["fields"] = Items(field, $"{field}.en", $"{field}.es"),
            ["fuzziness"] = "AUTO",
        },
    };

    private static JsonObject PeopleKnn(string field, float[] wanted, int limit, long me, string model) => new()
    {
        ["field"] = field,
        ["query_vector"] = Items(wanted.Select(n => (JsonNode)n).ToArray()),
        ["k"] = limit,
        ["num_candidates"] = Math.Max(100, limit * 5),
        ["similarity"] = VectorIndex.MinScore,
        ["filter"] = new JsonObject
        {
            ["bool"] = new JsonObject
            {
                ["filter"] = Items(new JsonObject { ["term"] = new JsonObject { ["model"] = model } }),
                ["must_not"] = Items(new JsonObject { ["term"] = new JsonObject { ["id"] = me } }),
            },
        },
    };

    private async Task BulkAsync(string index, IReadOnlyList<(string Id, object Doc)> docs, CancellationToken ct)
    {
        foreach (var batch in docs.Chunk(BulkDocs))
        {
            var lines = new StringBuilder();
            foreach (var (id, doc) in batch)
            {
                lines.Append(JsonSerializer.Serialize(new { index = new { _id = id } })).Append('\n');
                lines.Append(JsonSerializer.Serialize(doc, doc.GetType())).Append('\n');
            }
            using var content = new StringContent(lines.ToString(), Encoding.UTF8, "application/x-ndjson");
            using var response = await http.PostAsync($"{baseUrl}/{index}/_bulk?refresh=true", content, ct);
            response.EnsureSuccessStatusCode();
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            // Rejected documents are not retried: a model with other dimensions needs a new index, not a stuck sync.
            if (result.RootElement.GetProperty("errors").GetBoolean()) log.LogWarning("Elasticsearch rejected some documents in {Index}", index);
        }
    }

    /// <summary>Null means ask SQLite instead: Elasticsearch is down or took too long.</summary>
    public async Task<IReadOnlyList<(string Kind, long RefId, long TalkId)>?> SearchWordsAsync(string query, int limit, CancellationToken ct)
    {
        var hits = await SearchAsync(Moments, new JsonObject
        {
            ["size"] = limit,
            ["_source"] = Items("talk_id", "kind", "first_id"),
            ["query"] = new JsonObject
            {
                ["bool"] = new JsonObject
                {
                    ["must"] = WordsQuery(query),
                    ["filter"] = Items(new JsonObject { ["term"] = new JsonObject { ["doc"] = "words" } }),
                },
            },
        }, ct);
        return hits?.Select(hit =>
        {
            var source = hit.GetProperty("_source");
            return (source.GetProperty("kind").GetString()!, source.GetProperty("first_id").GetInt64(), source.GetProperty("talk_id").GetInt64());
        }).ToList();
    }

    public async Task<IReadOnlyList<VectorHit>?> SearchVectorsAsync(float[] wanted, string model, long? talkId, int limit, CancellationToken ct)
    {
        var filters = Items(new JsonObject { ["term"] = new JsonObject { ["model"] = model } });
        if (talkId is { } talk) filters.Add(new JsonObject { ["term"] = new JsonObject { ["talk_id"] = talk } });
        var hits = await SearchAsync(Moments, new JsonObject
        {
            ["size"] = limit,
            ["_source"] = Items("talk_id", "kind", "first_id", "last_id"),
            ["knn"] = Knn(wanted, limit, filters),
        }, ct);
        return hits?.Select(hit =>
        {
            var source = hit.GetProperty("_source");
            return new VectorHit(source.GetProperty("talk_id").GetInt64(), source.GetProperty("kind").GetString()!,
                source.GetProperty("first_id").GetInt64(), source.GetProperty("last_id").GetInt64(), Cosine(hit));
        }).Where(hit => hit.Score >= VectorIndex.MinScore).ToList();
    }

    public async Task<IReadOnlyList<long>?> SearchSessionWordsAsync(string query, int limit, CancellationToken ct)
    {
        var hits = await SearchAsync(Sessions, new JsonObject { ["size"] = limit, ["_source"] = Items("id"), ["query"] = WordsQuery(query) }, ct);
        return hits?.Select(hit => hit.GetProperty("_source").GetProperty("id").GetInt64()).ToList();
    }

    public async Task<IReadOnlyList<long>?> SearchSessionVectorsAsync(float[] wanted, string model, int limit, CancellationToken ct)
    {
        var filters = Items(new JsonObject { ["term"] = new JsonObject { ["model"] = model } });
        var hits = await SearchAsync(Sessions, new JsonObject { ["size"] = limit, ["_source"] = Items("id"), ["knn"] = Knn(wanted, limit, filters) }, ct);
        return hits?.Where(hit => Cosine(hit) >= VectorIndex.MinScore).Select(hit => hit.GetProperty("_source").GetProperty("id").GetInt64()).ToList();
    }

    /// <summary>A long sentence may share a word or two with anything, so most of what it asks has to be there. Forgives typos.</summary>
    private static JsonObject WordsQuery(string query) => new()
    {
        ["multi_match"] = new JsonObject
        {
            ["query"] = query,
            ["fields"] = Items("text", "text.en", "text.es"),
            ["fuzziness"] = "AUTO",
            ["minimum_should_match"] = "2<60%",
        },
    };

    private static JsonObject Knn(float[] wanted, int limit, JsonArray filters) => new()
    {
        ["field"] = "vector",
        ["query_vector"] = Items(wanted.Select(n => (JsonNode)n).ToArray()),
        ["k"] = limit,
        ["num_candidates"] = Math.Max(100, limit * 5),
        ["filter"] = filters,
    };

    private static JsonArray Items(params JsonNode[] items)
    {
        var list = new JsonArray();
        foreach (var item in items) list.Add(item);
        return list;
    }

    /// <summary>Elasticsearch reports cosine similarity as (1 + cosine) / 2.</summary>
    private static double Cosine(JsonElement hit) => hit.GetProperty("_score").GetDouble() * 2 - 1;

    private async Task<List<JsonElement>?> SearchAsync(string index, JsonObject body, CancellationToken ct)
    {
        if (!_ready) return null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(QueryTimeout);
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await http.PostAsync($"{baseUrl}/{index}/_search", content, timeout.Token);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            return doc.RootElement.GetProperty("hits").GetProperty("hits").EnumerateArray().Select(hit => hit.Clone()).ToList();
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // The exception type only: a message could echo the private question.
            log.LogWarning("Elasticsearch search failed, using SQLite: {Type}", e.GetType().Name);
            return null;
        }
    }

    private static float[] Floats(byte[] stored) => MemoryMarshal.Cast<byte, float>(stored).ToArray();
}
