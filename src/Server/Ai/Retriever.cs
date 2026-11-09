using System.Text;
using TalkLens.Core;

namespace TalkLens.Ai;

public sealed record ContextItem(string Kind, long RefId, long T, string Text);

/// <summary>Finds the excerpts behind a question, crossing voice and slides by time.</summary>
public static class Retriever
{
    private const int MaxContextChars = 24000;
    private const int RecentSegments = 12;
    private const int MaxSegmentsPerSlide = 8;
    private const long SlideWindowFallbackMs = 120000;

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Spanish
        "que", "qué", "como", "cómo", "cuando", "cuándo", "donde", "dónde", "por", "para", "con", "sin", "una", "uno", "unos", "unas",
        "los", "las", "del", "sus", "ese", "esa", "eso", "este", "esta", "esto", "son", "fue", "era", "dijo", "explicó", "mostró",
        "habló", "hablo", "ponente", "charla", "sobre", "cual", "cuál", "quien", "quién", "hay", "más", "mas", "pero", "también",
        // English
        "the", "and", "for", "what", "when", "where", "who", "how", "why", "was", "were", "did", "does", "said", "say", "says", "explain",
        "explained", "show", "showed", "shown", "speaker", "talk", "about", "with", "this", "that", "they", "are", "you", "there", "which", "from",
    };

    /// <summary>FTS5 query from free text. Terms are quoted, so user input can never become query syntax.</summary>
    public static string BuildQuery(string question)
    {
        var terms = new List<string>();
        var word = new StringBuilder();
        foreach (var c in question + " ")
        {
            if (char.IsLetterOrDigit(c)) { word.Append(c); continue; }
            if (word.Length >= 3 && !StopWords.Contains(word.ToString())) terms.Add($"\"{word}\"*");
            word.Clear();
        }
        return string.Join(" OR ", terms.Distinct().Take(16));
    }

    /// <param name="withNeighbors">Adds the sentences around each hit. Good context for a model, noise when shown raw.</param>
    public static IReadOnlyList<ContextItem> Find(Store store, long talkId, string question, bool withNeighbors = true)
    {
        var hits = store.Search(talkId, BuildQuery(question), 12);
        if (hits.Count == 0) return [];

        var segments = store.GetSegments(talkId);
        var slides = store.GetSlides(talkId);
        var items = new Dictionary<(string, long), ContextItem>();

        foreach (var hit in hits)
        {
            if (hit.Kind == "segment")
            {
                var index = IndexOf(segments, hit.RefId);
                if (index < 0) continue;
                var reach = withNeighbors ? 1 : 0;
                for (var i = Math.Max(0, index - reach); i <= Math.Min(segments.Count - 1, index + reach); i++)
                    Add(items, FromSegment(segments[i]));
                continue;
            }

            // A slide matched: bring in what the speaker said while it was on screen.
            var slide = slides.FirstOrDefault(s => s.Id == hit.RefId);
            if (slide == null) continue;
            Add(items, FromSlide(slide));
            var next = slides.FirstOrDefault(s => s.T > slide.T)?.T ?? slide.T + SlideWindowFallbackMs;
            foreach (var segment in segments.Where(s => s.End >= slide.T && s.T < next).Take(MaxSegmentsPerSlide))
                Add(items, FromSegment(segment));
        }
        return Trim(items.Values.OrderBy(i => i.T).ToList(), MaxContextChars);
    }

    /// <summary>Adds what a search by meaning found to what the words found.</summary>
    public static IReadOnlyList<ContextItem> WithMeaning(IReadOnlyList<ContextItem> hits, IReadOnlyList<VectorHit> meaning, IReadOnlyList<Segment> segments, IReadOnlyList<Slide> slides)
    {
        var items = hits.ToDictionary(i => (i.Kind, i.RefId));
        foreach (var hit in meaning)
        {
            if (hit.Kind == "slide")
            {
                if (slides.FirstOrDefault(s => s.Id == hit.FirstId) is { } slide) Add(items, FromSlide(slide));
                continue;
            }
            foreach (var segment in segments.Where(s => hit.FirstId <= s.Id && s.Id <= hit.LastId)) Add(items, FromSegment(segment));
        }
        return items.Values.OrderBy(i => i.T).ToList();
    }

    public static IReadOnlyList<ContextItem> All(IReadOnlyList<Segment> segments, IReadOnlyList<Slide> slides) =>
        segments.Select(FromSegment).Concat(slides.Where(s => s.Text.Length > 0).Select(FromSlide)).OrderBy(i => i.T).ToList();

    /// <summary>Search hits plus the latest speech, so "what did they just say?" works on long talks.</summary>
    public static IReadOnlyList<ContextItem> WithRecent(IReadOnlyList<ContextItem> hits, IReadOnlyList<Segment> segments)
    {
        var items = hits.ToDictionary(i => (i.Kind, i.RefId));
        foreach (var segment in segments.TakeLast(RecentSegments)) Add(items, FromSegment(segment));
        return Trim(items.Values.OrderBy(i => i.T).ToList(), MaxContextChars);
    }

    /// <summary>Drops the oldest excerpts first when over budget.</summary>
    public static IReadOnlyList<ContextItem> Trim(IReadOnlyList<ContextItem> items, int maxChars)
    {
        var kept = new List<ContextItem>();
        var used = 0;
        foreach (var item in items.Reverse())
        {
            if (used + item.Text.Length > maxChars) break;
            used += item.Text.Length;
            kept.Add(item);
        }
        kept.Reverse();
        return kept;
    }

    private static int IndexOf(IReadOnlyList<Segment> segments, long id)
    {
        for (var i = 0; i < segments.Count; i++)
            if (segments[i].Id == id) return i;
        return -1;
    }

    private static void Add(Dictionary<(string, long), ContextItem> items, ContextItem item) => items[(item.Kind, item.RefId)] = item;
    private static ContextItem FromSegment(Segment s) => new("segment", s.Id, s.T, s.Text);
    private static ContextItem FromSlide(Slide s) => new("slide", s.Id, s.T, s.Text);
}
