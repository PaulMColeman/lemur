using System.Text;
using System.Text.RegularExpressions;
using Lemur.Transcripts;

namespace Lemur.Pipeline;

/// <summary>What the Recordings window's search box looks for: words that must all appear, in any order; "quoted words" stay together.</summary>
public sealed partial class SearchQuery
{
    SearchQuery(IReadOnlyList<string> terms) => Terms = terms;

    public IReadOnlyList<string> Terms { get; }

    public bool IsEmpty => Terms.Count == 0;

    public static readonly SearchQuery None = new([]);

    public static SearchQuery Parse(string? text)
    {
        var terms = new List<string>();
        foreach (Match m in Term().Matches(text ?? ""))
        {
            var t = Spaces().Replace((m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["w"].Value).Trim(), " ");
            if (t.Length > 0 && !terms.Contains(t, StringComparer.OrdinalIgnoreCase)) terms.Add(t);
        }
        return new SearchQuery(terms);
    }

    /// <summary>Where the first term (in query order) is found in <paramref name="text"/>, and its length; null if none is.</summary>
    public (int Index, int Length)? FirstIn(string text)
    {
        foreach (var t in Terms)
        {
            int i = text.IndexOf(t, StringComparison.OrdinalIgnoreCase);
            if (i >= 0) return (i, t.Length);
        }
        return null;
    }

    [GeneratedRegex("\"(?<q>[^\"]*)\"?|(?<w>[^\\s\"]+)")]
    private static partial Regex Term();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

/// <summary>The Recordings window's date filter.</summary>
public enum DateRange { AnyTime, Today, Yesterday, Last7Days, Last30Days, ThisMonth, LastMonth, ThisYear, Between }

public static class DateRanges
{
    public static readonly DateRange[] All = Enum.GetValues<DateRange>();

    public static string Name(DateRange r) => r switch
    {
        DateRange.AnyTime => "Any time",
        DateRange.Today => "Today",
        DateRange.Yesterday => "Yesterday",
        DateRange.Last7Days => "Last 7 days",
        DateRange.Last30Days => "Last 30 days",
        DateRange.ThisMonth => "This month",
        DateRange.LastMonth => "Last month",
        DateRange.ThisYear => "This year",
        _ => "Between dates…",
    };

    /// <summary>The days a range covers: from its first day to the day after its last (null: any time).</summary>
    /// <param name="from">For <see cref="DateRange.Between"/>: its first and last day, either way round.</param>
    public static (DateTime From, DateTime Until)? Days(DateRange r, DateTime today, DateTime? from = null, DateTime? to = null)
    {
        today = today.Date;
        var month = new DateTime(today.Year, today.Month, 1);
        return r switch
        {
            DateRange.Today => (today, today.AddDays(1)),
            DateRange.Yesterday => (today.AddDays(-1), today),
            DateRange.Last7Days => (today.AddDays(-6), today.AddDays(1)),
            DateRange.Last30Days => (today.AddDays(-29), today.AddDays(1)),
            DateRange.ThisMonth => (month, month.AddMonths(1)),
            DateRange.LastMonth => (month.AddMonths(-1), month),
            DateRange.ThisYear => (new DateTime(today.Year, 1, 1), new DateTime(today.Year + 1, 1, 1)),
            DateRange.Between when from is { } a && to is { } b =>
                a.Date <= b.Date ? (a.Date, b.Date.AddDays(1)) : (b.Date, a.Date.AddDays(1)),
            _ => null,
        };
    }

    /// <summary>Whether a meeting that started at <paramref name="at"/> (its local day) falls in the range.</summary>
    public static bool Contains((DateTime From, DateTime Until)? days, DateTimeOffset? at) =>
        days is not { } d || (at is { } t && t.ToLocalTime().DateTime >= d.From && t.ToLocalTime().DateTime < d.Until);
}

/// <summary>
/// Finds meetings by anything in them: the title, the description, the people, and the note as the user sees it
/// (summary and transcript, with names, and their own edits); for a meeting whose note is gone, its own summary and
/// transcript. Each meeting's text is read once and kept until its files change. Front matter, headings and the
/// date line are left out, so "meeting" or "Teams" doesn't find every note.
/// </summary>
public sealed partial class MeetingSearch
{
    readonly Dictionary<string, (DateTime Stamp, string Text)> _bodies = new(StringComparer.OrdinalIgnoreCase);

    public bool Matches(RecordingRow row, SearchQuery query)
    {
        if (query.IsEmpty) return true;
        var header = Header(row);
        string? body = null;
        foreach (var term in query.Terms)
        {
            if (header.Contains(term, StringComparison.OrdinalIgnoreCase)) continue;
            body ??= Body(row);
            if (!body.Contains(term, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>
    /// Up to <paramref name="max"/> lines of the meeting that hold a term, shortened around it: transcript lines keep
    /// their time and speaker ("[00:12:31] Dana: …the firewall rules…").
    /// </summary>
    public IReadOnlyList<string> Lines(RecordingRow row, SearchQuery query, int max = 6)
    {
        if (query.IsEmpty) return [];
        var found = new List<string>();
        foreach (var line in Body(row).Split('\n'))
        {
            if (query.FirstIn(line) is not { } hit) continue;
            found.Add(Snippet(line, hit.Index, hit.Length));
            if (found.Count == max) break;
        }
        return found;
    }

    /// <summary>Whether a misheard word is anywhere in the meeting (its title and description, or its note).</summary>
    public bool Mentions(RecordingRow row, Transcripts.Vocabulary vocabulary) =>
        vocabulary.Mentions(Header(row)) || vocabulary.Mentions(Body(row));

    /// <summary>How many lines of the meeting hold a term.</summary>
    public int Count(RecordingRow row, SearchQuery query) =>
        query.IsEmpty ? 0 : Body(row).Split('\n').Count(l => query.FirstIn(l) is not null);

    static string Header(RecordingRow r) => $"{r.Title}\n{r.Description}\n{string.Join("\n", r.People)}\n{r.Source}";

    string Body(RecordingRow row)
    {
        var rec = new RecordingFolder(row.Path);
        string[] sources = row.NotePath is { } note && File.Exists(note) ? [note] : [rec.SummaryFile, rec.LabelledFile];
        DateTime stamp;
        try { stamp = sources.Where(File.Exists).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty().Max(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
        if (_bodies.TryGetValue(row.Path, out var cached) && cached.Stamp == stamp) return cached.Text;
        string text;
        try
        {
            text = sources.Length == 1 ? FromNote(File.ReadAllText(sources[0])) : FromRecording(rec);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return ""; // being written: the next search reads it
        }
        _bodies[row.Path] = (stamp, text);
        return text;
    }

    /// <summary>
    /// A note's text without its front matter, headings and date line; one line per paragraph or list item (a
    /// paragraph wrapped over several lines is joined up), markup gone.
    /// </summary>
    static string FromNote(string note)
    {
        var lines = note.Replace("\r\n", "\n").Split('\n');
        int i = 0;
        if (lines.Length > 0 && lines[0] == "---")
        {
            int close = Array.IndexOf(lines, "---", 1);
            i = close < 0 ? 0 : close + 1;
        }
        var sb = new StringBuilder();
        var paragraph = new StringBuilder();
        void Flush()
        {
            if (paragraph.Length == 0) return;
            sb.Append(Plain(paragraph.ToString())).Append('\n');
            paragraph.Clear();
        }
        bool afterTitle = false;
        for (; i < lines.Length; i++)
        {
            var l = lines[i].Trim();
            if (l.Length == 0 || l.StartsWith('#') || l.Equals("None.", StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                if (l.StartsWith('#')) afterTitle = l.StartsWith("# ");
                continue;
            }
            if (afterTitle)
            {
                afterTitle = false;
                if (l.Contains(" · ")) continue; // "Wednesday 14 October 2026, 09:18 · 5:32 · Teams (Edge) · [recording](…)"
            }
            if (ListItem().IsMatch(l) || l.StartsWith("**[")) Flush();
            if (paragraph.Length > 0) paragraph.Append(' ');
            paragraph.Append(l);
        }
        Flush();
        return sb.ToString();
    }

    /// <summary>For a meeting without its note: the summary, then the transcript ("[00:12:31] Speaker B: …").</summary>
    static string FromRecording(RecordingFolder rec)
    {
        var sb = new StringBuilder();
        if (File.Exists(rec.SummaryFile)) sb.Append(FromNote(File.ReadAllText(rec.SummaryFile)));
        if (rec.Read<List<LabelledSegment>>(rec.LabelledFile) is { } segments)
            foreach (var turn in TranscriptFormat.MergeTurns(segments))
                sb.Append($"[{TranscriptFormat.Clock(turn.Start)}] {turn.Speaker}: {turn.Text}\n");
        return sb.ToString();
    }

    /// <summary>"**[00:12:31] Dana:** text" → "[00:12:31] Dana: text"; "- [ ] Dana - task" → "Dana - task".</summary>
    static string Plain(string line) => Markup().Replace(ListMark().Replace(line, ""), "");

    /// <summary>The line, or for a long one the part around the term; a transcript line keeps its time and speaker in front.</summary>
    static string Snippet(string line, int index, int length)
    {
        const int Width = 150;
        if (line.Length <= Width) return line;
        var lead = TranscriptLead().Match(line);
        int start = lead.Success ? lead.Length : 0;
        string prefix = lead.Success ? lead.Value : "";
        if (index < start) return line[..(Width - 1)].TrimEnd() + "…";
        int from = Math.Max(start, index - 50);
        if (from > start)
        {
            int space = line.IndexOf(' ', from);
            from = space >= 0 && space < index ? space + 1 : from;
        }
        int to = Math.Min(line.Length, Math.Max(index + length + 20, from + Width - prefix.Length));
        if (to < line.Length)
        {
            int space = line.LastIndexOf(' ', to - 1);
            to = space > index + length ? space : to;
        }
        var part = line[from..to].Trim();
        return prefix + (from > start ? "…" : "") + (to < line.Length ? part.TrimEnd('.', ',', ';', ':') + "…" : part);
    }

    [GeneratedRegex(@"^\s*(?:[-*+]\s+)?(?:\[[ xX]\]\s+)?")]
    private static partial Regex ListMark();

    [GeneratedRegex(@"^(?:[-*+]|\d+[.)])\s")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"\*\*|__|`")]
    private static partial Regex Markup();

    [GeneratedRegex(@"^\[\d{2}:\d{2}:\d{2}\] [^:]{1,60}: ")]
    private static partial Regex TranscriptLead();
}
