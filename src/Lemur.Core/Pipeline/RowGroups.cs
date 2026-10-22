using System.Globalization;

namespace Lemur.Pipeline;

/// <summary>The stretch of time a heading in the Recordings window covers, newest first.</summary>
public enum GroupKind { Today, Yesterday, ThisWeek, LastWeek, ThisMonth, Month, Undated }

/// <summary>
/// A heading in the Recordings window's list and the meetings under it. <see cref="Key"/> names the group and the days
/// it covers ("today:2026-10-15", "month:2026-09"), so a heading that comes to mean other days (the window left open
/// overnight) starts as a new group would, not as the old one was left.
/// </summary>
public sealed record RowGroup(string Key, GroupKind Kind, string Title, IReadOnlyList<RecordingRow> Rows)
{
    public double Seconds => Rows.Sum(r => r.Seconds);

    /// <summary>Open when the window opens: the last two days. Older groups wait to be opened.</summary>
    public bool OpenAtFirst => Kind is GroupKind.Today or GroupKind.Yesterday;

    /// <summary>"3 meetings · 2 h 10 min" — how many, and how long they were altogether.</summary>
    public string Summary
    {
        get
        {
            var count = Rows.Count == 1 ? "1 meeting" : $"{Rows.Count} meetings";
            return RowGroups.Hours(Seconds) is { } time ? $"{count} · {time}" : count;
        }
    }
}

/// <summary>
/// Puts the Recordings window's rows under headings, the way a mail program does: Today, Yesterday, Earlier this week,
/// Last week, the rest of this month, then one heading per month. Weeks start on the day the user's Windows settings
/// say (Sunday in the US, Monday in much of Europe). Rows keep their order inside a group.
/// </summary>
public static class RowGroups
{
    public static List<RowGroup> Of(IEnumerable<RecordingRow> rows, DateTime today, DayOfWeek? firstDay = null)
    {
        today = today.Date;
        var weekStart = today.AddDays(-(((int)today.DayOfWeek - (int)(firstDay ?? FirstDay) + 7) % 7));
        var lastWeekStart = weekStart.AddDays(-7);
        var monthStart = new DateTime(today.Year, today.Month, 1);

        var groups = new List<(string Key, GroupKind Kind, string Title, List<RecordingRow> Rows)>();
        foreach (var row in rows)
        {
            var (key, kind, title) = Place(row.StartedAt?.ToLocalTime().Date);
            var group = groups.FindIndex(g => g.Key == key);
            if (group < 0)
            {
                groups.Add((key, kind, title, []));
                group = groups.Count - 1;
            }
            groups[group].Rows.Add(row);
        }
        return [.. groups.OrderBy(g => g.Kind).ThenByDescending(g => g.Key, StringComparer.Ordinal)
            .Select(g => new RowGroup(g.Key, g.Kind, g.Title, g.Rows))];

        (string Key, GroupKind Kind, string Title) Place(DateTime? day)
        {
            if (day is not { } d) return ("undated", GroupKind.Undated, "No date");
            if (d >= today) return ($"today:{Day(today)}", GroupKind.Today, "Today"); // a clock set ahead counts as today
            if (d == today.AddDays(-1)) return ($"yesterday:{Day(today)}", GroupKind.Yesterday, "Yesterday");
            if (d >= weekStart) return ($"week:{Day(weekStart)}", GroupKind.ThisWeek, "Earlier this week");
            if (d >= lastWeekStart) return ($"week:{Day(lastWeekStart)}", GroupKind.LastWeek, "Last week");
            if (d >= monthStart) return ($"rest:{Day(monthStart)}", GroupKind.ThisMonth, $"Earlier in {monthStart:MMMM}");
            return ($"month:{d:yyyy-MM}", GroupKind.Month, d.ToString("MMMM yyyy", CultureInfo.CurrentCulture));
        }
    }

    static DayOfWeek FirstDay => CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;

    static string Day(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>A group's total length: "45 min", "2 h 10 min", "61 h"; null for no time at all.</summary>
    public static string? Hours(double seconds)
    {
        var minutes = (int)Math.Round(seconds / 60);
        if (seconds <= 0) return null;
        if (minutes < 1) return "under 1 min";
        if (minutes < 60) return $"{minutes} min";
        if (minutes < 600) return minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60:00} min";
        return $"{(int)Math.Round(seconds / 3600)} h";
    }
}
