using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TalkLens.Ai;
using TalkLens.Core;
using TalkLens.Web;
using Xunit;

namespace TalkLens.Tests;

public sealed class VectorTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"talklens-test-{Guid.NewGuid():N}.db");
    private readonly Store _store;

    public VectorTests() => _store = new Store(_dbPath);

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    /// <summary>Texts about the same topic get the same direction, whatever their words: that is the part word search cannot do.</summary>
    private sealed class TopicModel : IEmbeddingModel
    {
        public string Name { get; set; } = "topics/3";
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public List<string> Seen { get; } = [];

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
        {
            Calls++;
            if (Fail) throw new HttpRequestException("down");
            Seen.AddRange(texts);
            return Task.FromResult(texts.Select(Vector).ToArray());
        }

        private static float[] Vector(string text)
        {
            if (text.Contains("kernel") || text.Contains("núcleo")) return [1, 0, 0];
            if (text.Contains("salary") || text.Contains("sueldo")) return [0, 1, 0];
            return [0, 0, 1];
        }
    }

    private sealed class FakeChat(string reply) : IChatModel
    {
        public string Name => "fake";
        public string? LastUser { get; private set; }
        public Task<string?> CompleteAsync(string system, string user, byte[]? jpeg, int maxTokens, CancellationToken ct)
        {
            LastUser = user;
            return Task.FromResult<string?>(reply);
        }
    }

    private VectorIndex Index(IEmbeddingModel? model, bool online = true) => new(_store, model, () => online, NullLogger.Instance);

    private long EndedTalk(string title, bool keep = true, params string[] lines)
    {
        var talk = _store.CreateTalk(title, "", "en", keep);
        for (var i = 0; i < lines.Length; i++) _store.AddSegment(talk.Id, i * 1000, i * 1000 + 900, lines[i], "test");
        _store.EndTalk(talk.Id);
        return talk.Id;
    }

    private static Segment Seg(long id, string text) => new(id, 1, id * 1000, id * 1000 + 900, text, "test");

    [Fact]
    public void A_live_talk_keeps_its_last_short_stretch_until_it_grows()
    {
        var text = new string('a', 400);
        var chunks = VectorIndex.Chunk([Seg(1, text), Seg(2, text), Seg(3, "short")], final: false);
        Assert.Equal((1L, 2L), (chunks.Single().FirstId, chunks.Single().LastId));
        Assert.Equal(1000, chunks.Single().T);
    }

    [Fact]
    public void An_ended_talk_indexes_everything()
    {
        var chunks = VectorIndex.Chunk([Seg(1, new string('a', 700)), Seg(2, "short")], final: true);
        Assert.Equal(2, chunks.Count);
        Assert.Equal("short", chunks[1].Text);
    }

    [Fact]
    public void Fusing_prefers_what_both_searches_found_and_lands_on_the_exact_sentence()
    {
        var moments = Hybrid.Fuse(
            [("segment", 5L, 1L), ("segment", 50L, 2L)],
            [new VectorHit(3, "segment", 70, 80, 0.9), new VectorHit(1, "segment", 3, 8, 0.8)],
            10);
        Assert.Equal(new SearchMoment(1, "segment", 5, 5, false), moments[0]);
        Assert.Contains(new SearchMoment(3, "segment", 70, 80, true), moments);
        Assert.Contains(new SearchMoment(2, "segment", 50, 50, false), moments);
        Assert.Equal(3, moments.Count);
    }

    [Fact]
    public async Task A_question_in_other_words_finds_the_right_talk()
    {
        var kernel = EndedTalk("Inside Linux", true, "We attach probes to the kernel with eBPF.");
        EndedTalk("Hiring", true, "How to negotiate your salary.");
        var index = Index(new TopicModel());
        await index.IndexPendingAsync(default);

        var hit = Assert.Single(await index.SearchAsync("¿cómo se observa el núcleo?", null, 10, default));
        Assert.Equal(kernel, hit.TalkId);
    }

    [Fact]
    public async Task Search_results_mark_what_was_found_only_by_meaning()
    {
        var kernel = EndedTalk("Inside Linux", true, "We attach probes to the kernel with eBPF.");
        var index = Index(new TopicModel());
        await index.IndexPendingAsync(default);

        var meaning = await index.SearchAsync("el núcleo", null, 10, default);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(Endpoints.SearchResults(_store, "el núcleo", meaning), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var talk = Assert.Single(doc.RootElement.EnumerateArray());
        Assert.Equal(kernel, talk.GetProperty("talkId").GetInt64());
        Assert.True(talk.GetProperty("moments")[0].GetProperty("byMeaning").GetBoolean());
    }

    [Fact]
    public async Task Without_a_model_or_internet_search_still_works_by_words()
    {
        EndedTalk("Inside Linux", true, "We attach probes to the kernel with eBPF.");
        var model = new TopicModel();
        var offline = Index(model, online: false);
        await offline.IndexPendingAsync(default);
        Assert.Empty(await offline.SearchAsync("kernel", null, 10, default));
        Assert.Equal(0, model.Calls);
        Assert.Empty(await Index(null).SearchAsync("kernel", null, 10, default));

        var text = JsonSerializer.Serialize(Endpoints.SearchResults(_store, "eBPF", []));
        Assert.Contains("Inside Linux", text);
    }

    [Fact]
    public async Task A_failing_model_does_not_break_a_search()
    {
        EndedTalk("Inside Linux", true, "The kernel.");
        var model = new TopicModel();
        var index = Index(model);
        await index.IndexPendingAsync(default);
        model.Fail = true;
        Assert.Empty(await index.SearchAsync("kernel", null, 10, default));
    }

    [Fact]
    public async Task Nothing_is_embedded_twice_and_what_failed_is_caught_up_later()
    {
        EndedTalk("Inside Linux", true, "The kernel.");
        var model = new TopicModel { Fail = true };
        var index = Index(model);
        await Assert.ThrowsAsync<HttpRequestException>(() => index.IndexPendingAsync(default));
        model.Fail = false;
        await index.IndexPendingAsync(default);
        await index.IndexPendingAsync(default);
        Assert.Single(model.Seen);
    }

    [Fact]
    public async Task A_talk_the_speaker_did_not_want_kept_is_never_sent()
    {
        EndedTalk("Off the record", false, "Secret kernel plans.");
        var model = new TopicModel();
        await Index(model).IndexPendingAsync(default);
        Assert.Empty(model.Seen);
    }

    [Fact]
    public async Task Deleting_a_talk_removes_its_vectors()
    {
        var talk = EndedTalk("Inside Linux", true, "The kernel.");
        var model = new TopicModel();
        var index = Index(model);
        await index.IndexPendingAsync(default);
        _store.DeleteTalk(talk);
        Assert.Empty(_store.ReadVectors(model.Name, null));
        Assert.Empty(await index.SearchAsync("kernel", null, 10, default));
    }

    [Fact]
    public async Task A_slide_whose_text_changes_is_embedded_again()
    {
        var talk = EndedTalk("Inside Linux", true);
        var slide = _store.AddSlide(talk, 1000);
        _store.SetSlideText(slide.Id, talk, "kernel diagram", "ok");
        var model = new TopicModel();
        var index = Index(model);
        await index.IndexPendingAsync(default);
        _store.SetSlideText(slide.Id, talk, "salary table", "ok");
        await index.IndexPendingAsync(default);

        Assert.Equal(["kernel diagram", "salary table"], model.Seen);
        Assert.Equal("slide", Assert.Single(await index.SearchAsync("sueldo", null, 10, default)).Kind);
    }

    [Fact]
    public async Task Vectors_from_another_model_are_not_compared()
    {
        EndedTalk("Inside Linux", true, "The kernel.");
        var model = new TopicModel();
        await Index(model).IndexPendingAsync(default);
        model.Name = "other/3";
        Assert.Empty(await Index(model).SearchAsync("kernel", null, 10, default));
    }

    [Fact]
    public async Task A_question_across_talks_reaches_a_talk_that_shares_no_words_with_it()
    {
        var talk = EndedTalk("Inside Linux", true, "We attach probes to the kernel with eBPF.");
        var index = Index(new TopicModel());
        await index.IndexPendingAsync(default);
        var chat = new FakeChat("Hablaron de eBPF [1].");
        var answerer = new Answerer(_store, new AiModels { Chat = chat }, () => true, 2, NullLogger.Instance, index);

        var answer = await answerer.AskAcrossAsync("¿qué dijeron del núcleo?", default);

        Assert.Equal("ai", answer.Mode);
        Assert.Equal(talk, Assert.Single(answer.Talks!).TalkId);
        Assert.Contains("eBPF", chat.LastUser);
    }

    [Fact]
    public async Task A_long_talk_gives_the_model_what_matches_by_meaning()
    {
        var lines = Enumerable.Repeat(new string('x', 700), 60).ToList();
        lines.Insert(5, "We attach probes to the kernel with eBPF, a long explanation. " + new string('y', 600));
        var talk = EndedTalk("Inside Linux", true, [.. lines]);
        var index = Index(new TopicModel());
        await index.IndexPendingAsync(default);
        var chat = new FakeChat("Con eBPF [1].");
        var answerer = new Answerer(_store, new AiModels { Chat = chat }, () => true, 2, NullLogger.Instance, index);

        await answerer.AskAsync(talk, "¿qué dijeron del núcleo?", default);

        Assert.Contains("eBPF", chat.LastUser);
    }

    /// <summary>Spends a little on the real model, so it only runs when asked: TALKLENS_LIVE_EMBEDDINGS=1 and OPENAI_API_KEY.</summary>
    [Fact]
    public async Task The_real_model_finds_paraphrases_across_languages()
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (Environment.GetEnvironmentVariable("TALKLENS_LIVE_EMBEDDINGS") != "1" || string.IsNullOrEmpty(key)) return;
        using var http = new HttpClient();
        var index = Index(new OpenAiEmbeddings(http, "https://api.openai.com/v1", key, "text-embedding-3-small", 512));
        var network = EndedTalk("Venue networks", true, "Every phone connects to our own router, so the video never needs the internet.", "The delay stays under five seconds, even with three hundred people watching.");
        var hiring = EndedTalk("Hiring engineers", true, "When you negotiate your salary, always ask for the whole range first.", "Good references matter more than a perfect resume.");

        await index.IndexPendingAsync(default);

        Assert.Equal(network, (await index.SearchAsync("¿cómo llega el video a los celulares sin internet?", null, 5, default)).First().TalkId);
        Assert.Equal(hiring, (await index.SearchAsync("¿cuánto dinero debo pedir en una entrevista de trabajo?", null, 5, default)).First().TalkId);
        Assert.Empty(await index.SearchAsync("receta tradicional de mole poblano", null, 5, default));
    }

    [Fact]
    public void Fusing_sessions_ranks_agreement_first_and_marks_what_only_meaning_found()
    {
        var hits = Hybrid.FuseSessions([1, 2], [2, 3], 10);

        Assert.Equal([2L, 1L, 3L], hits.Select(h => h.Id));
        Assert.False(hits[0].ByMeaning);
        Assert.True(hits[2].ByMeaning);
    }

    [Fact]
    public async Task The_schedule_is_found_by_what_a_talk_is_about_even_when_the_words_are_not_there()
    {
        var kernel = _store.AddAgenda(1, "Watching the kernel", "Ada", 1000, 2000, "");
        var salary = _store.AddAgenda(1, "Your salary", "Luis", 1000, 2000, "");
        var index = Index(new TopicModel());
        await index.IndexPendingAsync(default);

        var hits = await index.SessionsAsync("el núcleo", true, 10, default);

        Assert.Equal(kernel.Id, Assert.Single(hits).Id);
        Assert.True(hits[0].ByMeaning);
        Assert.Equal(salary.Id, Assert.Single(await index.SessionsAsync("SALARY", false, 10, default)).Id);
    }

    [Fact]
    public async Task A_schedule_change_is_embedded_again_and_a_deleted_talk_leaves_no_vector()
    {
        var item = _store.AddAgenda(1, "Watching the kernel", "", 1000, 2000, "");
        var model = new TopicModel();
        var index = Index(model);
        await index.IndexPendingAsync(default);
        await index.IndexPendingAsync(default);
        Assert.Equal(1, model.Calls);

        _store.DeleteAgenda(item.Id);

        Assert.Empty(_store.ReadAgendaVectors());
    }

    [Fact]
    public async Task Without_elasticsearch_search_carries_on_with_sqlite()
    {
        using var http = new HttpClient();
        var elastic = new ElasticIndex(http, "http://127.0.0.1:9", "talklens-test", _store, NullLogger.Instance);
        var index = new VectorIndex(_store, new TopicModel(), () => true, NullLogger.Instance, elastic);
        var talk = EndedTalk("Tracing", true, "We trace the kernel with eBPF.");
        _store.AddAgenda(1, "Watching the kernel", "", 1000, 2000, "");
        await index.IndexPendingAsync(default);

        await elastic.SyncAsync(default);

        Assert.False(elastic.Ready);
        Assert.Equal(talk, (await index.WordsAsync("eBPF", 5, default)).Single().TalkId);
        Assert.Single(await index.SessionsAsync("kernel", true, 5, default));
    }

    /// <summary>Needs Elasticsearch running, so it only runs when asked: TALKLENS_LIVE_ELASTIC=1. Uses its own indices and removes them.</summary>
    [Fact]
    public async Task Elasticsearch_finds_by_words_and_meaning_and_stays_in_step()
    {
        if (Environment.GetEnvironmentVariable("TALKLENS_LIVE_ELASTIC") != "1") return;
        const string Url = "http://127.0.0.1:9200";
        var prefix = $"talklens-test-{Guid.NewGuid():N}";
        using var http = new HttpClient();
        var elastic = new ElasticIndex(http, Url, prefix, _store, NullLogger.Instance);
        var index = new VectorIndex(_store, new TopicModel(), () => true, NullLogger.Instance, elastic);
        try
        {
            // Long enough to be a chunk of its own even before the talk ends.
            var kernel = EndedTalk("Tracing", true, "We trace the kernel with eBPF and it changes how security teams work.");
            var salary = EndedTalk("Careers", true, "Always ask about the salary range before anything else.");
            var hidden = EndedTalk("Private", false, "The kernel secret that nobody should find.");
            var session = _store.AddAgenda(1, "Your salary", "Luis", 1000, 2000, "Negotiating without fear");
            await index.IndexPendingAsync(default);

            await elastic.SyncAsync(default);
            await elastic.SyncAsync(default);

            Assert.True(elastic.Ready);
            // A typo and another word ending, which SQLite would miss.
            Assert.Equal(kernel, (await index.WordsAsync("tracing kernal", 5, default)).First().TalkId);
            var meaning = await index.SearchAsync("el núcleo", null, 5, default);
            Assert.Equal(kernel, meaning.First().TalkId);
            Assert.InRange(meaning.First().Score, 0.99, 1.01);
            Assert.DoesNotContain(meaning, hit => hit.TalkId == hidden);
            Assert.Equal(salary, (await index.SearchAsync("el sueldo", salary, 5, default)).Single().TalkId);
            Assert.Equal(session.Id, (await index.SessionsAsync("el sueldo", true, 5, default)).First().Id);
            Assert.Equal(session.Id, (await index.SessionsAsync("negociating", false, 5, default)).Single().Id);

            // A deleted talk disappears from search.
            _store.DeleteTalk(salary);
            await elastic.SyncAsync(default);
            Assert.DoesNotContain(await index.WordsAsync("salary range", 5, default), hit => hit.TalkId == salary);

            // A wiped index fills again by itself, without new vectors.
            await http.DeleteAsync($"{Url}/{prefix}-moments");
            await elastic.SyncAsync(default);
            await elastic.SyncAsync(default);
            Assert.Equal(kernel, (await index.SearchAsync("el núcleo", null, 5, default)).First().TalkId);
        }
        finally
        {
            await http.DeleteAsync($"{Url}/{prefix}-moments");
            await http.DeleteAsync($"{Url}/{prefix}-sessions");
        }
    }
}
