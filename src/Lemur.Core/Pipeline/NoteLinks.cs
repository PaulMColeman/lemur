using System.Globalization;
using System.Text.RegularExpressions;

namespace Lemur.Pipeline;

/// <summary>
/// lemur://note/&lt;recording&gt;[?t=&lt;seconds&gt;] — a link that opens a meeting's note page on a PC with Lemur, at a moment
/// of the meeting when it says one (docs/2026-10-15-meeting-export-design.md §3.6). The meeting file gives one per
/// meeting and one per action item, so a task board's card can lead back to where the task was agreed. Only this exact
/// shape is accepted, so a crafted link can't point anywhere else: the recording's name as
/// <see cref="RecordingFolder.ByName"/> takes it, and a whole number of seconds.
/// </summary>
public static partial class NoteLinks
{
    /// <summary>"lemur://note/" — the same scheme as the note pages' "Name speakers" links.</summary>
    public const string Prefix = NoteHtml.NameLinkScheme + "://note/";

    /// <summary>The longest moment a link may name (in seconds): about eleven days, far past any meeting.</summary>
    public const int MaxSeconds = 999_999;

    /// <summary>The link to a meeting's note, at <paramref name="seconds"/> into it when given (rounded down).</summary>
    public static string For(string recording, double? seconds = null)
    {
        if (!Name().IsMatch(recording)) throw new ArgumentException($"not a recording's name: {recording}", nameof(recording));
        var at = seconds is { } s && s >= 0 && !double.IsNaN(s) ? (int?)Math.Min(MaxSeconds, (int)Math.Floor(s)) : null;
        return Prefix + recording + (at is { } t ? "?t=" + t.ToString(CultureInfo.InvariantCulture) : "");
    }

    /// <summary>The recording and the moment a link names; false for anything that isn't exactly a note link.</summary>
    public static bool TryParse(string? link, out string recording, out int? seconds)
    {
        recording = "";
        seconds = null;
        if (link is null) return false;
        var m = Link().Match(link.Trim());
        if (!m.Success) return false;
        recording = m.Groups["rec"].Value;
        if (m.Groups["t"].Success) seconds = int.Parse(m.Groups["t"].Value, CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>
    /// What a link to <paramref name="recording"/> opens on this PC: its note page (.html), or the Markdown note when it
    /// has no page; null when the note isn't here. The note is where the recording's state says (a renamed note is
    /// followed there) or, on a PC without the recording — a second PC sharing the notes folder through OneDrive — the
    /// one in the notes folder whose front matter says <c>recording: &lt;name&gt;</c>, the original rather than a
    /// conflict copy OneDrive made beside it. Nothing outside the notes folder is looked at but the state's note.
    /// </summary>
    public static string? FindNote(string recordingsRoot, string notesFolder, string recording)
    {
        if (!Name().IsMatch(recording)) return null;
        string? note = null;
        if (RecordingFolder.ByName(recordingsRoot, recording) is { } rec)
        {
            try
            {
                note = rec.LoadState().NotePath;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                // unreadable state: look for the note by its front matter instead
            }
        }
        if (note is null || !IsMarkdown(note) || !File.Exists(note)) note = Search(notesFolder, recording);
        if (note is null) return null;
        var page = NoteWriter.HtmlPath(note);
        return File.Exists(page) ? page : note;
    }

    static bool IsMarkdown(string path) => Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The note in the folder (not below it) whose front matter names the recording. Notes whose file name starts with
    /// the recording's date and minute ("2026-10-14 0930 …", as Lemur names them) are read first; only if none says it
    /// are the others read, skipping those OneDrive keeps online only, which reading would download.
    /// </summary>
    static string? Search(string notesFolder, string recording)
    {
        List<FileInfo> files;
        try
        {
            files = Directory.Exists(notesFolder) ? new DirectoryInfo(notesFolder).EnumerateFiles("*.md").ToList() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
        var stem = $"{recording[..4]}-{recording[4..6]}-{recording[6..8]} {recording[9..13]}";
        bool Named(FileInfo f) => f.Name.StartsWith(stem, StringComparison.OrdinalIgnoreCase);
        var found = files.Where(Named).Where(f => Says(f.FullName, recording)).ToList();
        if (found.Count == 0)
            found = files.Where(f => !Named(f) && !OnlineOnly(f)).Where(f => Says(f.FullName, recording)).ToList();
        // OneDrive keeps both sides of a clash, naming its copy "<name>-<PC>.md": the original is the one the others
        // extend. Otherwise (renamed by hand, say) the newest.
        string Stem(FileInfo f) => Path.GetFileNameWithoutExtension(f.Name);
        return found
            .Where(f => !found.Any(o => o != f && Stem(f).StartsWith(Stem(o) + "-", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault()?.FullName;
    }

    // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS, which FileAttributes doesn't name: a cloud file not on this PC yet.
    static bool OnlineOnly(FileInfo f) => (f.Attributes & (FileAttributes.Offline | (FileAttributes)0x400000)) != 0;

    /// <summary>Whether the note's front matter (the "---" block that starts it) has <c>recording: &lt;name&gt;</c>.</summary>
    static bool Says(string note, string recording)
    {
        try
        {
            using var reader = new StreamReader(new FileStream(note, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
            if (reader.ReadLine()?.Trim() != "---") return false;
            for (int i = 0; i < 100 && reader.ReadLine() is { } line; i++)
            {
                if (line.Trim() == "---") return false;
                if (FrontMatterRecording().Match(line) is { Success: true } m) return m.Groups["rec"].Value == recording;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // locked or gone: not this one
        }
        return false;
    }

    // [0-9], not \d: \d takes other scripts' digits too, which int.Parse then refuses.
    [GeneratedRegex(@"^[0-9]{8}-[0-9]{6}(-[0-9]{1,3})?$")]
    private static partial Regex Name();

    // A browser may hand the link over with a trailing slash ("lemur://note/20261014-093000/?t=5").
    [GeneratedRegex(@"^lemur://note/(?<rec>[0-9]{8}-[0-9]{6}(-[0-9]{1,3})?)/?(\?t=(?<t>[0-9]{1,6}))?$", RegexOptions.IgnoreCase)]
    private static partial Regex Link();

    // As NoteWriter writes it, or quoted by an editor that rewrote the front matter (Obsidian's properties).
    [GeneratedRegex(@"^recording:\s*([""']?)(?<rec>[^""'\s]+)\1\s*$")]
    private static partial Regex FrontMatterRecording();
}
