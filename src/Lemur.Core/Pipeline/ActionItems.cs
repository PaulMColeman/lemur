using System.Globalization;
using System.Text.RegularExpressions;

namespace Lemur.Pipeline;

/// <summary>One line of a summary's action items, as the meeting file gives it (docs/2026-10-15-meeting-export-design.md §3.2).</summary>
/// <param name="Text">The task, dashes and all ("review the Q3 - Q4 deck"); the whole line for one not in the prompt's form.</param>
/// <param name="Owner">Who committed to it, as the summary names them; null when the line doesn't say.</param>
/// <param name="OwnerIsMe">The owner is the user for sure (<see cref="ActionItems.IsMe"/>).</param>
/// <param name="Due">The day it is due, when the words pin one down.</param>
/// <param name="DueText">The due words as the summary wrote them ("Fri 2026-10-16", "next week"); null when it gave none.</param>
/// <param name="At">Seconds into the meeting where it was agreed, from the time ending the line (<see cref="ActionItems.TrailingTime"/>);
/// null without one, or past the end.</param>
/// <param name="Done">Ticked: "- [x]".</param>
public sealed record ActionItem(string Text, string? Owner, bool OwnerIsMe, DateOnly? Due, string? DueText, int? At, bool Done);

/// <param name="Found">The summary has an action items section in the prompt's form ("- [ ] owner - task"), so owners can be
/// trusted; false for a custom prompt without one, whose lines (if any) come as text-only items.</param>
public sealed record ActionItemList(bool Found, IReadOnlyList<ActionItem> Items);

/// <summary>A time ending a line (<see cref="ActionItems.TrailingTime"/>).</summary>
/// <param name="Seconds">Into the meeting.</param>
/// <param name="Index">Where its bracket opens: the line before it is <c>line[..Index]</c>.</param>
/// <param name="Clock">As written, without its brackets: "00:12:34", "12:34".</param>
public sealed record ItemTime(int Seconds, int Index, string Clock)
{
    /// <summary>Not past the meeting's end, which would be the model's mistake.</summary>
    public bool Within(double durationSeconds) => durationSeconds <= 0 || Seconds <= durationSeconds;
}

/// <summary>
/// Reads the summary's action items checklist, which the prompt asks for as "- [ ] owner - task - due [hh:mm:ss]"
/// (<see cref="SummaryPrompt"/>): the time comes off the end first, then the owner is what comes before the first " - ",
/// the due date what comes after the last one if all of it reads as a date, and the task everything between. A line
/// with one " - " and a date after it is a task and its date, the owner left out. Summaries written before the prompt
/// asked for dates and times read the same way, without them. No model call: pure text. Numbers are [0-9], not \d:
/// \d takes other scripts' digits too, which int.Parse then refuses.
/// </summary>
public static partial class ActionItems
{
    /// <param name="day">The meeting's day, which "Friday" and "end of the week" count from.</param>
    /// <param name="durationSeconds">The meeting's length: a time past its end is the model's mistake, and dropped.</param>
    /// <param name="me">The user's names: the display name, and what the meeting's transcript calls them.</param>
    /// <param name="others">Everyone else the transcript names ("Dana Whitfield", "Speaker B", "Sam?").</param>
    /// <param name="culture">Whose short date order "10/20" is read in; the current culture when null.</param>
    public static ActionItemList Read(string summary, DateOnly day, double durationSeconds, IReadOnlyCollection<string> me,
        IReadOnlyCollection<string> others, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (SummarySections.Lines(summary, "action items", "action item") is not { } lines) return new(false, []);
        var items = new List<ActionItem>();
        bool formed = false;
        int? margin = null; // how far in the section's bullets start: one further in is a note on the item above, not an item
        foreach (var raw in lines)
        {
            if (BulletIndent().Match(raw) is { Success: true } bullet)
            {
                int indent = bullet.Groups["in"].Value.Replace("\t", "    ").Length;
                if (indent > (margin ??= indent)) continue; // "  - with the appendix"
            }
            var check = Checkbox().Match(raw);
            var text = check.Success ? check.Groups["rest"].Value : SummarySections.Unbulleted(raw);
            if (SummarySections.IsNothing(text)) continue;
            var (rest, at) = TakeTime(text, durationSeconds);
            if (rest.Length == 0) continue;
            if (!check.Success)
            {
                items.Add(new(rest, null, false, null, null, at, false)); // a plain line: kept, but it says nothing of who
                continue;
            }
            var item = Item(rest, at, check.Groups["x"].Value != " ", day, me, others, culture);
            formed |= item.Owner is not null || item.Due is not null;
            items.Add(item);
        }
        // A section that says "None." is the prompt's own way of saying there were none: found, and empty.
        return new(formed || items.Count == 0, items);
    }

    static ActionItem Item(string rest, int? at, bool done, DateOnly day, IReadOnlyCollection<string> me,
        IReadOnlyCollection<string> others, CultureInfo culture)
    {
        var dashes = Separator().Matches(rest);
        if (dashes.Count == 0) return new(rest, null, false, null, null, at, done);
        var first = dashes[0];
        // "Book the venue - Fri 2026-10-16": a task and its date. Only a date, so "Paul - send it" keeps its owner.
        if (dashes.Count == 1 && ReadDue(rest[(first.Index + first.Length)..], day, culture) is (_, { } on, var said))
            return new(rest[..first.Index].Trim(), null, false, on, said, at, done);
        var owner = Owner(rest[..first.Index]);
        int from = first.Index + first.Length, to = rest.Length;
        DateOnly? due = null;
        string? dueText = null;
        if (dashes.Count > 1)
        {
            var last = dashes[^1];
            var (isDue, date, words) = ReadDue(rest[(last.Index + last.Length)..], day, culture);
            if (isDue) (to, due, dueText) = (last.Index, date, words);
        }
        var task = rest[from..to].Trim();
        if (task.Length == 0) task = rest.Trim(); // nothing between the dashes: the line is all there is
        return new(task, owner, IsMe(owner, me, others), due, dueText, at, done);
    }

    /// <summary>The line without its trailing time, and that time in seconds unless it is past the meeting's end.</summary>
    static (string Line, int? At) TakeTime(string text, double durationSeconds) =>
        TrailingTime(text) is { } time ? (Tidy(text[..time.Index]), time.Within(durationSeconds) ? time.Seconds : null) : (Tidy(text), null);

    /// <summary>
    /// The time ending a line, where an item was agreed: "[00:12:34]" as the prompt asks, and as models also write it —
    /// "[12:34]", in parentheses, a full stop after it, minutes past 59 without hours ("[75:30]"). Null if the line doesn't
    /// end in one. The meeting file's items (their <c>at</c>) and the note page's play links both read it here, so they agree.
    /// </summary>
    public static ItemTime? TrailingTime(string line)
    {
        var m = Time().Match(line);
        if (!m.Success) return null;
        int min = Num(m, "m");
        if (m.Groups["h"].Success && min >= 60) return null;
        return new((m.Groups["h"].Success ? Num(m, "h") * 3600 : 0) + min * 60 + Num(m, "s"), m.Groups["time"].Index, m.Groups["clock"].Value);
    }

    /// <summary>Spaces tidied, and a dash left hanging at the end ("Paul - send it - [00:01:02]") taken off.</summary>
    static string Tidy(string s) => HangingDash().Replace(Spaces().Replace(s, " ").Trim(), "");

    static string? Owner(string s)
    {
        var o = Spaces().Replace(s, " ").Trim().Trim('*', '_', '`', '"', '“', '”').Trim().TrimEnd(':').Trim();
        return o.Length == 0 ? null : o;
    }

    /// <summary>
    /// Whether an owner is the user (design §3.2): "me" or "I"; the same name, case and spacing ignored; or the same first
    /// name where one of the two is only that ("Paul" and "Paul Smith", either way round) and nobody else in the meeting has
    /// that first name. Nothing looser: "Paul's team", "Paul and Dana" and "Pauline" are someone else, and so is a name
    /// someone else in the meeting has too, which could be either of them.
    /// </summary>
    /// <param name="me">The user's names: the display name, and what the meeting's transcript calls them.</param>
    /// <param name="others">Everyone else the transcript names; a "?" on a guessed name doesn't count.</param>
    public static bool IsMe(string? owner, IReadOnlyCollection<string> me, IReadOnlyCollection<string> others)
    {
        if (owner is null) return false;
        var o = Words(owner);
        if (o.Length == 0) return false;
        if (o is ["me"] or ["i"] or ["myself"]) return true;
        var theirs = others.Select(n => Words(n.TrimEnd('?'))).Where(w => w.Length > 0).ToList();
        if (theirs.Any(w => w.SequenceEqual(o))) return false;
        foreach (var mine in me.Select(Words).Where(w => w.Length > 0))
        {
            if (mine.SequenceEqual(o)) return true;
            if (mine[0] != o[0] || (mine.Length > 1 && o.Length > 1)) continue;
            var longer = mine.Length > 1 ? mine : o;
            if (longer.Skip(1).All(NameWord) && !theirs.Any(w => w[0] == o[0])) return true;
        }
        return false;
    }

    static string[] Words(string name) =>
        name.Trim().Trim('*', '_', '`', '"').ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>A word of a person's name after their first ("Smith", "O'Brien", "J."), not "and" or "team" or a possessive.</summary>
    static bool NameWord(string w) => NamePart().IsMatch(w) && !w.EndsWith("'s") && !w.EndsWith("’s") && !NotNames.Contains(w);

    static readonly HashSet<string> NotNames = ["and", "or", "with", "plus", "team", "group", "et", "al", "etc", "others", "everyone", "all"];

    /// <summary>
    /// What comes after an item's last " - ": whether it reads as a due date at all (otherwise it is part of the task),
    /// the day it pins down, and the words to keep. "None" and the like are a due date that says there is none.
    /// </summary>
    static (bool IsDue, DateOnly? Date, string? Words) ReadDue(string tail, DateOnly day, CultureInfo culture)
    {
        var words = tail.Trim().Trim('*', '_', '`').Trim();
        if (NoDue().IsMatch(words)) return (true, null, null);
        if (DueDate(words, day, culture) is { } date) return (true, date, words);
        return Vague(words) ? (true, null, words) : (false, null, null);
    }

    /// <summary>
    /// The day due words pin down, counted from the meeting's day (design §3.2): a weekday is the next one after it (a week on
    /// if it is that day; "this Friday" said on a Friday is that day), "next Friday" the one in the following week (weeks start
    /// on the culture's first day), "tomorrow", "end of (the) week" that week's Friday and "end of next week" the one after,
    /// "end of (the) month", and dates written out: "Fri 2026-10-16", "2026-10-20", "Oct 20", "20 October", the 20th, or "10/20"
    /// in the culture's short date order. A date without a year is the first such day on or after the meeting. Vaguer words
    /// ("next week", "soon"), and a weekday that isn't the date's, are null.
    /// </summary>
    public static DateOnly? DueDate(string words, DateOnly day, CultureInfo culture)
    {
        var p = Plain(words);
        if (p.Length == 0) return null;
        if (Today().IsMatch(p)) return day;
        if (p == "tomorrow") return day.AddDays(1);
        if (EndOfWeek().Match(p) is { Success: true } week)
            return OnOrAfter(day, DayOfWeek.Friday).AddDays(week.Groups["next"].Success ? 7 : 0);
        if (EndOfMonth().Match(p) is { Success: true } month)
        {
            var first = new DateOnly(day.Year, day.Month, 1).AddMonths(month.Groups["next"].Success ? 2 : 1);
            return first.AddDays(-1);
        }
        if (WeekdayAlone().Match(p) is { Success: true } wd && Weekday(wd.Groups["wd"].Value) is { } dow)
        {
            var which = wd.Groups["which"].Value.Trim();
            if (which == "next")
            {
                var start = day.AddDays(-(((int)day.DayOfWeek - (int)culture.DateTimeFormat.FirstDayOfWeek + 7) % 7));
                return start.AddDays(7 + ((int)dow - (int)culture.DateTimeFormat.FirstDayOfWeek + 7) % 7);
            }
            return which == "this" ? OnOrAfter(day, dow) : OnOrAfter(day.AddDays(1), dow);
        }
        return Written(p, day, culture);
    }

    /// <summary>A date written out, after an optional weekday that must agree with it ("Fri 2026-10-16").</summary>
    static DateOnly? Written(string p, DateOnly day, CultureInfo culture)
    {
        DayOfWeek? said = null;
        if (WeekdayFirst().Match(p) is { Success: true } m && Weekday(m.Groups["wd"].Value) is { } dow)
        {
            said = dow;
            p = m.Groups["rest"].Value;
        }
        var date = Calendar(p, day, culture);
        return date is null || (said is not null && date.Value.DayOfWeek != said) ? null : date;
    }

    static DateOnly? Calendar(string p, DateOnly day, CultureInfo culture)
    {
        if (YearFirst().Match(p) is { Success: true } iso) return Make(Num(iso, "y"), Num(iso, "m"), Num(iso, "d"));
        if (MonthFirst().Match(p) is { Success: true } md && Month(md.Groups["mon"].Value) is { } m1)
            return md.Groups["y"].Success ? Make(Num(md, "y"), m1, Num(md, "d")) : Next(day, m1, Num(md, "d"));
        if (DayFirst().Match(p) is { Success: true } dm && Month(dm.Groups["mon"].Value) is { } m2)
            return dm.Groups["y"].Success ? Make(Num(dm, "y"), m2, Num(dm, "d")) : Next(day, m2, Num(dm, "d"));
        if (Numeric().Match(p) is { Success: true } n)
        {
            var pattern = culture.DateTimeFormat.ShortDatePattern;
            bool monthFirst = pattern.IndexOf('M') < pattern.IndexOf('d');
            int month = monthFirst ? Num(n, "a") : Num(n, "b"), dom = monthFirst ? Num(n, "b") : Num(n, "a");
            if (!n.Groups["y"].Success) return Next(day, month, dom);
            int y = Num(n, "y");
            return Make(y < 100 ? 2000 + y : y, month, dom);
        }
        if (Ordinal().Match(p) is { Success: true } o)
        {
            int dom = Num(o, "d");
            for (var month = new DateOnly(day.Year, day.Month, 1); month.Year <= day.Year + 1; month = month.AddMonths(1))
                if (Make(month.Year, month.Month, dom) is { } d && d >= day) return d;
        }
        return null;
    }

    static int Num(Match m, string group) => int.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture);

    static DateOnly? Make(int year, int month, int day) =>
        year is >= 1 and <= 9999 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month)
            ? new DateOnly(year, month, day) : null;

    /// <summary>The first <paramref name="month"/>/<paramref name="dom"/> on or after the meeting's day (Feb 29 waits for a leap year).</summary>
    static DateOnly? Next(DateOnly day, int month, int dom)
    {
        for (int y = day.Year; y <= day.Year + 8; y++)
            if (Make(y, month, dom) is { } d && d >= day) return d;
        return null;
    }

    static DateOnly OnOrAfter(DateOnly day, DayOfWeek dow) => day.AddDays(((int)dow - (int)day.DayOfWeek + 7) % 7);

    /// <summary>Lower case, markup and "by"/"due"/"before" in front and a time of day behind taken off: "by **Friday** at 3pm" → "friday".</summary>
    static string Plain(string words)
    {
        var s = Spaces().Replace(Markup().Replace(words.ToLowerInvariant(), " "), " ").Trim().TrimEnd('.', ',', ';', '!').Trim();
        for (string before = ""; before != s;)
        {
            before = s;
            s = Lead().Replace(s, "").Trim();
        }
        return TimeOfDay().Replace(s, "").Trim().TrimEnd(',').Trim();
    }

    /// <summary>Words about when, that don't pin a day down: "next week", "soon", "end of Q4", "after the release".</summary>
    static bool Vague(string words)
    {
        var p = Spaces().Replace(Markup().Replace(words.ToLowerInvariant(), " "), " ").Trim().TrimEnd('.', ',', ';', '!').Trim();
        if (Conditional().IsMatch(p)) return true;
        bool when = false;
        foreach (var w in p.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim(',', ';', ':', '!', '?')))
        {
            if (w.Length == 0) continue;
            if (WhenWords.Contains(w) || Weekday(w) is not null || Month(w.TrimEnd('.')) is not null || Number().IsMatch(w)) when = true;
            else if (!Glue.Contains(w)) return false;
        }
        return when;
    }

    static readonly HashSet<string> WhenWords =
    [
        "today", "tonight", "tomorrow", "morning", "afternoon", "evening", "noon", "midday", "eod", "eow", "eom", "cob", "asap",
        "soon", "later", "tbd", "tba", "day", "days", "week", "weeks", "weekend", "fortnight", "month", "months", "quarter",
        "quarters", "year", "years", "sprint", "meeting", "call", "q1", "q2", "q3", "q4", "h1", "h2", "one", "two", "three", "four",
        "five", "six", "seven", "eight", "nine", "ten", "couple", "few",
    ];

    static readonly HashSet<string> Glue =
    [
        "the", "this", "next", "following", "coming", "upcoming", "end", "start", "beginning", "middle", "mid", "of", "early",
        "late", "a", "an", "by", "on", "in", "within", "around", "at", "for", "from", "starting", "sometime", "some", "time",
        "as", "possible", "or", "and", "latest", "due", "every", "each",
    ];

    static DayOfWeek? Weekday(string w) => w.TrimEnd('.') switch
    {
        "monday" or "mon" => DayOfWeek.Monday,
        "tuesday" or "tue" or "tues" => DayOfWeek.Tuesday,
        "wednesday" or "wed" or "weds" => DayOfWeek.Wednesday,
        "thursday" or "thu" or "thur" or "thurs" => DayOfWeek.Thursday,
        "friday" or "fri" => DayOfWeek.Friday,
        "saturday" or "sat" => DayOfWeek.Saturday,
        "sunday" or "sun" => DayOfWeek.Sunday,
        _ => null,
    };

    static int? Month(string w)
    {
        if (w == "sept") return 9;
        var names = CultureInfo.InvariantCulture.DateTimeFormat;
        for (int i = 0; i < 12; i++)
            if (w == names.MonthNames[i].ToLowerInvariant() || w == names.AbbreviatedMonthNames[i].ToLowerInvariant()) return i + 1;
        return null;
    }

    // "- [ ] text", "* [x] text", "1. [ ] text".
    [GeneratedRegex(@"^\s*(?:[-*+]|[0-9]+[.)])\s+\[(?<x>[ xX])\]\s*(?<rest>.*)$")]
    private static partial Regex Checkbox();

    // A bullet, and the spaces or tabs before it: "  - with the appendix", "\t* [ ] …".
    [GeneratedRegex(@"^(?<in>[ \t]*)(?:[-*+]|[0-9]+[.)])(?:\s|$)")]
    private static partial Regex BulletIndent();

    // " - ", and the en and em dashes a model may use instead.
    [GeneratedRegex(@"\s+[-–—]\s+")]
    private static partial Regex Separator();

    // A trailing "[00:12:34]" (or "[12:34]", in brackets or parentheses).
    [GeneratedRegex(@"(?<time>[\[(]\s*(?<clock>(?:(?<h>[0-9]{1,2}):)?(?<m>[0-9]{1,2}):(?<s>[0-5][0-9]))\s*[\])])\s*[.,;]?\s*$")]
    private static partial Regex Time();

    [GeneratedRegex(@"\s+[-–—]$")]
    private static partial Regex HangingDash();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^\p{L}[\p{L}'’.\-]*$")]
    private static partial Regex NamePart();

    [GeneratedRegex(@"^(?:none|n/?a|-|–|—|unknown|unspecified|not (?:set|specified|given|stated|mentioned|said)|no (?:due )?(?:date|deadline)(?: (?:given|set|specified|stated|mentioned|said))?)\.?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex NoDue();

    [GeneratedRegex(@"\*\*|__|[`""“”()]|(?<!\w)_|_(?!\w)")]
    private static partial Regex Markup();

    [GeneratedRegex(@"^(?:due(?: by| on| date)?\s*:?|by|on|before|until|till|no later than|not later than)\s+")]
    private static partial Regex Lead();

    // "friday at 3pm", "friday 10:00", "friday eod", "tomorrow morning".
    [GeneratedRegex(@"\s+(?:at\s+)?(?:[0-9]{1,2}(?::[0-9]{2})?\s*(?:am|pm)|[0-9]{1,2}:[0-9]{2}|noon|midday|eod|cob|end of (?:the )?day|morning|afternoon|evening)$")]
    private static partial Regex TimeOfDay();

    [GeneratedRegex(@"^(?:today|tonight|eod|cob|end of (?:the )?day|end of today|close of business)$")]
    private static partial Regex Today();

    [GeneratedRegex(@"^(?:the )?end of (?:(?<next>next)|the|this)?\s*week$|^eow$")]
    private static partial Regex EndOfWeek();

    [GeneratedRegex(@"^(?:the )?end of (?:(?<next>next)|the|this)?\s*month$|^eom$|^month[- ]end$")]
    private static partial Regex EndOfMonth();

    [GeneratedRegex(@"^(?<which>this |next |coming |the coming |the next )?(?<wd>[a-z]+\.?)$")]
    private static partial Regex WeekdayAlone();

    [GeneratedRegex(@"^(?<wd>[a-z]+)\.?,?\s+(?<rest>.+)$")]
    private static partial Regex WeekdayFirst();

    [GeneratedRegex(@"^(?<y>[0-9]{4})[-/.](?<m>[0-9]{1,2})[-/.](?<d>[0-9]{1,2})$")]
    private static partial Regex YearFirst();

    // "oct 20", "october 20th, 2026", "oct. 20".
    [GeneratedRegex(@"^(?<mon>[a-z]+)\.?\s+(?<d>[0-9]{1,2})(?:st|nd|rd|th)?(?:,?\s+(?<y>[0-9]{4}))?$")]
    private static partial Regex MonthFirst();

    // "20 october", "the 20th of oct 2026".
    [GeneratedRegex(@"^(?:the\s+)?(?<d>[0-9]{1,2})(?:st|nd|rd|th)?\s+(?:of\s+)?(?<mon>[a-z]+)\.?,?(?:\s+(?<y>[0-9]{4}))?$")]
    private static partial Regex DayFirst();

    // "10/20", "20.10.2026", "10/20/26".
    [GeneratedRegex(@"^(?<a>[0-9]{1,2})[/.](?<b>[0-9]{1,2})(?:[/.](?<y>[0-9]{4}|[0-9]{2}))?$")]
    private static partial Regex Numeric();

    // "the 20th".
    [GeneratedRegex(@"^(?:the\s+)?(?<d>[0-9]{1,2})(?:st|nd|rd|th)$")]
    private static partial Regex Ordinal();

    // Words a date is counted from that aren't a date themselves: "after the release", "before the board meeting".
    [GeneratedRegex(@"^(?:before|after|until|till|ahead of|in time for|prior to|no later than|not later than|once)\s+\S")]
    private static partial Regex Conditional();

    [GeneratedRegex(@"^[0-9]{1,4}(?:st|nd|rd|th)?$|^[0-9]{1,4}(?:[/.-][0-9]{1,4}){1,2}$|^[0-9]{1,2}(?::[0-9]{2})?(?:am|pm)$|^[0-9]{1,2}:[0-9]{2}$")]
    private static partial Regex Number();
}

/// <summary>
/// A summary's sections by their headings, read the way <see cref="MeetingAbout"/> reads its lines: any heading level, any
/// case, a trailing colon, or a line that is only bold ("**Action items:**", which custom prompts get back).
/// </summary>
public static partial class SummarySections
{
    /// <summary>The lines under the first heading called one of <paramref name="names"/> (lower case), up to the next heading; null if there is none.</summary>
    public static IReadOnlyList<string>? Lines(string markdown, params string[] names)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        int start = Array.FindIndex(lines, l => Heading(l) is { } h && names.Contains(h));
        if (start < 0) return null;
        var body = new List<string>();
        for (int i = start + 1; i < lines.Length && Heading(lines[i]) is null; i++) body.Add(lines[i]);
        return body;
    }

    /// <summary>A section's text, trimmed; null if it has none.</summary>
    public static string? Text(string markdown, params string[] names) =>
        Lines(markdown, names) is { } lines && string.Join("\n", lines).Trim() is { Length: > 0 } text ? text : null;

    /// <summary>A section's bullets (or lines) without their markers, leaving out blank lines and "None.".</summary>
    public static List<string> Bullets(string markdown, params string[] names) =>
        (Lines(markdown, names) ?? []).Select(Unbulleted).Where(l => l.Length > 0 && !IsNothing(l)).ToList();

    /// <summary>The heading's words in lower case ("action items"); null for a line that isn't a heading.</summary>
    public static string? Heading(string line)
    {
        var m = HeadingLine().Match(line);
        return m.Success ? Spaces().Replace(m.Groups["h"].Value, " ").Trim().TrimEnd(':').Trim().ToLowerInvariant() : null;
    }

    /// <summary>A line without its "- ", "* " or "1. " marker, trimmed.</summary>
    public static string Unbulleted(string line) => Bullet().Replace(line, "").Trim();

    /// <summary>What the prompt says a section with nothing in it should say ("None."), and the like.</summary>
    public static bool IsNothing(string text) => Nothing().IsMatch(text.Trim().Trim('*', '_').Trim());

    [GeneratedRegex(@"^\s{0,3}(?:#{1,6}\s+(?<h>[^#\r\n]+?)\s*#*|(?:\*\*|__)(?<h>[^*_\r\n]+?)(?:\*\*|__)\s*:?)\s*$")]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"^\s*(?:[-*+]|[0-9]+[.)])\s+")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"^(?:none|n/?a|nothing(?: yet| to report)?|no (?:action items|decisions|open questions|items)\b.*)[.!]?$", RegexOptions.IgnoreCase)]
    private static partial Regex Nothing();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
