using System.Text;
using System.Text.RegularExpressions;

namespace Lemur.Detection;

public static class MeetingTitle
{
    const string TeamsMarker = "| Microsoft Teams";

    /// <summary>
    /// Teams' own places, which it puts before what you are looking at: "Calendar | Meeting join | Weekly sync | Microsoft Teams"
    /// is the screen before joining Weekly sync. (English Teams; in other languages they stay in the title.)
    /// </summary>
    static readonly string[] TeamsPlaces = ["Calendar", "Chat", "Activity", "Teams", "Calls", "Files", "Apps", "Meet", "Meeting join"];

    /// <summary>
    /// "Weekly sync | Microsoft Teams and 11 more pages - Personal - Microsoft Edge" → "Weekly sync";
    /// "Calendar | Meeting join | Weekly sync | Microsoft Teams" → "Weekly sync".
    /// Returns null when the title carries nothing useful.
    /// </summary>
    public static string? FromWindowTitle(string? windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle)) return null;
        var clean = StripInvisible(windowTitle);
        int marker = clean.IndexOf(TeamsMarker, StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return null;
        var parts = Regex.Replace(clean[..marker], @"^\s*\(\d+\)", "") // "(2) Chat | …": unread count
            .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !TeamsPlaces.Contains(p, StringComparer.OrdinalIgnoreCase))
            .ToList();
        return parts.Count == 0 ? null : string.Join(" | ", parts);
    }

    /// <summary>
    /// Which meeting a call window shows, for noticing the app move on to another one: Teams' meeting name, or
    /// for other apps the whole title less the browser's changing "and 3 more pages". Null for no title.
    /// </summary>
    public static string? Key(string? windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle)) return null;
        return FromWindowTitle(windowTitle) ?? Regex.Replace(StripInvisible(windowTitle), @"\s+and \d+ more pages?\b", "").Trim();
    }

    /// <summary>Edge puts a zero-width space inside "Microsoft​ Edge"; titles can carry other format chars too.</summary>
    static string StripInvisible(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            var cat = char.GetUnicodeCategory(ch);
            if (cat is System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.Control) continue;
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
