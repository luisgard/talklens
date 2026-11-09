using TalkLens.Core;
using Xunit;

namespace TalkLens.Tests;

public class TranslationBufferTests
{
    private static Segment Piece(long id, string text) => new(id, 1, id, id, text, "cloud");

    private sealed class Rig
    {
        public readonly List<long[]> Batches = [];
        public readonly TaskCompletionSource Flushed = new();
        public TranslationBuffer Buffer { get; }

        public Rig(int holdMs = 50, CancellationToken stop = default)
        {
            Buffer = new TranslationBuffer(TimeSpan.FromMilliseconds(holdMs), pieces =>
            {
                lock (Batches) Batches.Add(pieces.Select(p => p.Id).ToArray());
                Flushed.TrySetResult();
            }, stop);
        }
    }

    [Fact]
    public void A_whole_sentence_goes_straight_away()
    {
        var rig = new Rig(holdMs: 60000);
        rig.Buffer.Add(Piece(1, "Hablemos de privacidad."));
        Assert.Equal([1L], Assert.Single(rig.Batches));
    }

    [Theory]
    [InlineData("¿Me escuchan?")]
    [InlineData("¡Qué loco!")]
    [InlineData("Y entonces… ")]
    public void Other_sentence_endings_count(string text)
    {
        var rig = new Rig(holdMs: 60000);
        rig.Buffer.Add(Piece(1, text));
        Assert.Single(rig.Batches);
    }

    [Fact]
    public void Pieces_wait_for_the_end_of_the_sentence()
    {
        var rig = new Rig(holdMs: 60000);
        rig.Buffer.Add(Piece(1, "Estamos usando la inteligencia artificial de"));
        rig.Buffer.Add(Piece(2, "OpenAI"));
        Assert.Empty(rig.Batches);
        rig.Buffer.Add(Piece(3, "y funciona."));
        Assert.Equal([1L, 2L, 3L], Assert.Single(rig.Batches));
    }

    [Fact]
    public async Task A_sentence_that_never_ends_goes_after_the_hold()
    {
        var rig = new Rig();
        rig.Buffer.Add(Piece(1, "Por ejemplo,"));
        rig.Buffer.Add(Piece(2, "como que"));
        await rig.Flushed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([1L, 2L], Assert.Single(rig.Batches));
    }

    [Fact]
    public async Task An_old_hold_does_not_cut_the_next_sentence_short()
    {
        var rig = new Rig(holdMs: 300);
        rig.Buffer.Add(Piece(1, "uno"));
        rig.Buffer.Add(Piece(2, "dos."));
        await Task.Delay(200);
        rig.Buffer.Add(Piece(3, "tres"));
        await Task.Delay(150);
        lock (rig.Batches) Assert.Equal([1L, 2L], Assert.Single(rig.Batches));
    }

    [Fact]
    public async Task Nothing_is_sent_after_the_talk_stops()
    {
        using var stop = new CancellationTokenSource();
        var rig = new Rig(stop: stop.Token);
        rig.Buffer.Add(Piece(1, "uno"));
        stop.Cancel();
        await Task.Delay(150);
        Assert.Empty(rig.Batches);
    }
}
