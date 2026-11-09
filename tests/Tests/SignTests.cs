using Microsoft.Extensions.Logging.Abstractions;
using TalkLens.Ai;
using TalkLens.Core;
using Xunit;

namespace TalkLens.Tests;

public sealed class SignTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"talklens-signs-{Guid.NewGuid():N}.sigml");
    private readonly SignLexicon _lexicon;

    public SignTests()
    {
        File.WriteAllText(_path, """
            <sigml>
              <hamgestural_sign gloss="woman"><sign_manual/></hamgestural_sign>
              <hamgestural_sign gloss="see"><sign_manual/></hamgestural_sign>
            </sigml>
            """);
        _lexicon = new SignLexicon(_path);
    }

    public void Dispose() => File.Delete(_path);

    private sealed class FakeChat(Func<string?> reply) : IChatModel
    {
        public string Name => "fake";
        public int Calls { get; private set; }

        public Task<string?> CompleteAsync(string system, string user, byte[]? jpeg, int maxTokens, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(reply());
        }
    }

    private SignTranslator Build(IChatModel? chat, bool online = true) =>
        new(_lexicon, new AiModels { Chat = chat }, () => online, NullLogger.Instance);

    [Fact]
    public void Live_captions_are_cut_while_spoken_and_nothing_is_sent_twice()
    {
        var chunker = new SignChunker();

        // The last word may still change, and two words are too few to mean much.
        Assert.Empty(Texts(chunker.Add("a", "Today I", false)));
        Assert.Empty(Texts(chunker.Add("a", "Today I want", false)));
        Assert.Equal(["Today I want to talk about"], Texts(chunker.Add("a", "Today I want to talk about how", false)));
        Assert.Empty(Texts(chunker.Add("a", "Today I want to talk about how", false)));
        Assert.Equal(["how we run."], Texts(chunker.Add("a", "Today I want to talk about how we run.", true)));
    }

    [Fact]
    public void A_piece_ends_at_a_pause_when_there_is_one_and_captions_do_not_mix()
    {
        var chunker = new SignChunker();

        Assert.Equal(["Well, thank you,"], Texts(chunker.Add("a", "Well, thank you, everyone here", false)));
        Assert.Equal(["Good"], Texts(chunker.Add("b", "Good", true)));
        Assert.Equal(["everyone here today"], Texts(chunker.Add("a", "Well, thank you, everyone here today", true)));
    }

    [Fact]
    public async Task Pieces_arrive_in_the_order_they_were_said_however_long_each_takes()
    {
        var sent = new List<object>();
        var first = new TaskCompletionSource<string?>();
        var chat = new QueuedChat(first.Task, Task.FromResult<string?>("[\"see\"]"));
        var signer = new LiveSigner(Build(chat), () => true, item => { sent.Add(item); return Task.CompletedTask; }, NullLogger.Instance, default);

        signer.Add("a", "the woman runs. she sees", true);
        Assert.Empty(sent);
        first.SetResult("[\"woman\"]");
        await signer.Drained;

        Assert.Equal(2, sent.Count);
        Assert.Contains("woman", System.Text.Json.JsonSerializer.Serialize(sent[0]));
        Assert.Contains("see", System.Text.Json.JsonSerializer.Serialize(sent[1]));
    }

    [Fact]
    public async Task Nothing_is_translated_while_nobody_watches_and_turning_it_on_does_not_replay()
    {
        var sent = new List<object>();
        var watching = false;
        var chat = new FakeChat(() => "[\"woman\"]");
        var signer = new LiveSigner(Build(chat), () => watching, item => { sent.Add(item); return Task.CompletedTask; }, NullLogger.Instance, default);

        signer.Add("a", "the woman runs far away today now", false);
        Assert.Equal(0, chat.Calls);

        watching = true;
        signer.Add("a", "the woman runs far away today now and then", true);
        await signer.Drained;
        Assert.Equal(1, chat.Calls);
        Assert.Single(sent);
    }

    [Fact]
    public async Task What_it_signs_is_kept_at_the_moment_it_was_said()
    {
        var kept = new List<long>();
        var chat = new FakeChat(() => "[\"woman\"]");
        var signer = new LiveSigner(Build(chat), () => true, _ => Task.CompletedTask, NullLogger.Instance, default, (t, _) => kept.Add(t));

        // Ten words over ten seconds: the second piece starts at the sixth word.
        signer.Add("a", "the woman runs far away. she sees the big house", true, 10_000, 20_000);
        await signer.Drained;

        Assert.Equal([10_000L, 15_000L], kept);
    }

    private static IEnumerable<string> Texts(IEnumerable<SignPiece> pieces) => pieces.Select(p => p.Text);

    private sealed class QueuedChat(params Task<string?>[] replies) : IChatModel
    {
        private int _next;
        public string Name => "queued";
        public Task<string?> CompleteAsync(string system, string user, byte[]? jpeg, int maxTokens, CancellationToken ct) => replies[_next++];
    }

    [Fact]
    public async Task Only_lexicon_signs_reach_the_avatar_whatever_the_model_says()
    {
        var chat = new FakeChat(() => """["woman","<sign_manual/>","fly","?see"]""");
        var result = await Build(chat).TranslateAsync("The woman flies", default);

        Assert.True(result.ByModel);
        Assert.Equal(["woman", "<sign_manual/>", "fly", "see"], result.Tokens.Select(t => t.Word));
        Assert.Equal([true, false, false, false], result.Tokens.Select(t => t.HasSign));
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(result.Sigml, "<hamgestural_sign"));
        Assert.Contains("gloss=\"woman\"", result.Sigml);
    }

    [Fact]
    public async Task Without_a_model_it_goes_word_by_word_and_shows_the_gaps()
    {
        foreach (var translator in new[] { Build(null), Build(new FakeChat(() => "[\"see\"]"), online: false) })
        {
            var result = await translator.TranslateAsync("The woman runs", default);
            Assert.False(result.ByModel);
            Assert.Equal(["woman", "runs"], result.Tokens.Select(t => t.Word));
            Assert.Equal([true, false], result.Tokens.Select(t => t.HasSign));
        }
    }

    [Theory]
    [InlineData("I cannot help with that")]
    [InlineData("[woman, see")]
    [InlineData("{\"signs\":1}")]
    public async Task A_useless_reply_falls_back_to_word_by_word(string reply)
    {
        var result = await Build(new FakeChat(() => reply)).TranslateAsync("woman", default);
        Assert.False(result.ByModel);
        Assert.Equal(["woman"], result.Tokens.Select(t => t.Word));
    }

    [Fact]
    public async Task A_model_that_corrects_itself_counts_with_its_last_answer()
    {
        var reply = """
            ["woman","and"]

            Wait, "and" is not in the list. Corrected:

            ["woman","?and"]
            """;
        var result = await Build(new FakeChat(() => reply)).TranslateAsync("woman and", default);
        Assert.True(result.ByModel);
        Assert.Equal(["woman", "and"], result.Tokens.Select(t => t.Word));
        Assert.Equal([true, false], result.Tokens.Select(t => t.HasSign));
    }

    [Fact]
    public async Task The_same_caption_only_costs_one_call()
    {
        var chat = new FakeChat(() => """["see"]""");
        var translator = Build(chat);
        await translator.TranslateAsync("I see", default);
        await translator.TranslateAsync(" I see ", default);
        Assert.Equal(1, chat.Calls);
    }

    [Fact]
    public void The_shipped_lexicon_loads_and_is_valid_xml()
    {
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "Server"))) root = Path.GetDirectoryName(root)!;
        var shipped = new SignLexicon(Path.Combine(root, "src", "Server", "wwwroot", "signs", "lexicon.sigml"));
        Assert.True(shipped.Glosses.Count > 800);
        Assert.Contains("hns_sign", shipped.Find("hello"));
    }
}
