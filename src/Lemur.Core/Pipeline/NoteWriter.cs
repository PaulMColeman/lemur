using System.Text;
using Lemur.Audio;
using Lemur.Logging;
using Lemur.Transcripts;

namespace Lemur.Pipeline;

/// <summary>
/// The durable result: one Markdown file per meeting in the user's notes folder, and beside it the same
/// note as a web page for people to read (<see cref="NoteHtml"/>) and the meeting's audio as an MP3 the
/// page plays. The MP3 is part of the note: it is kept until the user deletes it, unlike the recording's
/// working audio, which goes after settings.keepAudioDays — unless settings.noteMp3.deleteWithAudio sends it with
/// the working audio (<see cref="AudioCleanUp"/>). The meeting file (<see cref="MeetingFile"/>) goes beside them too,
/// written by whoever wrote the note.
/// </summary>
public static class NoteWriter
{
    /// <param name="people">Everyone named for sure, for the front matter (Obsidian can list meetings per person).</param>
    /// <param name="recording">The recording folder's name; lets the page offer "name speakers".</param>
    /// <param name="audioWav">The meeting's mix; saved beside the note as an MP3 if it exists and Windows can encode it.</param>
    /// <returns>The Markdown note's path; the page is at <see cref="HtmlPath"/> of it, the audio at <see cref="AudioPath"/>.</returns>
    public static string Write(string notesFolder, RecordingMeta meta, string summaryMarkdown,
        IReadOnlyList<LabelledSegment> segments, string backendKind, string? webUrl,
        IReadOnlyList<string>? people = null, string? recording = null, string? audioWav = null)
    {
        Directory.CreateDirectory(notesFolder);
        var stem = Path.GetFileNameWithoutExtension(FileName(meta.StartedAt, meta.Title));
        var path = Path.Combine(notesFolder, stem + ".md");
        for (int i = 2; File.Exists(path) || File.Exists(HtmlPath(path)) || File.Exists(AudioPath(path)) || File.Exists(MeetingFile.PathFor(path)); i++)
            path = Path.Combine(notesFolder, $"{stem} ({i}).md");

        string? audio = null;
        if (audioWav is not null && File.Exists(audioWav))
        {
            try
            {
                Mp3Export.Encode(audioWav, AudioPath(path));
                audio = AudioPath(path);
            }
            catch (Exception ex)
            {
                Log.Warn($"{Path.GetFileName(path)}: written without its audio, which could not be saved as MP3: {ex.Message}");
            }
        }

        try
        {
            // Render both before writing either, so a rendering failure leaves no half set behind.
            var markdown = Render(meta, summaryMarkdown, segments, backendKind, webUrl, people, recording, Name(audio));
            var html = NoteHtml.Render(meta, summaryMarkdown, segments, backendKind, webUrl, people, recording, Name(audio));
            File.WriteAllText(path, markdown, new UTF8Encoding(false));
            File.WriteAllText(HtmlPath(path), html, new UTF8Encoding(false));
        }
        catch
        {
            if (audio is not null) File.Delete(audio);
            throw;
        }
        return path;
    }

    public static string HtmlPath(string notePath) => Path.ChangeExtension(notePath, ".html");

    public static string AudioPath(string notePath) => Path.ChangeExtension(notePath, ".mp3");

    /// <summary>The note's MP3 as the note and page refer to it (a name beside them), or null if there isn't one.</summary>
    static string? ExistingAudio(string notePath) => File.Exists(AudioPath(notePath)) ? Name(AudioPath(notePath)) : null;

    static string? Name(string? path) => path is null ? null : Path.GetFileName(path);

    /// <summary>
    /// Replaces an existing note pair in place (names given or words fixed after it was written, and it wasn't edited).
    /// Both files or neither: each is written beside and swapped in, and if the page can't be, the note goes back as it was.
    /// </summary>
    public static void Rewrite(string notePath, RecordingMeta meta, string summaryMarkdown, IReadOnlyList<LabelledSegment> segments,
        string backendKind, string? webUrl, IReadOnlyList<string>? people, string? recording)
    {
        var audio = ExistingAudio(notePath);
        var markdown = Render(meta, summaryMarkdown, segments, backendKind, webUrl, people, recording, audio);
        var html = NoteHtml.Render(meta, summaryMarkdown, segments, backendKind, webUrl, people, recording, audio);
        var before = File.ReadAllText(notePath);
        Swap(notePath, markdown);
        try
        {
            Swap(HtmlPath(notePath), html);
        }
        catch
        {
            try { Swap(notePath, before); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Writes beside the file, then moves it over: a failure leaves the file as it was.</summary>
    static void Swap(string path, string text)
    {
        var tmp = path + ".lemur-tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        try
        {
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Only the page: the .md was edited by the user, so it is changed by label swaps instead.</summary>
    public static void RewriteHtml(string notePath, RecordingMeta meta, string summaryMarkdown, IReadOnlyList<LabelledSegment> segments,
        string backendKind, string? webUrl, IReadOnlyList<string>? people, string? recording) =>
        Swap(HtmlPath(notePath), NoteHtml.Render(meta, summaryMarkdown, segments, backendKind, webUrl, people, recording, ExistingAudio(notePath)));

    /// <summary>
    /// After the clean-up deleted the note's MP3 (<see cref="AudioCleanUp"/>): the front matter's "audio:" line and the
    /// " · [recording]" link go, as Lemur wrote them, from a note the user edited too, since what they point at is gone.
    /// What's left is what <see cref="Render"/> writes without audio. Written beside and swapped in.
    /// </summary>
    /// <param name="audio">The MP3's file name, as the note names it.</param>
    /// <returns>Whether the note changed.</returns>
    public static bool DropAudio(string notePath, string audio)
    {
        var text = File.ReadAllText(notePath);
        var changed = FrontMatter.Get(text, "audio") == audio ? FrontMatter.Set(text, "audio", null) : text;
        changed = changed.Replace($" · [recording](<{audio}>)", "");
        if (changed == text) return false;
        Swap(notePath, changed);
        return true;
    }

    public static string Hash(string path) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    public static string FileName(DateTimeOffset startedAt, string? title)
    {
        // "Design review: onboarding" reads as "Design review - onboarding"; other characters a file name can't hold become "-".
        var name = $"{startedAt.ToLocalTime():yyyy-MM-dd HHmm} {(string.IsNullOrWhiteSpace(title) ? "Meeting" : title.Replace(": ", " - "))}";
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim().TrimEnd('.');
        if (safe.Length > 120) safe = safe[..120].TrimEnd();
        return safe + ".md";
    }

    /// <param name="audio">The MP3's file name, beside the note.</param>
    public static string Render(RecordingMeta meta, string summaryMarkdown, IReadOnlyList<LabelledSegment> segments,
        string backendKind, string? webUrl, IReadOnlyList<string>? people = null, string? recording = null, string? audio = null)
    {
        var local = meta.StartedAt.ToLocalTime();
        var title = string.IsNullOrWhiteSpace(meta.Title) ? "Meeting" : meta.Title;
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"title: {Yaml(title)}");
        if (!string.IsNullOrWhiteSpace(meta.Description)) sb.AppendLine($"description: {Yaml(meta.Description)}");
        sb.AppendLine($"date: {local:yyyy-MM-ddTHH:mm:sszzz}");
        sb.AppendLine($"duration: {TranscriptFormat.Clock(meta.DurationSeconds)}");
        sb.AppendLine($"source: {meta.Source}");
        sb.AppendLine($"backend: {backendKind}");
        if (webUrl is not null) sb.AppendLine($"link: {webUrl}");
        if (people is { Count: > 0 }) sb.AppendLine($"people: [{string.Join(", ", people.Select(Yaml))}]");
        if (recording is not null) sb.AppendLine($"recording: {recording}");
        if (audio is not null) sb.AppendLine($"audio: {Yaml(audio)}");
        sb.AppendLine("tags: [meeting]");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.Append($"{local:dddd d MMMM yyyy, HH:mm} · {TranscriptFormat.Clock(meta.DurationSeconds)} · {meta.Source}");
        if (webUrl is not null) sb.Append($" · [open in {backendKind}]({webUrl})");
        // <…> lets the link hold the spaces of a note's file name; < and > can't occur in one.
        sb.AppendLine(audio is null ? "" : $" · [recording](<{audio}>)");
        sb.AppendLine();
        sb.AppendLine(summaryMarkdown.Trim());
        sb.AppendLine();
        sb.AppendLine("## Transcript");
        sb.AppendLine();
        foreach (var turn in TranscriptFormat.MergeTurns(segments))
        {
            sb.AppendLine($"**[{TranscriptFormat.Clock(turn.Start)}] {turn.Speaker}:** {turn.Text}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    static string Yaml(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
