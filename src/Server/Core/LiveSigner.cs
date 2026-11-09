using TalkLens.Ai;

namespace TalkLens.Core;

/// <param name="Start">Where the piece begins, as a share of the caption, so a replay can sign it when it was said.</param>
public sealed record SignPiece(string Text, double Start);

/// <summary>Cuts captions into short pieces while they are still being spoken, so the avatar does not wait for the end of a sentence.</summary>
public sealed class SignChunker
{
    private const int MinWords = 3;
    private const int MaxWords = 6;

    private readonly Dictionary<string, int> _sent = [];

    public IReadOnlyList<SignPiece> Add(string key, string text, bool final)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        // The last word of an unfinished caption can still change.
        var end = final ? words.Length : words.Length - 1;
        var next = _sent.GetValueOrDefault(key);
        var pieces = new List<SignPiece>();

        while (next < end)
        {
            var take = PieceLength(words, next, end, final);
            if (take == 0) break;
            pieces.Add(new SignPiece(string.Join(' ', words, next, take), (double)next / words.Length));
            next += take;
        }

        if (final) _sent.Remove(key);
        else _sent[key] = next;
        return pieces;
    }

    private static int PieceLength(string[] words, int start, int end, bool final)
    {
        var available = end - start;
        for (var count = MinWords; count <= Math.Min(MaxWords, available); count++)
            if (EndsClause(words[start + count - 1])) return count;
        if (available >= MaxWords) return MaxWords;
        return final ? available : 0;
    }

    private static bool EndsClause(string word) => word[^1] is '.' or ',' or ';' or ':' or '?' or '!' or '…';
}

/// <summary>
/// Experimental. Signs one talk for everyone who turned the avatar on. It translates once on the server, so a hundred phones cost
/// the same as one, and nothing is translated while nobody is watching. What it signs is kept with the talk time it was said at,
/// so the recording can sign it too.
/// </summary>
public sealed class LiveSigner(SignTranslator translator, Func<bool> watched, Func<object, Task> send, ILogger log, CancellationToken stop,
    Action<long, SignTranslation>? keep = null)
{
    // Past this the piece goes word by word: late signs are worth less than rough ones.
    private static readonly TimeSpan ModelLimit = TimeSpan.FromMilliseconds(2500);

    private readonly object _gate = new();
    private readonly SignChunker _chunker = new();
    private Task _last = Task.CompletedTask;

    public void Add(string key, string text, bool final, long startMs = 0, long endMs = 0)
    {
        lock (_gate)
        {
            // Kept up to date while nobody watches, so turning it on does not sign what was said before.
            var pieces = _chunker.Add(key, text, final);
            if (!watched()) return;
            foreach (var piece in pieces) _last = SignAsync(piece.Text, startMs + (long)((endMs - startMs) * piece.Start), _last);
        }
    }

    /// <summary>Waits for what is on its way. For tests.</summary>
    public Task Drained { get { lock (_gate) return _last; } }

    // All pieces translate at once, because one at a time would fall behind speech. Each still waits its turn to be sent.
    private async Task SignAsync(string piece, long t, Task earlier)
    {
        SignTranslation? item = null;
        try
        {
            item = await translator.TranslateAsync(piece, stop, ModelLimit);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            log.LogWarning("Signing a piece failed: {Type}", e.GetType().Name);
        }

        await earlier;
        if (item == null) return;
        try
        {
            keep?.Invoke(t, item);
            await send(item);
        }
        catch (Exception e)
        {
            log.LogDebug("Sending signs skipped: {Type}", e.GetType().Name);
        }
    }
}
