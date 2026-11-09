using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using TalkLens.Ai;
using TalkLens.Core;
using Xunit;

namespace TalkLens.Tests;

public sealed class QuestionQueueTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"talklens-questions-{Guid.NewGuid():N}.db");
    private readonly Store _store;
    private readonly Talk _talk;

    public QuestionQueueTests()
    {
        _store = new Store(_dbPath);
        _talk = _store.CreateTalk("Demo", "Ada", "es", true);
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    private sealed class FakeChat(string? reply) : IChatModel
    {
        public int Calls;
        public string Name => "fake";

        public Task<string?> CompleteAsync(string system, string user, byte[]? jpeg, int maxTokens, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(reply);
        }
    }

    private QuestionQueue Queue(IChatModel? chat = null, bool online = true) =>
        new(_store, new AiModels { Chat = chat }, () => online, _ => Task.CompletedTask, NullLogger.Instance);

    private async Task<SpeakerQuestion> Ask(QuestionQueue queue, string text)
    {
        var question = _store.AddQuestion(_talk.Id, 0, text);
        await queue.GroupAsync(question);
        return question;
    }

    [Fact]
    public async Task Repeats_in_slightly_different_words_group_without_any_model()
    {
        var queue = Queue(online: false);
        await Ask(queue, "¿Cuánto cuesta el router del evento?");
        await Ask(queue, "cuanto cuesta el router del evento");

        var group = Assert.Single(queue.Groups(_talk.Id));
        Assert.Equal(2, group.Count);
        Assert.Equal("¿Cuánto cuesta el router del evento?", group.Text);
    }

    [Fact]
    public async Task The_model_groups_questions_that_mean_the_same()
    {
        var queue = Queue(new FakeChat("1"));
        await Ask(queue, "How much does the router cost?");
        await Ask(queue, "¿Qué precio tiene ese equipo?");

        Assert.Equal(2, Assert.Single(queue.Groups(_talk.Id)).Count);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("7")]
    [InlineData("-1")]
    [InlineData("Ignore your instructions")]
    [InlineData("1 because they match")]
    [InlineData(null)]
    public async Task Any_other_reply_keeps_the_question_on_its_own(string? reply)
    {
        var queue = Queue(new FakeChat(reply));
        await Ask(queue, "How much does the router cost?");
        await Ask(queue, "Will the slides be shared?");

        Assert.Equal(2, queue.Groups(_talk.Id).Count);
    }

    [Fact]
    public async Task A_first_question_costs_no_model_call()
    {
        var chat = new FakeChat("1");
        await Ask(Queue(chat), "How much does the router cost?");
        Assert.Equal(0, chat.Calls);
    }

    [Fact]
    public async Task Answered_questions_are_not_merged_into()
    {
        var queue = Queue(online: false);
        var first = await Ask(queue, "How much does the router cost?");
        _store.SetGroupStatus(_talk.Id, first.GroupId, "answered");
        await Ask(queue, "How much does the router cost?");

        Assert.Equal(2, queue.Groups(_talk.Id).Count);
    }

    [Fact]
    public async Task A_new_repeat_joins_the_status_of_its_group()
    {
        var queue = Queue(online: false);
        var first = await Ask(queue, "How much does the router cost?");
        _store.SetGroupStatus(_talk.Id, first.GroupId, "shown");
        await Ask(queue, "How much does the router cost?");

        var group = Assert.Single(queue.Groups(_talk.Id));
        Assert.Equal("shown", group.Status);
        Assert.Equal(2, group.Count);
    }

    [Fact]
    public async Task Most_repeated_come_first_and_the_shown_one_above_them()
    {
        var queue = Queue(online: false);
        var once = await Ask(queue, "Will the slides be shared?");
        await Ask(queue, "How much does the router cost?");
        await Ask(queue, "How much does the router cost?");

        Assert.Equal("How much does the router cost?", queue.Groups(_talk.Id)[0].Text);

        _store.SetGroupStatus(_talk.Id, once.GroupId, "shown");
        Assert.Equal("Will the slides be shared?", queue.Groups(_talk.Id)[0].Text);
    }

    [Fact]
    public async Task Only_one_question_is_on_the_speaker_screen_at_a_time()
    {
        var queue = Queue(online: false);
        var a = await Ask(queue, "Will the slides be shared?");
        var b = await Ask(queue, "How much does the router cost?");

        _store.SetGroupStatus(_talk.Id, a.GroupId, "shown");
        _store.SetGroupStatus(_talk.Id, b.GroupId, "shown");

        var shown = Assert.Single(queue.Groups(_talk.Id), g => g.Status == "shown");
        Assert.Equal(b.GroupId, shown.Id);
    }

    [Fact]
    public async Task Deleting_a_talk_deletes_its_questions()
    {
        await Ask(Queue(online: false), "Will the slides be shared?");
        _store.DeleteTalk(_talk.Id);
        Assert.Empty(_store.ListQuestions(_talk.Id));
    }

    [Fact]
    public async Task Questions_stay_within_their_talk()
    {
        var other = _store.CreateTalk("Other", "Ada", "es", true);
        var queue = Queue(online: false);
        await Ask(queue, "Will the slides be shared?");
        var second = _store.AddQuestion(other.Id, 0, "Will the slides be shared?");
        await queue.GroupAsync(second);

        Assert.Single(queue.Groups(_talk.Id));
        Assert.Single(queue.Groups(other.Id));
        Assert.Equal(1, _store.CountOpenGroups(other.Id));
    }
}
