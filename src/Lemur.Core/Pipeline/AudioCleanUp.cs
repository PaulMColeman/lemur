using System.Runtime.InteropServices;
using System.Text.Json;
using Lemur.Config;
using Lemur.Logging;

namespace Lemur.Pipeline;

/// <summary>
/// The queue's clean-up, after each pass: settings.keepAudioDays after a meeting's notes were made, its working audio (the
/// two tracks, the mix, its voiceprints) is deleted, and with settings.noteMp3.deleteWithAudio the MP3 beside its note
/// too — unless the user marked the meeting Keep recording (Paul, 2026-10-15: "Many meetings I dont need to keep the file
/// for … bypass that 14 day retention value for important meeting recordings"). The note and its transcript stay. The MP3
/// is the meeting's last audio, so it goes to the Recycle Bin; the note's .md and page then stop pointing at it, and the
/// meeting file says it has none at the queue's next pass (its stamp has whether the MP3 is there). What was done is
/// recorded in the state (<see cref="ProcessingState.AudioDeleted"/>, <see cref="ProcessingState.Mp3Deleted"/>), so
/// nothing is tried twice. Log lines name the recording only: the MP3's path is the meeting's title.
/// </summary>
public static class AudioCleanUp
{
    /// <summary>Its notes were made more than <paramref name="days"/> ago; with -1 (forever) never.</summary>
    public static bool PastDays(DateTimeOffset? completedAt, int days, DateTimeOffset now) =>
        days >= 0 && completedAt is { } done && done <= now.AddDays(-days);

    /// <param name="deleteMp3">Deletes an MP3, saying whether it went to the Recycle Bin: <see cref="Recycle"/>, unless a test
    /// gives another.</param>
    /// <returns>How many meetings lost their working audio, and how many their note's MP3.</returns>
    public static (int Audio, int Mp3s) Run(AppSettings settings, DateTimeOffset? now = null, Func<string, bool>? deleteMp3 = null)
    {
        if (settings.KeepAudioDays < 0) return (0, 0);
        var time = now ?? DateTimeOffset.Now;
        bool mp3s = settings.NoteMp3.DeleteWithAudio;
        int audio = 0, mp3 = 0;
        foreach (var rec in RecordingFolder.All(settings.ResolvedRecordingsFolder))
        {
            try
            {
                var state = rec.LoadState();
                if (state.Stage != Stage.Done || !PastDays(state.CompletedAt, settings.KeepAudioDays, time)) continue;
                if (state.AudioDeleted && (!mp3s || state.Mp3Deleted)) continue;
                // Read again under the lock; this keeps a kept meeting from taking it at every pass.
                if (rec.LoadMeta().KeepRecording) continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                continue; // mid-write, or unreadable (the queue says so, once): looked at again at the next pass
            }
            try
            {
                var (a, m) = CleanUp(rec, settings, mp3s, deleteMp3 ?? Recycle);
                if (a) audio++;
                if (m) mp3++;
            }
            catch (Exception ex)
            {
                Log.Warn($"{rec.Name}: could not delete old audio ({Log.TypeAndCode(ex)}); it is tried again at the next pass");
            }
        }
        return (audio, mp3);
    }

    /// <summary>One meeting past the days, under its lock: "Keep recording" may have been ticked a moment ago.</summary>
    static (bool Audio, bool Mp3) CleanUp(RecordingFolder rec, AppSettings settings, bool mp3s, Func<string, bool> deleteMp3)
    {
        using (RecordingFolder.Lock())
        {
            if (rec.LoadMeta().KeepRecording) return (false, false);
            var state = rec.LoadState();
            var after = $"after {Days(settings.KeepAudioDays)}";
            bool audio = false;
            if (!state.AudioDeleted)
            {
                rec.DeleteAudio();
                state.AudioDeleted = audio = true;
                rec.SaveState(state); // kept if the MP3 can't go now: only it is tried again
                Log.Info($"{rec.Name}: audio deleted {after}");
            }
            if (!mp3s || state.Mp3Deleted) return (audio, false);

            bool deleted = false;
            if (state.NotePath is { } note && NoteWriter.AudioPath(note) is var path && File.Exists(path) && Ours(rec, note))
            {
                bool recycled;
                try
                {
                    recycled = deleteMp3(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"{rec.Name}: could not delete its note's MP3 ({Log.TypeAndCode(ex)}); it is tried again at the next pass");
                    return (audio, false);
                }
                deleted = state.Mp3Deleted = true;
                rec.SaveState(state); // before the note is touched: whatever happens there, the MP3 is gone
                Log.Info($"{rec.Name}: its note's MP3 deleted {after} ({(recycled ? "to the Recycle Bin" : "for good: its path is too long for the Recycle Bin")})");
                Unlink(rec, state, note, Path.GetFileName(path), settings.DisplayName);
            }
            state.Mp3Deleted = true; // gone, or never there (no encoder, the note moved): nothing to look for again
            rec.SaveState(state);
            return (audio, deleted);
        }
    }

    /// <summary>
    /// The note at the meeting's NotePath is this meeting's: its front matter names the recording. A note deleted by hand
    /// frees its name for a later meeting's note, whose MP3 mustn't go with this one's clean-up. With no note there, the
    /// MP3 is this meeting's: a new note never takes a name an MP3 still holds (<see cref="NoteWriter.Write"/>).
    /// </summary>
    static bool Ours(RecordingFolder rec, string note) =>
        !File.Exists(note) || FrontMatter.Get(File.ReadAllText(note), "recording") == rec.Name;

    /// <summary>
    /// The note and its page stop pointing at the MP3 just deleted. An untouched note stays untouched (its hash is taken
    /// again), so later changes can still rewrite it. If the note or page can't be changed now (open elsewhere), the page's
    /// player says the recording isn't beside it. Nothing here may throw: the MP3 is gone either way.
    /// </summary>
    static void Unlink(RecordingFolder rec, ProcessingState state, string note, string mp3, string displayName)
    {
        try
        {
            if (File.Exists(note))
            {
                bool untouched = state.NoteHash is not null && NoteWriter.Hash(note) == state.NoteHash;
                if (NoteWriter.DropAudio(note, mp3) && untouched) state.NoteHash = NoteWriter.Hash(note);
            }
            if (NotePages.Remake(rec, state, displayName)) state.PageVersion = NoteHtml.PageVersion;
        }
        catch (Exception ex)
        {
            Log.Warn($"{rec.Name}: its note still points at the MP3 that was deleted ({Log.TypeAndCode(ex)})");
        }
    }

    /// <summary>
    /// Saving <paramref name="edited"/> over <paramref name="before"/> could delete MP3s that weren't going to go: MP3s turned
    /// on (or "forever" turned off while they're on), fewer days, or another recordings folder. Settings then counts them and
    /// asks first (<see cref="Mp3sToDelete"/>).
    /// </summary>
    /// <param name="before">The settings as saved; null for a first setup.</param>
    public static bool MoreMp3sGo(AppSettings? before, AppSettings edited) =>
        edited.NoteMp3.DeleteWithAudio && edited.KeepAudioDays >= 0 &&
        !(before is { NoteMp3.DeleteWithAudio: true, KeepAudioDays: >= 0 } && edited.KeepAudioDays >= before.KeepAudioDays &&
          SameFolder(before.ResolvedRecordingsFolder, edited.ResolvedRecordingsFolder));

    static bool SameFolder(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// The meetings whose note's MP3 the clean-up would delete now, were MP3s to go after <paramref name="days"/>: notes made
    /// longer ago than that, not kept, the MP3 still beside the note. Settings asks with this number before turning it on.
    /// </summary>
    public static int Mp3sToDelete(string root, int days, DateTimeOffset now)
    {
        int n = 0;
        foreach (var rec in RecordingFolder.All(root))
        {
            try
            {
                var state = rec.LoadState();
                if (state.Stage != Stage.Done || state.Mp3Deleted || !PastDays(state.CompletedAt, days, now)) continue;
                if (state.NotePath is not { } note || !File.Exists(NoteWriter.AudioPath(note)) || !Ours(rec, note)) continue;
                if (!rec.LoadMeta().KeepRecording) n++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                // Mid-write or unreadable: not counted, as the clean-up would skip it too.
            }
        }
        return n;
    }

    static string Days(int n) => n == 1 ? "1 day" : $"{n} days";

    /// <summary>
    /// Sends a file to the Recycle Bin, with no window: the queue's worker has none, and a file in use mustn't ask anybody
    /// anything. A path the shell can't take (260 characters or more) is deleted instead. Any other failure throws, so the
    /// file is tried again rather than lost for good. On a drive without a Recycle Bin (a network folder), or with it turned
    /// off, Windows itself deletes the file.
    /// </summary>
    /// <returns>Whether it went to the Recycle Bin (false: deleted, its path too long).</returns>
    /// <exception cref="IOException">Windows couldn't: open in another program, say.</exception>
    public static bool Recycle(string path)
    {
        var full = Path.GetFullPath(path);
        // The struct below is laid out for 64 bits (32-bit Windows packs it to 1); Lemur ships for x64.
        if (full.Length >= 260 || !Environment.Is64BitProcess)
        {
            File.Delete(full);
            return false;
        }
        var op = new FileOp { Func = FoDelete, From = full + "\0", Flags = FofSilent | FofNoConfirmation | FofAllowUndo | FofNoErrorUi };
        int code = SHFileOperation(ref op);
        if (code != 0 || op.AnyOperationsAborted || File.Exists(full))
            throw new IOException($"Windows couldn't move it to the Recycle Bin (0x{code:X})"); // no path: it is the meeting's title
        return true;
    }

    const uint FoDelete = 3;
    const ushort FofSilent = 0x4, FofNoConfirmation = 0x10, FofAllowUndo = 0x40, FofNoErrorUi = 0x400;

    /// <summary>SHFILEOPSTRUCTW. <see cref="From"/> is a list ending in two nulls: the marshaller adds the second.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct FileOp
    {
        public IntPtr Window;
        public uint Func;
        public string From;
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref FileOp op);
}
