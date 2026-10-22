using System.Text.Json;
using Lemur.Audio;
using Lemur.Config;
using Lemur.Logging;
using Lemur.Pipeline;
using Lemur.Speakers;
using Lemur.Transcripts;

namespace Lemur.Tests;

/// <summary>Log.Echo is one static: the tests that read it take turns.</summary>
[CollectionDefinition("Log echo")]
public class LogEchoCollection;

/// <summary>
/// The clean-up after settings.keepAudioDays: the working audio, the note's MP3 when Settings says so, and "Keep recording"
/// (Paul, 2026-10-15), which keeps both.
/// </summary>
[Collection("Log echo")]
public class CleanUpTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("llmmr-cleanup").FullName;

    /// <summary>Wednesday 2026-10-14 09:30 on this PC's clock.</summary>
    static readonly DateTimeOffset Start = new(new DateTime(2026, 10, 14, 9, 30, 0, DateTimeKind.Local));

    /// <summary>Fifteen days after the notes were made: past the default 14.</summary>
    static DateTimeOffset Later => DateTimeOffset.Now.AddDays(15);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    AppSettings Settings(bool mp3s = true) => new()
    {
        DisplayName = "Paul",
        RecordingsFolder = Path.Combine(_root, "rec"),
        NotesFolder = Path.Combine(_root, "notes"),
        Backend = { Type = BackendTypes.Scriberr, Scriberr = { BaseUrl = "https://x" } },
        Speakers = { PeopleFolder = Path.Combine(_root, "people") },
        NoteMp3 = { DeleteWithAudio = mp3s },
    };

    /// <summary>Deletes the MP3 for good: a test never sends anything to this PC's Recycle Bin.</summary>
    readonly List<string> _deleted = [];

    bool Delete(string path)
    {
        File.Delete(path);
        _deleted.Add(path);
        return true;
    }

    /// <summary>A finished meeting, with its note, page, MP3 and meeting file: you on the mic, then someone on the call.</summary>
    async Task<(RecordingFolder Rec, string Note)> Finished(AppSettings s, string title = "Budget review", DateTimeOffset? started = null)
    {
        var at = started ?? Start;
        var rec = RecordingFolder.CreateNew(s.ResolvedRecordingsFolder, at);
        int rate = AnchoredTrackWriter.SampleRate;
        var mic = new float[2 * rate];
        var others = new float[2 * rate];
        for (int i = 0; i < rate; i++) mic[i] = 0.2f * MathF.Sin(i * 0.2f);
        for (int i = rate; i < 2 * rate; i++) others[i] = 0.2f * MathF.Sin(i * 0.3f);
        for (int i = 0; i < 2 * rate; i++) mic[i] += 0.0005f * MathF.Sin(i * 1.7f);
        using (var a = new WavSampleSink(rec.MicWav)) a.Write(mic);
        using (var b = new WavSampleSink(rec.OthersWav)) b.Write(others);
        rec.SaveMeta(new RecordingMeta { Title = title, TitleByUser = true, Source = "Teams (Edge)", StartedAt = at, EndedAt = at.AddSeconds(2), DurationSeconds = 2 });
        rec.SaveState(new ProcessingState());
        var state = await new MeetingProcessor(s, () => new FakeBackend()).ProcessAsync(rec, CancellationToken.None);
        Assert.Equal(Stage.Done, state.Stage);
        Assert.True(File.Exists(NoteWriter.AudioPath(state.NotePath!)));
        return (rec, state.NotePath!);
    }

    static MeetingFile.Content Load(string note) =>
        JsonSerializer.Deserialize<MeetingFile.Content>(File.ReadAllText(MeetingFile.PathFor(note)), Json.Options)!;

    [Fact]
    public async Task A_meeting_marked_keep_recording_keeps_its_audio_and_mp3_and_the_rest_lose_both()
    {
        var s = Settings();
        var (kept, keptNote) = await Finished(s, "Budget review");
        var (other, otherNote) = await Finished(s, "Standup", Start.AddHours(1));
        kept.UpdateMeta(m => m.KeepRecording = true);

        Assert.Equal((1, 1), AudioCleanUp.Run(s, Later, Delete));

        Assert.True(File.Exists(kept.MicWav) && File.Exists(kept.MixWav));
        Assert.True(File.Exists(NoteWriter.AudioPath(keptNote)));
        Assert.Equal((false, false), (kept.LoadState().AudioDeleted, kept.LoadState().Mp3Deleted));

        Assert.False(File.Exists(other.MicWav) || File.Exists(other.OthersWav) || File.Exists(other.MixWav));
        Assert.False(File.Exists(NoteWriter.AudioPath(otherNote)));
        Assert.Equal([NoteWriter.AudioPath(otherNote)], _deleted);
        Assert.Equal((true, true), (other.LoadState().AudioDeleted, other.LoadState().Mp3Deleted));
        // The note and its transcript stay.
        Assert.True(File.Exists(otherNote) && File.Exists(NoteWriter.HtmlPath(otherNote)));
        Assert.True(File.Exists(other.LabelledFile) && File.Exists(other.SummaryFile));
        Assert.Contains("**[00:00:00] Paul:** I'll write it up.", File.ReadAllText(otherNote));

        // Done once: nothing is looked for again. Unticked later, the kept one goes at the next clean-up.
        Assert.Equal((0, 0), AudioCleanUp.Run(s, Later, Delete));
        kept.UpdateMeta(m => m.KeepRecording = false);
        Assert.Equal((1, 1), AudioCleanUp.Run(s, Later, Delete));
        Assert.False(File.Exists(NoteWriter.AudioPath(keptNote)));
    }

    [Fact]
    public async Task With_the_setting_off_no_mp3_is_deleted_and_on_it_goes_after_the_days()
    {
        var s = Settings(mp3s: false);
        var (rec, note) = await Finished(s);
        var mp3 = NoteWriter.AudioPath(note);

        Assert.Equal((0, 0), AudioCleanUp.Run(s, DateTimeOffset.Now.AddDays(13), Delete)); // not old enough
        Assert.True(File.Exists(rec.MicWav));
        Assert.Equal((1, 0), AudioCleanUp.Run(s, Later, Delete));
        Assert.False(File.Exists(rec.MicWav));
        Assert.True(File.Exists(mp3));
        Assert.False(rec.LoadState().Mp3Deleted);
        Assert.Equal((0, 0), AudioCleanUp.Run(s, DateTimeOffset.Now.AddYears(3), Delete));
        Assert.True(File.Exists(mp3));
        Assert.Empty(_deleted);

        // Turned on: a meeting whose working audio went long ago loses its MP3 too, once it's past the days.
        s.NoteMp3.DeleteWithAudio = true;
        Assert.Equal(0, AudioCleanUp.Mp3sToDelete(s.ResolvedRecordingsFolder, 14, DateTimeOffset.Now.AddDays(13)));
        Assert.Equal(1, AudioCleanUp.Mp3sToDelete(s.ResolvedRecordingsFolder, 14, Later));
        Assert.Equal((0, 0), AudioCleanUp.Run(s, DateTimeOffset.Now.AddDays(13), Delete));
        Assert.Equal((0, 1), AudioCleanUp.Run(s, Later, Delete));
        Assert.False(File.Exists(mp3));
        Assert.True(rec.LoadState().Mp3Deleted);
        Assert.Equal(0, AudioCleanUp.Mp3sToDelete(s.ResolvedRecordingsFolder, 14, Later));
    }

    [Fact]
    public async Task Forever_deletes_nothing()
    {
        var s = Settings();
        s.KeepAudioDays = -1;
        var (rec, note) = await Finished(s);

        Assert.Equal((0, 0), AudioCleanUp.Run(s, DateTimeOffset.Now.AddYears(5), Delete));
        Assert.True(File.Exists(rec.MicWav));
        Assert.True(File.Exists(NoteWriter.AudioPath(note)));
        Assert.False(AudioCleanUp.PastDays(DateTimeOffset.Now.AddYears(-5), -1, DateTimeOffset.Now));
        Assert.Equal(0, AudioCleanUp.Mp3sToDelete(s.ResolvedRecordingsFolder, -1, DateTimeOffset.Now.AddYears(5)));
    }

    [Fact]
    public async Task The_note_and_page_stop_pointing_at_a_deleted_mp3_and_the_meeting_file_says_it_has_none()
    {
        var s = Settings();
        var (rec, note) = await Finished(s);
        var name = Path.GetFileName(NoteWriter.AudioPath(note));
        Assert.Contains($"audio: \"{name}\"", File.ReadAllText(note));
        Assert.Contains($" · [recording](<{name}>)", File.ReadAllText(note));
        Assert.Contains("<audio", File.ReadAllText(NoteWriter.HtmlPath(note)));
        Assert.Equal(name, Load(note).Notes!.Audio);
        rec.UpdateState(st => st.PageVersion = 0); // an older page: made again either way

        AudioCleanUp.Run(s, Later, Delete);

        // An untouched note is what Lemur writes without audio, and stays untouched, so later changes still rewrite it.
        var text = File.ReadAllText(note);
        Assert.DoesNotContain("audio:", text);
        Assert.DoesNotContain("[recording]", text);
        Assert.Equal(NoteWriter.Render(rec.LoadMeta(), File.ReadAllText(rec.SummaryFile),
            Naming.Apply(rec.Read<List<LabelledSegment>>(rec.LabelledFile)!, rec.Read<IdentifiedResult>(rec.IdentifiedFile), "Paul"),
            "fake", rec.LoadState().WebUrl, [], rec.Name), text);
        var state = rec.LoadState();
        Assert.Equal(NoteWriter.Hash(note), state.NoteHash);
        var page = File.ReadAllText(NoteWriter.HtmlPath(note));
        Assert.DoesNotContain("<audio", page);
        Assert.DoesNotContain("<script", page);
        Assert.DoesNotContain(Uri.EscapeDataString(name), page);
        Assert.Equal(NoteHtml.PageVersion, state.PageVersion);

        // The meeting file's stamp has whether the MP3 is there: the queue's pass writes it again, with no audio.
        Assert.Equal(1, MeetingFile.RefreshAll(s.ResolvedRecordingsFolder, "Paul"));
        Assert.Null(Load(note).Notes!.Audio);
        Assert.Equal(0, MeetingFile.RefreshAll(s.ResolvedRecordingsFolder, "Paul"));
    }

    [Fact]
    public async Task An_edited_note_keeps_its_edits_and_loses_only_the_links_to_the_mp3()
    {
        var s = Settings();
        var (rec, note) = await Finished(s);
        File.AppendAllText(note, "\nMy own notes.\n");
        var hash = rec.LoadState().NoteHash;

        AudioCleanUp.Run(s, Later, Delete);

        var text = File.ReadAllText(note);
        Assert.EndsWith("My own notes.\n", text.ReplaceLineEndings("\n"));
        Assert.DoesNotContain("audio:", text);
        Assert.DoesNotContain("[recording]", text);
        Assert.Contains("title: \"Budget review\"", text);
        Assert.Equal(hash, rec.LoadState().NoteHash); // still edited: never rewritten whole
        Assert.DoesNotContain("<audio", File.ReadAllText(NoteWriter.HtmlPath(note)));
    }

    [Fact]
    public async Task A_missing_mp3_is_fine_and_one_that_cant_be_deleted_is_tried_again_and_never_named_in_the_log()
    {
        var s = Settings();
        var (gone, goneNote) = await Finished(s, "Budget review");
        File.Delete(NoteWriter.AudioPath(goneNote)); // the user deleted it
        var (held, heldNote) = await Finished(s, "Budget review two", Start.AddHours(1));

        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        Log.Echo = lines.Enqueue;
        try
        {
            Assert.Equal((2, 0), AudioCleanUp.Run(s, Later, _ => throw new IOException($"The process cannot access the file '{NoteWriter.AudioPath(heldNote)}'")));
        }
        finally
        {
            Log.Echo = null;
        }
        Assert.Equal((true, true), (gone.LoadState().AudioDeleted, gone.LoadState().Mp3Deleted));
        Assert.Equal((true, false), (held.LoadState().AudioDeleted, held.LoadState().Mp3Deleted)); // the audio stays deleted; the MP3 is tried again
        Assert.True(File.Exists(NoteWriter.AudioPath(heldNote)));
        var mine = lines.Where(l => l.Contains(gone.Name) || l.Contains(held.Name)).ToList();
        Assert.Contains(mine, l => l.Contains(held.Name) && l.Contains("WARN") && l.Contains("IOException"));
        Assert.DoesNotContain(mine, l => l.Contains("Budget") || l.Contains(_root));

        Assert.Equal((0, 1), AudioCleanUp.Run(s, Later, Delete));
        Assert.False(File.Exists(NoteWriter.AudioPath(heldNote)));
    }

    [Fact]
    public async Task A_meeting_that_cant_be_read_doesnt_stop_the_clean_up_of_the_rest()
    {
        // The clean-up read every state.json outside its try: one it couldn't read stopped it, and the rest of the queue's pass.
        var s = Settings();
        var (broken, _) = await Finished(s, "Budget review");
        var (rec, note) = await Finished(s, "Standup", Start.AddHours(1));
        File.WriteAllText(broken.StateFile, "{ not json");

        Assert.Equal(1, AudioCleanUp.Mp3sToDelete(s.ResolvedRecordingsFolder, 14, Later));
        Assert.Equal((1, 1), AudioCleanUp.Run(s, Later, Delete));
        Assert.False(File.Exists(rec.MicWav));
        Assert.False(File.Exists(NoteWriter.AudioPath(note)));
        Assert.True(File.Exists(broken.MicWav));
    }

    [Fact]
    public async Task A_note_name_taken_by_a_later_meeting_keeps_that_meetings_mp3()
    {
        // The older meeting's note was deleted by hand, and a later one's note took the name: its state still points there.
        var s = Settings();
        var (old, _) = await Finished(s, "Budget review");
        var (later, laterNote) = await Finished(s, "Standup", Start.AddHours(1));
        later.UpdateMeta(m => m.KeepRecording = true);
        old.UpdateState(st => st.NotePath = laterNote);
        var text = File.ReadAllText(laterNote);
        var page = File.ReadAllText(NoteWriter.HtmlPath(laterNote));

        Assert.Equal(0, AudioCleanUp.Mp3sToDelete(s.ResolvedRecordingsFolder, 14, Later));
        Assert.Equal((1, 0), AudioCleanUp.Run(s, Later, Delete)); // the older one's own audio only
        Assert.True(File.Exists(NoteWriter.AudioPath(laterNote)));
        Assert.Equal((text, page), (File.ReadAllText(laterNote), File.ReadAllText(NoteWriter.HtmlPath(laterNote))));
        Assert.Empty(_deleted);
        Assert.True(old.LoadState().Mp3Deleted); // its own MP3 went with its note
    }

    [Theory]
    [InlineData(false, 14, true, 14, true)]    // turned on
    [InlineData(true, 14, true, 14, false)]    // as it was
    [InlineData(true, 14, true, 30, false)]    // more days: nothing more goes
    [InlineData(true, 30, true, 14, true)]     // fewer days
    [InlineData(true, -1, true, 14, true)]     // forever turned off while it's on
    [InlineData(false, 14, true, -1, false)]   // on, but forever
    [InlineData(true, 14, false, 7, false)]    // turned off
    public void Settings_asks_only_when_more_mp3s_would_go(bool wasOn, int wasDays, bool on, int days, bool asks)
    {
        var before = Settings(wasOn);
        before.KeepAudioDays = wasDays;
        var edited = Settings(on);
        edited.KeepAudioDays = days;
        Assert.Equal(asks, AudioCleanUp.MoreMp3sGo(before, edited));
        Assert.Equal(on && days >= 0, AudioCleanUp.MoreMp3sGo(null, edited)); // a first setup
        edited.RecordingsFolder = Path.Combine(_root, "elsewhere");
        Assert.Equal(on && days >= 0, AudioCleanUp.MoreMp3sGo(before, edited)); // another folder's meetings weren't going
    }

    [Fact]
    public void Recycle_deletes_a_path_too_long_for_the_shell_and_never_loses_a_file_it_cant_take()
    {
        // Neither reaches this PC's Recycle Bin: the shell can't take the first, and the second is held open.
        var deep = Path.Combine(_root, new string('d', 120), new string('e', 120));
        Directory.CreateDirectory(deep);
        var longPath = Path.Combine(deep, "2026-10-14 0930 Budget review.mp3");
        File.WriteAllText(longPath, "x");
        Assert.True(Path.GetFullPath(longPath).Length >= 260);
        Assert.False(AudioCleanUp.Recycle(longPath));
        Assert.False(File.Exists(longPath));

        var held = Path.Combine(_root, "2026-10-14 0930 Standup.mp3");
        File.WriteAllText(held, "x");
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = Assert.Throws<IOException>(() => AudioCleanUp.Recycle(held));
            Assert.DoesNotContain("Standup", ex.Message);
        }
        Assert.True(File.Exists(held)); // tried again at the next pass, not deleted for good
    }

    [Fact]
    public async Task Keep_recording_survives_naming_a_fixed_word_and_a_rename()
    {
        var s = Settings();
        var (rec, _) = await Finished(s);
        rec.UpdateMeta(m => m.KeepRecording = true);

        await new SpeakerNamer(s).ApplyAsync(rec, [new NameChange("Speaker A", "Dana Whitfield")], CancellationToken.None);
        Assert.True(MeetingFixes.Apply(rec, new Vocabulary([new VocabularyEntry { Term = "Writeup", HeardAs = ["write it up"] }]), "Paul") > 0);
        MeetingRename.Apply(rec, "Budget review: Q4");
        Assert.True(rec.LoadMeta().KeepRecording);
        Assert.Equal((0, 0), AudioCleanUp.Run(s, Later, Delete));
    }

    [Fact]
    public void Ticked_while_recording_it_goes_into_meta_json_when_it_stops()
    {
        var meta = new RecordingMeta { KeepRecording = true };
        var json = JsonSerializer.Serialize(meta, Json.Options);
        Assert.Contains("\"keepRecording\": true", json);
        Assert.True(JsonSerializer.Deserialize<RecordingMeta>(json, Json.Options)!.KeepRecording);
        Assert.False(JsonSerializer.Deserialize<RecordingMeta>("""{ "title": "Older meta" }""", Json.Options)!.KeepRecording);
    }

    [Theory]
    [InlineData("""{ "displayName": "Paul" }""", false)] // settings from before the choice
    [InlineData("""{ "displayName": "Paul", "noteMp3": null }""", false)]
    [InlineData("""{ "displayName": "Paul", "noteMp3": {} }""", false)]
    [InlineData("""{ "displayName": "Paul", "noteMp3": { "deleteWithAudio": true } }""", true)]
    public void The_mp3_setting_is_off_unless_the_file_says_so(string json, bool on)
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, json);
        var settings = SettingsStore.LoadOrCreate(path);
        Assert.Equal(on, settings.NoteMp3.DeleteWithAudio);
        Assert.Equal(14, settings.KeepAudioDays);

        SettingsStore.Save(settings, path);
        Assert.Contains($"\"deleteWithAudio\": {(on ? "true" : "false")}", File.ReadAllText(path));
    }

    [Fact]
    public async Task The_recordings_window_says_what_is_kept_and_what_is_gone()
    {
        var s = Settings(mp3s: false);
        var (kept, _) = await Finished(s, "Budget review");
        var (ticked, _) = await Finished(s, "Planning", Start.AddHours(1));
        var (gone, _) = await Finished(s, "Standup", Start.AddHours(2));
        kept.UpdateMeta(m => m.KeepRecording = true);
        AudioCleanUp.Run(s, Later, Delete); // MP3s stay: the other two lose their working audio only
        ticked.UpdateMeta(m => m.KeepRecording = true);
        var live = Path.Combine(s.ResolvedRecordingsFolder, "20261014-160000");
        var list = new RecordingList();
        List<RecordingRow> Rows() => list.Build(s.ResolvedRecordingsFolder,
            new LiveRecording(live, "Weekly sync", "Teams (Edge)", TimeSpan.FromMinutes(3), Keep: true), null, DateTimeOffset.Now);
        var rows = Rows();
        RecordingRow Row(RecordingFolder r) => rows.Single(x => x.Name == r.Name);

        Assert.Equal((RowKind.Recording, true, true), (rows[0].Kind, rows[0].Keep, rows[0].CanKeep)); // ticked while being recorded
        Assert.Equal(("Done · recording kept", true, true), (Row(kept).Status, Row(kept).Keep, Row(kept).CanKeep));
        // Its working audio went before it was ticked: what's kept is its MP3.
        Assert.Equal(("Done · audio deleted · MP3 kept", true, false, true), (Row(ticked).Status, Row(ticked).CanKeep, Row(ticked).HasAudio, Row(ticked).HasMp3));
        Assert.Equal(("Done · audio deleted", true), (Row(gone).Status, Row(gone).CanKeep)); // its MP3 can still be kept
        Assert.Contains("The working audio has been deleted (the note keeps its MP3).", Row(gone).Detail);

        s.NoteMp3.DeleteWithAudio = true;
        Assert.Equal((0, 1), AudioCleanUp.Run(s, Later, Delete));
        rows = Rows();
        Assert.Equal(("Done · audio and MP3 deleted", false, false), (Row(gone).Status, Row(gone).HasMp3, Row(gone).CanKeep)); // nothing left
        Assert.Contains("The working audio and the note's MP3 have been deleted. The note and its transcript stay.", Row(gone).Detail);
        Assert.Equal("Done · audio deleted · MP3 kept", Row(ticked).Status);
    }
}
