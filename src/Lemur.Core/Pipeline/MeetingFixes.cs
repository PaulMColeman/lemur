using System.Text;
using System.Text.RegularExpressions;
using Lemur.Speakers;
using Lemur.Transcripts;

namespace Lemur.Pipeline;

/// <summary>
/// Puts vocabulary corrections into meetings already made (Fix a word…, new words saved in Settings, llmmr vocab fix):
/// the transcript (labelled.json), the summary, names from introductions (said aloud, so heard by the same
/// transcription), the description and any title Lemur chose, then the note: written again if nobody edited it, or the
/// words swapped in place if someone did, so their edits stay; and the meeting file. Finished meetings only: one still
/// being made picks the vocabulary up as it goes.
/// </summary>
public static partial class MeetingFixes
{
    /// <param name="transcript">Whether the meeting file has the transcript (<see cref="Config.MeetingFileSettings.IncludeTranscript"/>).</param>
    /// <returns>How many words were fixed (0: nothing in it needed fixing).</returns>
    public static int Apply(RecordingFolder rec, Vocabulary vocabulary, string displayName, bool transcript = true)
    {
        int count = Fix(rec, vocabulary, displayName);
        if (count > 0) MeetingFile.TryWrite(rec, displayName, vocabulary, transcript: transcript); // the meeting file follows the note, and any new title
        return count;
    }

    static int Fix(RecordingFolder rec, Vocabulary vocabulary, string displayName)
    {
        if (!vocabulary.CanFix) return 0;
        using (RecordingFolder.Lock())
        {
            if (rec.LoadState().Stage != Stage.Done) return 0;
            int count = 0;

            var labelled = rec.Read<List<LabelledSegment>>(rec.LabelledFile);
            if (labelled is not null)
            {
                var fixedSegments = vocabulary.Fix(labelled, out var n);
                if (n > 0)
                {
                    rec.Write(rec.LabelledFile, fixedSegments);
                    labelled = [.. fixedSegments];
                    count += n;
                }
            }

            var summary = File.Exists(rec.SummaryFile) ? File.ReadAllText(rec.SummaryFile) : null;
            if (summary is not null)
            {
                var fixedSummary = vocabulary.Fix(summary, out var n);
                if (n > 0)
                {
                    File.WriteAllText(rec.SummaryFile, fixedSummary);
                    summary = fixedSummary;
                    count += n;
                }
            }

            var identified = rec.Read<IdentifiedResult>(rec.IdentifiedFile);
            if (identified is not null)
            {
                int n = 0;
                foreach (var s in identified.Speakers.Where(s => s.Status == SpeakerStatus.Introduced && s.Name is not null))
                {
                    s.Name = vocabulary.Fix(s.Name!, out var k);
                    n += k;
                }
                if (n > 0)
                {
                    rec.Write(rec.IdentifiedFile, identified);
                    count += n;
                }
            }

            var meta = rec.LoadMeta();
            int inMeta = 0;
            meta.Description = Fixed(vocabulary, meta.Description, ref inMeta);
            meta.SuggestedTitle = Fixed(vocabulary, meta.SuggestedTitle, ref inMeta);
            string? title = null;
            if (!meta.TitleByUser) // a title the user typed is theirs
            {
                var t = vocabulary.Fix(meta.Title, out var k);
                if (k > 0) title = t;
            }
            if (inMeta > 0)
            {
                rec.SaveMeta(meta);
                count += inMeta;
            }
            if (title is not null)
            {
                MeetingRename.Apply(rec, title, byUser: false); // the note's files take the fixed title too
                count++;
            }

            var state = rec.LoadState();
            if (state.NotePath is not { } note || !File.Exists(note)) return count;
            meta = rec.LoadMeta();
            var text = File.ReadAllText(note);
            var fixedText = FixNote(text, vocabulary, out var inNote);
            if (count == 0 && inNote == 0) return 0;
            bool untouched = state.NoteHash is not null && NoteWriter.Hash(note) == state.NoteHash;
            var named = labelled is null ? null : Naming.Apply(labelled, identified, displayName);
            var people = Naming.People(identified);
            if (untouched && named is not null && summary is not null)
            {
                NoteWriter.Rewrite(note, meta, summary, named, state.Backend ?? "?", state.WebUrl, people, rec.Name);
                state.NoteHash = NoteWriter.Hash(note);
                state.PageVersion = NoteHtml.PageVersion;
                rec.SaveState(state);
                return Math.Max(count, inNote);
            }

            // Edited by the user: the words swapped where they are, so their edits stay; the page is made again from the fixed data.
            if (inNote > 0) File.WriteAllText(note, fixedText, new UTF8Encoding(false));
            if (named is not null && summary is not null)
                NoteWriter.RewriteHtml(note, meta, summary, named, state.Backend ?? "?", state.WebUrl, people, rec.Name);
            return Math.Max(count, inNote);
        }
    }

    /// <summary>Whether a finished meeting has a misheard word anywhere <see cref="Apply"/> would fix it.</summary>
    public static bool Needs(RecordingFolder rec, Vocabulary vocabulary)
    {
        if (!vocabulary.CanFix) return false;
        try
        {
            var state = rec.LoadState();
            if (state.Stage != Stage.Done) return false;
            var meta = rec.LoadMeta();
            if ((!meta.TitleByUser && vocabulary.Mentions(meta.Title)) || vocabulary.Mentions(meta.Description)) return true;
            if (File.Exists(rec.SummaryFile) && vocabulary.Mentions(File.ReadAllText(rec.SummaryFile))) return true;
            if (rec.Read<List<LabelledSegment>>(rec.LabelledFile) is { } labelled && labelled.Any(s => vocabulary.Mentions(s.Text))) return true;
            if (state.NotePath is not { } note || !File.Exists(note)) return false;
            FixNote(File.ReadAllText(note), vocabulary, out var inNote);
            return inNote > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>
    /// A note with its misheard words fixed where they can be: the description in its front matter, and the body less its
    /// title line (it may be the user's own), the date line under it (it links the recording), every link's target and
    /// web address, and on a transcript line who spoke (only what they said). File names and links never change here.
    /// </summary>
    static string FixNote(string text, Vocabulary vocabulary, out int count)
    {
        count = 0;
        if (FrontMatter.Get(text, "description") is { } description)
        {
            var fixedDescription = vocabulary.Fix(description, out var k);
            if (k > 0)
            {
                text = FrontMatter.Set(text, "description", FrontMatter.Quote(fixedDescription));
                count += k;
            }
        }
        var lines = text.Split('\n'); // each keeps its '\r', if any
        int i = 0;
        if (lines.Length > 0 && lines[0].TrimEnd('\r') == "---")
        {
            int close = Array.FindIndex(lines, 1, l => l.TrimEnd('\r') == "---");
            i = close < 0 ? lines.Length : close + 1; // an unclosed front matter: leave it all
        }
        bool titleSeen = false, afterTitle = false;
        for (; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (!titleSeen && line.StartsWith("# "))
            {
                titleSeen = afterTitle = true;
                continue;
            }
            if (afterTitle && line.Length > 0)
            {
                afterTitle = false;
                if (line.Contains(" · ")) continue;
            }
            var said = TranscriptLine().Match(line);
            int from = said.Success ? said.Groups["said"].Index : 0;
            var fixedLine = line[..from] + OutsideLinks(line[from..], vocabulary, ref count);
            if (fixedLine != line) lines[i] = fixedLine + (lines[i].EndsWith('\r') ? "\r" : "");
        }
        return count == 0 ? text : string.Join('\n', lines);
    }

    static string OutsideLinks(string s, Vocabulary vocabulary, ref int count)
    {
        var sb = new StringBuilder();
        int last = 0;
        foreach (Match m in Link().Matches(s))
        {
            sb.Append(vocabulary.Fix(s[last..m.Index], out var k)).Append(m.Value);
            count += k;
            last = m.Index + m.Length;
        }
        sb.Append(vocabulary.Fix(s[last..], out var rest));
        count += rest;
        return sb.ToString();
    }

    // A Markdown link's target "](…)" or "](<…>)", an autolink "<https://…>", a bare web address.
    [GeneratedRegex(@"\]\((?:<[^>\r\n]*>|[^)\s]*)\)|<[a-zA-Z][a-zA-Z0-9+.-]*://[^>\s]*>|\b[a-zA-Z][a-zA-Z0-9+.-]*://\S+")]
    private static partial Regex Link();

    // "**[00:12:31] Dana Whitfield:** what they said"
    [GeneratedRegex(@"^\*\*\[\d{2}:\d{2}:\d{2}\] [^\r\n]*?:\*\* (?<said>.*)$")]
    private static partial Regex TranscriptLine();

    /// <summary>
    /// Fixes every finished meeting under <paramref name="root"/> that needs it.
    /// </summary>
    /// <returns>How many meetings were fixed, and how many couldn't be (a note open elsewhere).</returns>
    public static (int Fixed, int Failed) ApplyAll(string root, Vocabulary vocabulary, string displayName, IEnumerable<RecordingFolder>? only = null,
        bool transcript = true)
    {
        int done = 0, failed = 0;
        foreach (var rec in only ?? RecordingFolder.All(root).Where(r => Needs(r, vocabulary)))
        {
            try
            {
                if (Apply(rec, vocabulary, displayName, transcript) > 0) done++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
            {
                failed++;
                Logging.Log.Warn($"{rec.Name}: couldn't fix its words ({ex.GetType().Name})");
            }
        }
        return (done, failed);
    }

    static string? Fixed(Vocabulary vocabulary, string? text, ref int count)
    {
        if (text is null) return null;
        var result = vocabulary.Fix(text, out var n);
        count += n;
        return result;
    }
}
