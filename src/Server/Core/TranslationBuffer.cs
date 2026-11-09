namespace TalkLens.Core;

/// <summary>
/// Joins caption pieces so a sentence is translated whole. A piece translated alone loses its meaning, so pieces wait until
/// one ends a sentence, or until the first has waited for the hold time, whichever comes first (RNF-04).
/// </summary>
public sealed class TranslationBuffer(TimeSpan hold, Action<IReadOnlyList<Segment>> ready, CancellationToken stop)
{
    private readonly object _gate = new();
    private readonly List<Segment> _pending = [];
    private int _batch;

    public void Add(Segment segment)
    {
        IReadOnlyList<Segment>? batch = null;
        lock (_gate)
        {
            _pending.Add(segment);
            if (EndsSentence(segment.Text)) batch = Take();
            else if (_pending.Count == 1) _ = FlushAfterHoldAsync(_batch);
        }
        if (batch != null) ready(batch);
    }

    private async Task FlushAfterHoldAsync(int batchNumber)
    {
        try { await Task.Delay(hold, stop); }
        catch (OperationCanceledException) { return; }

        IReadOnlyList<Segment> batch;
        lock (_gate)
        {
            if (batchNumber != _batch) return;
            batch = Take();
        }
        ready(batch);
    }

    private List<Segment> Take()
    {
        var batch = _pending.ToList();
        _pending.Clear();
        _batch++;
        return batch;
    }

    private static bool EndsSentence(string text) => text.TrimEnd().LastOrDefault() is '.' or '?' or '!' or '…';
}
