using Microsoft.Extensions.Logging.Abstractions;
using TalkLens.Ai;
using TalkLens.Core;
using Xunit;

namespace TalkLens.Tests;

public sealed class AnswererTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"talklens-test-{Guid.NewGuid():N}.db");
    private readonly Store _store;
    private readonly long _talk;

    public AnswererTests()
    {
        _store = new Store(_dbPath);
        _talk = _store.CreateTalk("Demo", "Ada", "es", true).Id;
        _store.AddSegment(_talk, 1000, 4000, "primero hablemos de los costos de la plataforma", "local");
        var slide = _store.AddSlide(_talk, 60000);
        _store.SetSlideText(slide.Id, _talk, "Diagrama de red: router y celulares", "cloud");
        _store.AddSegment(_talk, 61000, 66000, "aquí cada asistente se conecta sin internet", "local");
        var next = _store.AddSlide(_talk, 120000);
        _store.SetSlideText(next.Id, _talk, "Privacidad", "cloud");
        _store.AddSegment(_talk, 121000, 125000, "no usamos reconocimiento facial", "local");
    }

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    private sealed class FakeChat(Func<string, string?> reply) : IChatModel
    {
        public string Name => "fake";
        public string LastUser { get; private set; } = "";

        public Task<string?> CompleteAsync(string system, string user, byte[]? jpeg, int maxTokens, CancellationToken ct)
        {
            LastUser = user;
            return Task.FromResult(reply(user));
        }
    }

    private sealed class HangingChat : IChatModel
    {
        public string Name => "hanging";

        public async Task<string?> CompleteAsync(string system, string user, byte[]? jpeg, int maxTokens, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        }
    }

    private Answerer Build(IChatModel? chat, bool online = true) =>
        new(_store, new AiModels { Chat = chat }, () => online, 0.2, NullLogger.Instance);

    [Fact]
    public async Task Slide_match_brings_in_what_was_said_while_it_was_on_screen()
    {
        var items = Retriever.Find(_store, _talk, "¿qué explicó cuando mostró el diagrama de red?");
        Assert.Contains(items, i => i.Kind == "slide" && i.Text.Contains("Diagrama"));
        Assert.Contains(items, i => i.Kind == "segment" && i.Text.Contains("sin internet"));
        Assert.DoesNotContain(items, i => i.Text.Contains("reconocimiento facial"));

        var offline = await Build(null).AskAsync(_talk, "¿qué explicó cuando mostró el diagrama de red?", default);
        Assert.Equal("offline", offline.Mode);
        Assert.Contains(offline.Citations, c => c.T == 61000);
    }

    [Fact]
    public async Task Citations_are_renumbered_and_point_at_real_moments()
    {
        var chat = new FakeChat(_ => "Cada asistente se conecta sin internet [3]. Y el router [2].");
        var answer = await Build(chat).AskAsync(_talk, "¿cómo se conectan?", default);

        Assert.Equal("ai", answer.Mode);
        Assert.Equal("Cada asistente se conecta sin internet [1]. Y el router [2].", answer.Text);
        Assert.Equal(61000, answer.Citations[0].T);
        Assert.Equal(60000, answer.Citations[1].T);
    }

    [Fact]
    public async Task Model_saying_not_found_reaches_the_attendee_as_not_found()
    {
        var answer = await Build(new FakeChat(_ => Answerer.NotFoundMarker)).AskAsync(_talk, "¿cuál es la capital de Francia?", default);
        Assert.Equal("not_found", answer.Mode);
        Assert.Empty(answer.Citations);
    }

    [Theory]
    [InlineData("La capital de Francia es París.")]
    [InlineData("París [99].")]
    public async Task Answers_without_a_real_citation_are_discarded(string reply)
    {
        var answer = await Build(new FakeChat(_ => reply)).AskAsync(_talk, "¿cuál es la capital?", default);
        Assert.Equal("not_found", answer.Mode);
    }

    [Fact]
    public async Task Slow_model_falls_back_to_excerpts_within_the_time_budget()
    {
        var answer = await Build(new HangingChat()).AskAsync(_talk, "costos de la plataforma", default);
        Assert.Equal("offline", answer.Mode);
        Assert.Contains(answer.Citations, c => c.T == 1000);
    }

    [Fact]
    public async Task Offline_question_about_something_never_said_is_not_found()
    {
        var chat = new FakeChat(_ => throw new InvalidOperationException("must not be called offline"));
        var answer = await Build(chat, online: false).AskAsync(_talk, "xilófono zanahoria", default);
        Assert.Equal("not_found", answer.Mode);
    }

    [Fact]
    public async Task Talk_with_nothing_indexed_is_empty_and_never_calls_the_model()
    {
        var empty = _store.CreateTalk("New", "Ada", "es", true).Id;
        var chat = new FakeChat(_ => throw new InvalidOperationException("must not be called"));
        Assert.Equal("empty", (await Build(chat).AskAsync(empty, "¿algo?", default)).Mode);
    }

    [Fact]
    public async Task Catch_up_only_uses_the_requested_window()
    {
        var chat = new FakeChat(_ => "- Privacidad [1]");
        await Build(chat).CatchUpAsync(_talk, 100000, "es", default);
        Assert.Contains("reconocimiento facial", chat.LastUser);
        Assert.DoesNotContain("costos", chat.LastUser);
    }

    [Theory]
    [InlineData("what is \"this\" OR NEAR(x) *", "\"NEAR\"*")]
    [InlineData("eBPF; DROP TABLE talks--", "\"eBPF\"*")]
    public void User_text_cannot_become_query_syntax(string question, string expectedTerm)
    {
        var query = Retriever.BuildQuery(question);
        Assert.Contains(expectedTerm, query);
        Assert.Empty(_store.Search(_talk, query, 5));
    }

    [Fact]
    public void Questions_of_only_stop_words_search_nothing()
    {
        Assert.Equal("", Retriever.BuildQuery("¿qué dijo?"));
        Assert.Empty(_store.Search(_talk, "", 5));
    }

    [Fact]
    public void Search_ignores_accents_and_case()
    {
        Assert.NotEmpty(_store.Search(_talk, Retriever.BuildQuery("RECONOCIMIENTO FACÍAL"), 5));
    }

    [Fact]
    public void Deleting_a_talk_removes_it_from_search()
    {
        _store.DeleteTalk(_talk);
        Assert.Empty(_store.Search(_talk, Retriever.BuildQuery("costos"), 5));
        Assert.Empty(_store.GetSegments(_talk));
    }
}
