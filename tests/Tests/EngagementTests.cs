using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TalkLens.Ai;
using TalkLens.Core;
using Xunit;

namespace TalkLens.Tests;

public sealed class EngagementTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"talklens-test-{Guid.NewGuid():N}.db");
    private readonly Store _store;

    public EngagementTests() => _store = new Store(_dbPath);

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    private sealed class FakeChat(Func<string, string, string?> reply) : IChatModel
    {
        public string Name => "fake";
        public int Calls { get; private set; }
        public Task<string?> CompleteAsync(string system, string user, byte[]? jpeg, int maxTokens, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(reply(system, user));
        }
    }

    private TalkInsights Insights(FakeChat? chat, bool online = true) =>
        new(_store, new AiModels { Chat = chat }, () => online, NullLogger.Instance);

    private Talk TalkWith(params string[] lines)
    {
        var talk = _store.CreateTalk("Testing", "Ada", "en", true);
        for (var i = 0; i < lines.Length; i++) _store.AddSegment(talk.Id, i * 10_000, i * 10_000 + 8_000, lines[i], "cloud");
        _store.EndTalk(talk.Id);
        return _store.FindTalk(talk.Id)!;
    }

    // ----- Polls -----
    [Fact]
    public void A_phone_votes_once_and_can_change_its_mind()
    {
        var talk = TalkWith("hello");
        var poll = _store.OpenPoll(talk.Id, "Which?", ["Tests", "Deploys"]);
        _store.Vote(poll.Id, "phone-a", 0);
        _store.Vote(poll.Id, "phone-a", 1);
        _store.Vote(poll.Id, "phone-b", 1);

        Assert.Equal([0, 2], _store.PollCounts(poll));
        Assert.Equal(1, _store.VoteOf(poll.Id, "phone-a"));
        Assert.Null(_store.VoteOf(poll.Id, "phone-c"));
    }

    [Fact]
    public void Opening_a_poll_closes_the_one_before()
    {
        var talk = TalkWith("hello");
        var first = _store.OpenPoll(talk.Id, "One?", ["a", "b"]);
        var second = _store.OpenPoll(talk.Id, "Two?", ["c", "d"]);

        Assert.False(_store.FindPoll(first.Id)!.Open);
        Assert.True(_store.FindPoll(second.Id)!.Open);
        _store.ClosePoll(second.Id);
        Assert.False(_store.FindPoll(second.Id)!.Open);
    }

    [Fact]
    public void Deleting_a_talk_takes_its_polls_ratings_and_ai_extras_with_it()
    {
        var talk = TalkWith("hello");
        var poll = _store.OpenPoll(talk.Id, "One?", ["a", "b"]);
        _store.Vote(poll.Id, "phone", 0);
        _store.Rate(talk.Id, "phone", 5, "great");
        _store.SetExtra(talk.Id, "chapters", "en", "[]");

        _store.DeleteTalk(talk.Id);

        Assert.Empty(_store.ListPolls(talk.Id));
        Assert.Empty(_store.ListRatings(talk.Id));
        Assert.Null(_store.GetExtra(talk.Id, "chapters", "en"));
    }

    // ----- Ratings and the overview -----
    [Fact]
    public void Ratings_and_their_comments_stay_hidden_until_enough_people_rated()
    {
        var talk = TalkWith("hello");
        var reports = new Reports(_store, new AiModels(), () => false, NullLogger.Instance);
        for (var i = 0; i < Settings.DefaultMinParticipants - 1; i++) _store.Rate(talk.Id, $"phone-{i}", 4, "nice");

        var few = JsonSerializer.Serialize(reports.SpeakerFeedback(talk));
        Assert.Contains("\"rating\":null", few);
        Assert.DoesNotContain("nice", few);

        _store.Rate(talk.Id, "phone-last", 2, "");
        var enough = JsonSerializer.SerializeToElement(reports.SpeakerFeedback(talk));
        var rating = enough.GetProperty("rating");
        Assert.Equal(3.6, rating.GetProperty("average").GetDouble());
        Assert.Equal(Settings.DefaultMinParticipants, rating.GetProperty("count").GetInt32());
        Assert.Equal([0, 1, 0, 4, 0], rating.GetProperty("stars").EnumerateArray().Select(e => e.GetInt32()));
    }

    [Fact]
    public void The_overview_compares_talks_and_names_the_best_rated()
    {
        var reports = new Reports(_store, new AiModels(), () => false, NullLogger.Instance);
        var good = TalkWith("hello");
        var better = TalkWith("hi");
        for (var i = 0; i < Settings.DefaultMinParticipants; i++)
        {
            _store.Rate(good.Id, $"p{i}", 3, "");
            _store.Rate(better.Id, $"p{i}", 5, "");
        }

        var overview = JsonSerializer.SerializeToElement(reports.Overview());
        Assert.Equal(2, overview.GetProperty("talks").GetArrayLength());
        Assert.Equal(2 * Settings.DefaultMinParticipants, overview.GetProperty("totals").GetProperty("ratings").GetInt32());
        Assert.Equal("Testing", overview.GetProperty("best").GetProperty("rated").GetString());
        // Too few people watched to show attendance, so no talk is named the most attended.
        Assert.Equal(JsonValueKind.Null, overview.GetProperty("best").GetProperty("attended").ValueKind);
    }

    // ----- Made by the AI -----
    [Fact]
    public async Task Subtitles_are_translated_in_order_and_kept_once_the_talk_is_over()
    {
        var talk = TalkWith("Hello everyone", "Today we test");
        var chat = new FakeChat((_, user) => user.Contains("Hello everyone") ? "[\"Hola a todos\",\"Hoy probamos\"]" : null);

        var lines = await Insights(chat).SubtitlesAsync(talk, false, "es", default);
        Assert.Equal(["Hola a todos", "Hoy probamos"], lines!.Select(l => l.Text));
        Assert.Equal([0L, 10_000L], lines!.Select(l => l.T));

        // A second phone, even after a restart, gets the kept copy without another call.
        var again = await Insights(chat).SubtitlesAsync(talk, false, "es", default);
        Assert.Equal(1, chat.Calls);
        Assert.Equal("Hoy probamos", again![1].Text);
    }

    [Fact]
    public async Task The_original_language_needs_no_model_and_a_wrong_count_is_not_kept()
    {
        var talk = TalkWith("Hello everyone", "Today we test");
        var original = await Insights(null).SubtitlesAsync(talk, false, "en", default);
        Assert.Equal("Hello everyone", original![0].Text);

        var chat = new FakeChat((_, _) => "[\"Solo una\"]");
        Assert.Null(await Insights(chat).SubtitlesAsync(talk, false, "es", default));
        Assert.Null(_store.GetExtra(talk.Id, "subtitles", "es"));
        Assert.Null(await Insights(new FakeChat((_, _) => "[]"), online: false).SubtitlesAsync(talk, false, "es", default));
    }

    [Fact]
    public void Chapters_snap_to_real_lines_and_start_at_the_beginning()
    {
        var segments = new[] { 0L, 10_000, 20_000, 30_000 }.Select((t, i) => new Segment(i, 1, t, t + 5000, "x", "cloud")).ToList();
        var chapters = TalkInsights.CleanChapters(
            [new Chapter(4_000, "Why tests"), new Chapter(19_000, "How to start"), new Chapter(21_000, "Duplicate"), new Chapter(30_000, " ")],
            segments);

        Assert.Equal([0L, 20_000L], chapters.Select(c => c.T));
        Assert.Equal(["Why tests", "How to start"], chapters.Select(c => c.Title));
    }

    [Fact]
    public void The_glossary_keeps_only_terms_that_were_said()
    {
        var segments = new[] { new Segment(1, 1, 0, 5000, "We trace the kernel with eBPF and Kubernetes", "cloud") };
        var terms = TalkInsights.CleanGlossary(
            [new GlossaryTerm("eBPF", "Runs small programs in the kernel."), new GlossaryTerm("ebpf", "Again."), new GlossaryTerm("Rust", "A language.")],
            segments);

        Assert.Equal(["eBPF"], terms.Select(t => t.Term));
    }

    [Theory]
    [InlineData("[\"a\",\"b\"]", 2)]
    [InlineData("Here you go:\n[\"a\"]", 1)]
    [InlineData("See [1].\n[\"a\",\"b\",\"c\"]", 3)]
    [InlineData("no array here", -1)]
    [InlineData("[\"broken\"", -1)]
    public void The_reply_array_is_found_or_refused(string reply, int expected) =>
        Assert.Equal(expected, TalkInsights.LastArray<string>(reply)?.Count ?? -1);
}
