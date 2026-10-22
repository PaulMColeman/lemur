using Lemur.Logging;
using Lemur.Speakers;
using Lemur.Transcripts;

namespace Lemur.Pipeline;

/// <summary>
/// Note pages made with an older layout than <see cref="NoteHtml.PageVersion"/> are made again from the meeting's own
/// files, so older meetings get what new ones have (the attendees list). Only the page: the Markdown note, which the user
/// may have edited, is never touched. A meeting without the files to make it from keeps the page it has.
/// </summary>
public static class NotePages
{
    /// <returns>Whether the page was made again.</returns>
    public static bool Refresh(RecordingFolder rec, string displayName)
    {
        try
        {
            if (!Behind(rec.LoadState())) return false;
        }
        catch (System.Text.Json.JsonException)
        {
            return false; // unreadable: the queue says so, once, rather than this at every pass
        }
        using (RecordingFolder.Lock())
        {
            var state = rec.LoadState();
            if (!Behind(state)) return false;
            bool can = Remake(rec, state, displayName);
            state.PageVersion = NoteHtml.PageVersion; // made again, or nothing to make it from: either way, not looked at again
            rec.SaveState(state);
            return can;
        }
    }

    /// <summary>
    /// The page made again from the meeting's files, as they are now (with its player only if the MP3 is beside it), under
    /// the caller's <see cref="RecordingFolder.Lock"/>. The caller records the page's version in the state.
    /// </summary>
    /// <returns>Whether it was; false when the note, its page, the transcript or the summary isn't there.</returns>
    public static bool Remake(RecordingFolder rec, ProcessingState state, string displayName)
    {
        var labelled = rec.Read<List<LabelledSegment>>(rec.LabelledFile);
        if (state.NotePath is not { } note || !File.Exists(note) || !File.Exists(NoteWriter.HtmlPath(note)) || labelled is null ||
            !File.Exists(rec.SummaryFile))
            return false;
        var identified = rec.Read<IdentifiedResult>(rec.IdentifiedFile);
        NoteWriter.RewriteHtml(note, rec.LoadMeta(), File.ReadAllText(rec.SummaryFile),
            Naming.Apply(labelled, identified, displayName), state.Backend ?? "?", state.WebUrl, Naming.People(identified), rec.Name);
        return true;
    }

    /// <summary>Up to <paramref name="max"/> pages brought up to date.</summary>
    public static int RefreshAll(string root, string displayName, int max = int.MaxValue)
    {
        int done = 0;
        foreach (var rec in RecordingFolder.All(root))
        {
            if (done >= max) break;
            try
            {
                if (Refresh(rec, displayName)) done++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
            {
                Log.Warn($"{rec.Name}: couldn't bring its note page up to date ({ex.GetType().Name})");
            }
        }
        return done;
    }

    static bool Behind(ProcessingState state) => state.Stage == Stage.Done && state.PageVersion < NoteHtml.PageVersion;
}
