using System.Text;
using System.Text.RegularExpressions;

namespace Lemur.Pipeline;

/// <summary>
/// What a meeting is about, in a line: a title for meetings the call app didn't name (Google Meet, GoTo, Zoom, a manual
/// recording), and one sentence for the Recordings window, so a meeting can be found without opening it. The summary
/// model writes both as the first two lines of its reply (<see cref="SummaryPrompt"/>); for a summary without them,
/// <see cref="MeetingDescriber"/> asks for them on their own.
/// </summary>
public static partial class MeetingAbout
{
    /// <summary>Longest title kept from the model (a file name holds the date and up to 120 characters).</summary>
    public const int MaxTitle = 80;

    /// <summary>Longest title a person can type.</summary>
    public const int MaxTypedTitle = 100;

    public const int MaxDescription = 300;

    /// <summary>Titles that say nothing about the meeting: what a call app shows when it has no meeting name.</summary>
    static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "meeting", "call", "untitled", "(untitled)", "untitled meeting", "new meeting", "meet now", "teams meeting",
        "microsoft teams meeting", "teams call", "zoom", "zoom meeting", "zoom workplace", "goto", "goto meeting",
        "gotomeeting", "google meet", "meet", "webex", "webex meeting", "recording", "in-person meeting",
    };

    /// <summary>True when a title says nothing about the meeting (none, "Meeting", Meet's code).</summary>
    public static bool IsUntitled(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return true;
        var t = title.Trim();
        return Generic.Contains(t) || MeetCode().IsMatch(t) || DatedMeeting().IsMatch(t);
    }

    /// <summary>The title its summary suggests should become the meeting's: it has none, and the user didn't choose that.</summary>
    public static bool NeedsTitle(RecordingMeta meta) => !meta.TitleByUser && (meta.TitleFromFile || IsUntitled(meta.Title));

    /// <summary>Whether a summary says enough to describe the meeting from (not empty, not just "None.").</summary>
    public static bool HasSubstance(string? summary) => FromSummary(summary) is { Length: >= 20 };

    /// <summary>What the Recordings window calls a meeting with no title yet: "Google Meet (Edge) call", "Manual recording".</summary>
    public static string Placeholder(string? source) => source switch
    {
        null or "" => "Untitled meeting",
        "Manual" => "Manual recording",
        "Recovered" => "Recovered recording",
        RecordingImport.Source => "Imported recording",
        "test" => "Test recording",
        _ => $"{source} call",
    };

    /// <summary>
    /// Takes the "Title:" and "About:" lines off the top of a summary reply (bold or bulleted too). Only the lines it
    /// starts with: the first line that is neither ends the search, so nothing inside the summary is ever taken. What
    /// is left is the summary as the note shows it.
    /// </summary>
    public static (string Summary, string? Title, string? Description) Split(string reply)
    {
        var lines = reply.Replace("\r\n", "\n").Split('\n').ToList();
        string? title = null, about = null;
        int i = 0;
        while (i < lines.Count)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                i++;
                continue;
            }
            var m = Field().Match(lines[i]);
            if (!m.Success) break; // the summary has begun
            if (m.Groups["key"].Value.Contains("title", StringComparison.OrdinalIgnoreCase)) title ??= ModelTitle(m.Groups["value"].Value);
            else about ??= ModelDescription(m.Groups["value"].Value);
            lines.RemoveAt(i);
        }
        return (string.Join("\n", lines).Trim(), title, about);
    }

    /// <summary>
    /// A title as the model wrote it, tidied: no quotes, bold or full stop. Null for nothing, an echoed placeholder
    /// ("&lt;3 to 8 words&gt;", "[Meeting title]"), a non-answer ("None", "N/A", "No meeting notes provided") or a title that
    /// says nothing ("Meeting").
    /// </summary>
    public static string? ModelTitle(string? s)
    {
        var t = Tidy(s).Trim('"', '\'', '`', '*', '_', '“', '”', '‘', '’', ' ').TrimEnd('.', ' ');
        if (t.Length == 0 || Placeholder().IsMatch(t) || NonAnswer().IsMatch(t) || !t.Any(char.IsLetter) || IsUntitled(t)) return null;
        return Cut(t, MaxTitle, ellipsis: false);
    }

    /// <summary>A description as the model wrote it, tidied; null for nothing, an echoed placeholder or a non-answer.</summary>
    public static string? ModelDescription(string? s)
    {
        var t = Tidy(s).Trim('"', '`', '*', '_', '“', '”', ' ');
        // A short non-answer only: "No meeting next week; the launch moves to May." is a real description.
        if (t.Length == 0 || Placeholder().IsMatch(t) || (t.Length < 40 && NonAnswer().IsMatch(t.TrimEnd('.'))) || !t.Any(char.IsLetter))
            return null;
        return Cut(t, MaxDescription, ellipsis: true);
    }

    /// <summary>A title someone typed: spaces tidied, invisible characters dropped; null if nothing is left.</summary>
    public static string? TypedTitle(string? s)
    {
        var t = Tidy(s);
        return t.Length == 0 ? null : Cut(t, MaxTypedTitle, ellipsis: false);
    }

    /// <summary>
    /// For a summary without an "About:" line (written before descriptions, or by someone's own prompt): the first
    /// sentence or two of its Summary section.
    /// </summary>
    public static string? FromSummary(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return null;
        var lines = summary.Replace("\r\n", "\n").Split('\n');
        int heading = Array.FindIndex(lines, l => SummaryHeading().IsMatch(l));
        var body = new List<string>();
        for (int i = heading + 1; i < lines.Length; i++)
        {
            var l = lines[i].Trim();
            if (l.StartsWith('#'))
            {
                if (body.Count > 0 || heading >= 0) break;
                continue;
            }
            if (l.Length == 0)
            {
                if (body.Count > 0) break;
                continue;
            }
            body.Add(l.TrimStart('-', '*', ' '));
        }
        var text = Markup().Replace(Tidy(string.Join(" ", body)), "");
        if (text.Length == 0 || text.Equals("None.", StringComparison.OrdinalIgnoreCase)) return null;
        if (text.Length <= 220) return text;
        int end = -1;
        foreach (Match m in SentenceEnd().Matches(text))
        {
            if (m.Index + 1 > 220) break;
            end = m.Index + 1;
        }
        return end >= 40 ? text[..end] : Cut(text, 220, ellipsis: true);
    }

    /// <summary>One line: control and format characters dropped, runs of spaces made one.</summary>
    static string Tidy(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            var cat = char.GetUnicodeCategory(ch);
            if (cat is System.Globalization.UnicodeCategory.Format) continue;
            sb.Append(cat is System.Globalization.UnicodeCategory.Control ? ' ' : ch);
        }
        return Spaces().Replace(sb.ToString(), " ").Trim();
    }

    static string Cut(string s, int max, bool ellipsis)
    {
        if (s.Length <= max) return s;
        int space = s.LastIndexOf(' ', max - 1);
        var cut = (space > max / 2 ? s[..space] : s[..(max - 1)]).TrimEnd(' ', ',', ';', ':', '-');
        return ellipsis ? cut + "…" : cut;
    }

    // "Title: x", "**Title:** x", "- About: x", "# Title: x"; also "Meeting title".
    [GeneratedRegex(@"^\s*(?:#{1,6}\s*)?(?:[-*>]\s+)?(?:\*\*|__)?(?<key>meeting title|title|about)(?:\*\*|__)?\s*[:：]\s*(?:\*\*|__)?\s*(?<value>.*?)\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex Field();

    // The prompt's own wording echoed back: "<3 to 8 words>", "[Meeting title]", "{title}".
    [GeneratedRegex(@"^[<\[{].*[>\]}]$")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^(?:none|n/?a|null|undefined|unknown|tbd|tba|untitled|no title|not (?:available|applicable|provided|stated|known)|no (?:meeting|notes|summary|title|content|transcript|information)\b.*)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex NonAnswer();

    [GeneratedRegex(@"^\s*#{1,6}\s*summary\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SummaryHeading();

    // Google Meet's window: "Meet - abc-defg-hij".
    [GeneratedRegex(@"^(?:meet\s*[-–:]\s*)?[a-z]{3}-[a-z]{4}-[a-z]{3}$", RegexOptions.IgnoreCase)]
    private static partial Regex MeetCode();

    // What transcription jobs are called when there is no title: "Meeting 2026-10-14 09:18".
    [GeneratedRegex(@"^meeting\s+\d{4}-\d{2}-\d{2}(?:\s+\d{1,2}:\d{2})?$", RegexOptions.IgnoreCase)]
    private static partial Regex DatedMeeting();

    [GeneratedRegex(@"\*\*|__|`")]
    private static partial Regex Markup();

    [GeneratedRegex(@"[.!?](?=\s|$)")]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

/// <summary>The YAML block at the top of a Markdown note (between "---" lines): one key's line, read or changed.</summary>
public static class FrontMatter
{
    /// <summary>The value on "key: value", without its quotes; null if the note has no such line.</summary>
    public static string? Get(string text, string key)
    {
        if (Block(text) is not { } front) return null;
        var m = Regex.Match(front, $@"(?m)^{Regex.Escape(key)}:[ \t]*(?<v>[^\r\n]*)");
        if (!m.Success) return null;
        var v = m.Groups["v"].Value.Trim();
        return v.Length >= 2 && v[0] == '"' && v[^1] == '"' ? v[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\") : v;
    }

    /// <summary>
    /// Sets "key: value" (the value as it should appear, quotes and all); null removes the line. A new line goes
    /// before "tags:", or last. Text without front matter comes back unchanged.
    /// </summary>
    public static string Set(string text, string key, string? value)
    {
        if (Block(text) is not { } original) return text;
        var front = original;
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var line = value is null ? null : $"{key}: {value}";
        // The key's line and, for a block value (Obsidian writes lists that way), the indented or "- " lines under it.
        var m = Regex.Match(front, $@"(?m)^{Regex.Escape(key)}:[^\r\n]*(?:\r?\n(?:[ \t]+|- )[^\r\n]*)*");
        if (m.Success)
        {
            if (line is not null)
            {
                front = front[..m.Index] + line + front[(m.Index + m.Length)..];
            }
            else
            {
                int start = m.Index, stop = m.Index + m.Length;
                if (stop < front.Length && front[stop] == '\r') stop++;
                if (stop < front.Length && front[stop] == '\n') stop++;
                else if (start > 0 && front[start - 1] == '\n') start -= start > 1 && front[start - 2] == '\r' ? 2 : 1; // the last line
                front = front.Remove(start, stop - start);
            }
        }
        else if (line is not null)
        {
            int tags = front.IndexOf("\ntags:", StringComparison.Ordinal);
            front = tags >= 0 ? front[..(tags + 1)] + line + nl + front[(tags + 1)..] : front + nl + line;
        }
        return front + text[original.Length..];
    }

    /// <summary>Quoted for YAML: backslashes and quotes escaped.</summary>
    public static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>From the opening "---" to just before the line that closes it; null without front matter.</summary>
    static string? Block(string text)
    {
        if (!text.StartsWith("---")) return null;
        int end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        return end < 0 ? null : text[..end];
    }
}
