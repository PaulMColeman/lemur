using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Lemur.Logging;

namespace Lemur.Pipeline;

/// <summary>
/// Gives a meeting a new title: the Recordings window's Rename…, `llmmr rename`, or the title its summary suggested
/// for a meeting the call app didn't name. meta.json takes the title; the note's files (.md, .html, .mp3) move to the new
/// name (" (2)" if it's taken) and the title inside the note and its page changes. Only the title lines, and the page's
/// link to its MP3, are changed, so a note the user edited keeps their edits. It happens whole or not at all: if a file
/// can't be moved or written, or the state can't be saved, everything done so far is undone. The meeting file follows
/// afterwards, if it can (<see cref="MoveMeetingFile"/>), and is only moved here: its content says the old title until it
/// is written again, by the caller when it can (<see cref="MeetingFile.TryWrite"/>) or else by the queue's pass, which
/// sees that meta.json and the note's path changed.
/// </summary>
public static partial class MeetingRename
{
    /// <param name="byUser">The user chose this title, so no suggested title will replace it later.</param>
    /// <returns>The note's path now (null if there is no note yet: the title is used when it is written).</returns>
    /// <exception cref="IOException">The note's files couldn't be renamed (one open elsewhere, read-only); nothing changed.</exception>
    public static string? Apply(RecordingFolder rec, string title, bool byUser = true)
    {
        title = MeetingAbout.TypedTitle(title) ?? throw new ArgumentException("A meeting needs a title.");
        using (RecordingFolder.Lock())
        {
            var meta = rec.LoadMeta();
            var state = rec.LoadState();
            var (was, wasByUser, wasFromFile) = (meta.Title, meta.TitleByUser, meta.TitleFromFile);
            meta.Title = title;
            meta.TitleByUser |= byUser;
            meta.TitleFromFile = false; // a title given or suggested is no longer the imported file's name
            if (state.NotePath is not { } md || !File.Exists(md))
            {
                rec.SaveMeta(meta);
                return state.NotePath;
            }

            // The plan, in memory: nothing is touched unless all of it could be read.
            var newMd = FreeName(Path.GetDirectoryName(md)!, Path.GetFileNameWithoutExtension(NoteWriter.FileName(meta.StartedAt, title)), md);
            string html = NoteWriter.HtmlPath(md), mp3 = NoteWriter.AudioPath(md), json = MeetingFile.PathFor(md);
            string newHtml = NoteWriter.HtmlPath(newMd), newMp3 = NoteWriter.AudioPath(newMd), newJson = MeetingFile.PathFor(newMd);
            bool untouched = state.NoteHash is not null && NoteWriter.Hash(md) == state.NoteHash;
            var text = File.ReadAllText(md);
            var page = File.Exists(html) ? File.ReadAllText(html) : null;
            var (newText, newPage) = Retitled(text, page, was, title, File.Exists(mp3) ? (Path.GetFileName(mp3), Path.GetFileName(newMp3)) : null);

            var moved = new List<(string From, string To)>();
            var rewritten = new List<(string Path, string Before)>();
            try
            {
                foreach (var (from, to) in new[] { (md, newMd), (html, newHtml), (mp3, newMp3) })
                {
                    if (!File.Exists(from) || Identical(from, to)) continue;
                    File.Move(from, to);
                    moved.Add((from, to));
                }
                Replace(newMd, newText);
                rewritten.Add((newMd, text));
                if (newPage is not null)
                {
                    Replace(newHtml, newPage);
                    rewritten.Add((newHtml, page!));
                }
                state.NotePath = newMd;
                // A note Lemur wrote and nobody edited stays "untouched", so later changes may still rewrite it.
                if (untouched) state.NoteHash = NoteWriter.Hash(newMd);
                rec.SaveMeta(meta);
                rec.SaveState(state);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                for (int i = rewritten.Count - 1; i >= 0; i--) Undo(() => Replace(rewritten[i].Path, rewritten[i].Before));
                for (int i = moved.Count - 1; i >= 0; i--) Undo(() => File.Move(moved[i].To, moved[i].From));
                meta.Title = was;
                meta.TitleByUser = wasByUser;
                meta.TitleFromFile = wasFromFile;
                Undo(() => rec.SaveMeta(meta));
                throw new IOException("The note's files couldn't be renamed: one of them is open in another program, or read-only.", ex);
            }
            MoveMeetingFile(rec, json, newJson);
            return newMd;
        }
    }

    /// <summary>
    /// The meeting file after its note: moved, else deleted, else left under the old name. A sync tool or an indexer
    /// holding it mustn't stop a rename, and nothing is lost: it is written again under the new name, since its stamp
    /// has the note's path.
    /// </summary>
    static void MoveMeetingFile(RecordingFolder rec, string from, string to)
    {
        if (!File.Exists(from) || Identical(from, to)) return;
        string why;
        try
        {
            File.Move(from, to);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            why = ex.GetType().Name;
        }
        // Paths hold titles: the log says which recording only.
        try
        {
            File.Delete(from);
            Log.Info($"{rec.Name}: its meeting file couldn't be moved with the note ({why}), so it was deleted; it is written again under the new name");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"{rec.Name}: its meeting file couldn't be moved or deleted ({why}, {ex.GetType().Name}); left under the old name, " +
                     "and written again under the new one");
        }
    }

    /// <summary>
    /// The note and its page with the new title: front matter, the heading (if it still shows the title Lemur gave it;
    /// someone may have written their own), and the MP3's new name wherever they link to it.
    /// </summary>
    static (string Text, string? Page) Retitled(string text, string? page, string was, string title, (string Old, string New)? audio)
    {
        text = FrontMatter.Set(text, "title", FrontMatter.Quote(title));
        text = Heading(text, string.IsNullOrWhiteSpace(was) ? "Meeting" : was.Trim(), title);
        if (audio is { } a && a.Old != a.New)
        {
            if (FrontMatter.Get(text, "audio") is not null) text = FrontMatter.Set(text, "audio", FrontMatter.Quote(a.New));
            text = text.Replace($"[recording](<{a.Old}>)", $"[recording](<{a.New}>)");
        }
        if (page is not null)
        {
            var encoded = WebUtility.HtmlEncode(title);
            page = PageTitle().Replace(page, _ => $"<title>{encoded}</title>", 1);
            page = PageHeading().Replace(page, _ => $"<h1>{encoded}</h1>", 1);
            if (audio is { } b && b.Old != b.New) page = page.Replace(Uri.EscapeDataString(b.Old), Uri.EscapeDataString(b.New));
        }
        return (text, page);
    }

    /// <summary>Writes beside the file, then swaps it in: a failure leaves the file as it was.</summary>
    static void Replace(string path, string text)
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

    static void Undo(Action step)
    {
        try { step(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* left as it is: the best that can be done */ }
    }

    /// <summary>The first free "&lt;stem&gt;.md" (then " (2)", " (3)"…) whose page, MP3 and meeting file names are free too; the note's own files don't count.</summary>
    static string FreeName(string folder, string stem, string current)
    {
        for (int i = 1; ; i++)
        {
            var md = Path.Combine(folder, i == 1 ? stem + ".md" : $"{stem} ({i}).md");
            bool Taken(string path, string own) => File.Exists(path) && !SameFile(path, own);
            if (!Taken(md, current) && !Taken(NoteWriter.HtmlPath(md), NoteWriter.HtmlPath(current)) &&
                !Taken(NoteWriter.AudioPath(md), NoteWriter.AudioPath(current)) && !Taken(MeetingFile.PathFor(md), MeetingFile.PathFor(current)))
                return md;
        }
    }

    /// <summary>The same file (Windows names ignore case).</summary>
    static bool SameFile(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>The same name exactly: nothing to move (a change of capitals is still a move).</summary>
    static bool Identical(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.Ordinal);

    /// <summary>The note's "# Title" line, if it still says the title Lemur gave it.</summary>
    static string Heading(string text, string was, string title)
    {
        int body = text.StartsWith("---") && text.IndexOf("\n---", 3, StringComparison.Ordinal) is var end and >= 0 ? end + 4 : 0;
        var m = FirstHeading().Match(text, body);
        if (!m.Success || m.Groups["t"].Value.Trim() != was) return text;
        return text[..m.Groups["t"].Index] + title + text[(m.Groups["t"].Index + m.Groups["t"].Length)..];
    }

    [GeneratedRegex(@"(?m)^# (?<t>[^\r\n]*)")]
    private static partial Regex FirstHeading();

    [GeneratedRegex(@"<title>[^<]*</title>")]
    private static partial Regex PageTitle();

    [GeneratedRegex(@"<h1>[^<]*</h1>")]
    private static partial Regex PageHeading();
}
