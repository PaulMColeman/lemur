using Lemur.Audio;
using Lemur.Backends;
using Lemur.Config;
using Lemur.Pipeline;
using Lemur.Speakers;
using Lemur.Transcripts;

namespace Lemur.Tests;

/// <summary>Finished meetings made again with the current Lemur, keeping what the user decided.</summary>
public class RemakeTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("llmmr-remake").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    AppSettings Settings() => new()
    {
        DisplayName = "Paul",
        RecordingsFolder = Path.Combine(_root, "rec"),
        NotesFolder = Path.Combine(_root, "notes"),
        Backend = { Type = BackendTypes.Scriberr, Scriberr = { BaseUrl = "https://x" } },
        Speakers = { PeopleFolder = Path.Combine(_root, "people") },
    };

    /// <summary>Scriberr-like: hands back its job's transcript (the first time without word times, later with them).</summary>
    sealed class JobBackend : IMeetingBackend, IResumableTranscription
    {
        public List<TranscriptSegment> Segments = [];
        public string Summary = "Title: Plan review\nAbout: They agreed to write it up.\n\n## Summary\nTest.";
        public bool FailSummary;
        public readonly List<string?> JobsAsked = [];
        public string? LastTranscriptText;

        public string Kind => BackendTypes.Scriberr;
        public Task<TranscriptResult> TranscribeAsync(string audioPath, string title, TimeSpan duration, CancellationToken ct) =>
            TranscribeAsync(audioPath, title, duration, null, null, ct);
        public Task<TranscriptResult> TranscribeAsync(string audioPath, string title, TimeSpan duration, string? job,
            Action<TranscriptionJob>? started, CancellationToken ct)
        {
            JobsAsked.Add(job);
            if (job is null) started?.Invoke(new TranscriptionJob("j1", "https://fake/j1"));
            return Task.FromResult(new TranscriptResult(Segments, "j1", "https://fake/j1"));
        }
        public Task NameSpeakersAsync(TranscriptResult t, IReadOnlyDictionary<string, string> names, CancellationToken ct) => Task.CompletedTask;
        public Task<string> SummarizeAsync(TranscriptResult t, string instructions, string labelled, CancellationToken ct)
        {
            LastTranscriptText = labelled;
            return FailSummary ? throw new BackendException("Scriberr summarize: no summary came back") : Task.FromResult(Summary);
        }
        public void Dispose() { }
    }

    /// <summary>Two seconds: the user on the mic in the first, someone on the call in the second.</summary>
    RecordingFolder MakeRecording(AppSettings s)
    {
        var started = new DateTimeOffset(2026, 10, 14, 14, 6, 0, TimeSpan.FromHours(-5));
        var rec = RecordingFolder.CreateNew(s.ResolvedRecordingsFolder, started);
        int rate = AnchoredTrackWriter.SampleRate;
        var mic = new float[2 * rate];
        var others = new float[2 * rate];
        for (int i = 0; i < rate; i++) mic[i] = 0.2f * MathF.Sin(i * 0.2f);
        for (int i = rate; i < 2 * rate; i++) others[i] = 0.2f * MathF.Sin(i * 0.3f);
        using (var a = new WavSampleSink(rec.MicWav)) a.Write(mic);
        using (var b = new WavSampleSink(rec.OthersWav)) b.Write(others);
        rec.SaveMeta(new RecordingMeta { Title = "Budget Update", TitleByUser = true, Source = "Teams (Edge)", StartedAt = started, DurationSeconds = 2 });
        rec.SaveState(new ProcessingState());
        return rec;
    }

    static SpeakerIdentifier NoVoices(AppSettings s) => new(s, () => null, () => null) { Learn = false };

    static TranscriptWord W(double start, double end, string text, string speaker) => new(start, end, text, speaker);

    async Task<(RecordingFolder Rec, ProcessingState State, JobBackend Backend)> Finished(AppSettings s, List<TranscriptSegment> first)
    {
        var rec = MakeRecording(s);
        var backend = new JobBackend { Segments = first };
        var state = await new MeetingProcessor(s, () => backend, NoVoices(s)).ProcessAsync(rec, CancellationToken.None);
        Assert.Equal(Stage.Done, state.Stage);
        return (rec, state, backend);
    }

    [Fact]
    public async Task A_remake_leaves_the_transcript_out_of_the_meeting_file_when_settings_say_so()
    {
        var s = Settings();
        var (rec, before, backend) = await Finished(s, [new(0.2, 0.9, "S1", "I'll write it up."), new(1.1, 1.9, "S2", "Thanks! Really.")]);
        var file = MeetingFile.PathFor(before.NotePath!);
        Assert.Contains("Thanks! Really.", File.ReadAllText(file));

        s.MeetingFile.IncludeTranscript = false;
        await new MeetingRemake(s, () => backend, NoVoices(s)).RunAsync(rec, CancellationToken.None);
        Assert.DoesNotContain("\"transcript\":", File.ReadAllText(file));
        Assert.DoesNotContain("Really", File.ReadAllText(file));
    }

    [Fact]
    public async Task A_meeting_is_made_again_with_its_names_title_note_and_history_kept()
    {
        var s = Settings();
        var (rec, before, backend) = await Finished(s,
        [
            new(0.2, 0.9, "S1", "I'll write it up."),
            new(1.1, 1.9, "S2", "Thanks! Really."),
        ]);
        await new SpeakerNamer(s).ApplyAsync(rec, [new NameChange("Speaker A", "Dana")], CancellationToken.None);
        var namedNote = File.ReadAllText(before.NotePath!);
        Assert.Contains("Dana", namedNote);

        // This time the engine made one line of both, and timed its words: the words say where it changes track.
        backend.Segments =
        [
            new(0.2, 1.9, "S1", "I'll write it up. Thanks! Really.",
                [W(0.2, 0.4, "I'll", "S1"), W(0.4, 0.6, "write", "S1"), W(0.6, 0.75, "it", "S1"), W(0.75, 0.9, "up.", "S1"),
                 W(1.1, 1.5, "Thanks!", "S2"), W(1.5, 1.9, "Really.", "S2")]),
        ];
        var r = await new MeetingRemake(s, () => backend, NoVoices(s)).RunAsync(rec, CancellationToken.None);

        Assert.Equal("j1", backend.JobsAsked[^1]); // the job's transcript was fetched again, nothing uploaded
        Assert.Equal(1, r.SplitLines);
        Assert.Equal(1, r.NamesKept);
        Assert.Contains("[00:00:01] Dana: Thanks! Really.", backend.LastTranscriptText);
        var after = rec.LoadState();
        Assert.Equal(before.NotePath, after.NotePath); // the same note, rewritten in place
        Assert.Equal(NoteWriter.Hash(after.NotePath!), after.NoteHash);
        Assert.Equal(before.CompletedAt, after.CompletedAt);
        Assert.Equal(before.Steps.Select(t => (t.Step, t.StartedAt, t.FinishedAt, t.Tries)),
            after.Steps.Select(t => (t.Step, t.StartedAt, t.FinishedAt, t.Tries))); // estimates and the audio's age don't move
        var note = File.ReadAllText(after.NotePath!);
        Assert.Contains("**[00:00:01] Dana:** Thanks! Really.", note);
        Assert.Contains("# Budget Update", note);
        Assert.Equal("They agreed to write it up.", rec.LoadMeta().Description);
        Assert.Equal(namedNote, File.ReadAllText(Path.Combine(r.Backup, Path.GetFileName(after.NotePath!))));
        Assert.True(File.Exists(Path.Combine(r.Backup, "labelled.json")));
        Assert.Equal(["Paul", "Speaker A"], rec.Read<List<LabelledSegment>>(rec.LabelledFile)!.Select(x => x.Speaker));
    }

    [Fact]
    public async Task Keep_recording_survives_a_remake()
    {
        var s = Settings();
        var (rec, before, backend) = await Finished(s, [new(0.2, 0.9, "S1", "I'll write it up."), new(1.1, 1.9, "S2", "Thanks!")]);
        rec.UpdateMeta(m => m.KeepRecording = true);

        await new MeetingRemake(s, () => backend, NoVoices(s)).RunAsync(rec, CancellationToken.None);
        Assert.True(rec.LoadMeta().KeepRecording);
        Assert.Equal(before.CompletedAt, rec.LoadState().CompletedAt); // the clean-up's days don't start again
        s.NoteMp3.DeleteWithAudio = true;
        Assert.Equal((0, 0), AudioCleanUp.Run(s, DateTimeOffset.Now.AddDays(15), _ => throw new InvalidOperationException("kept")));
        Assert.True(File.Exists(rec.MixWav));
        Assert.True(File.Exists(NoteWriter.AudioPath(before.NotePath!)));
    }

    [Fact]
    public async Task A_name_given_to_the_users_own_letter_is_left_for_the_voices()
    {
        // The old labelling put a line under the user's voice group's letter, and the user named that letter after whoever
        // they heard in its clip. Made again, that name isn't carried over, and the voiceprint it saved is taken out.
        var s = Settings();
        var (rec, _, backend) = await Finished(s,
        [
            new(0.05, 0.95, "S1", "I'll write it up."),
            new(1.5, 1.9, "S1", "Thanks!"), // on the call track, though the engine heard the user's voice
        ]);
        Assert.Equal("Speaker A", rec.Read<List<LabelledSegment>>(rec.LabelledFile)![1].Speaker);
        var store = new PeopleStore(s.Speakers.ResolvedPeopleFolder);
        var sam = store.Create("Sam");
        var own = new float[] { 1, 0, 0 };
        sam.Prints.Add(new Voiceprint { Id = "pr-own", Model = "m", Vector = Vectors.ToBase64(own), From = new PrintSource { Recording = rec.Name } });
        sam.Prints.Add(new Voiceprint { Id = "pr-other", Model = "m", Vector = Vectors.ToBase64([0, 1, 0]), From = new PrintSource { Recording = "elsewhere" } });
        store.Save(sam);
        rec.Write(rec.VoiceprintsFile, new MeetingVoiceprints { Model = "m", Speakers = { ["S1"] = Vectors.ToBase64(own) } });
        await new SpeakerNamer(s).ApplyAsync(rec, [new NameChange("Speaker A", "Sam")], CancellationToken.None);

        // Taken out before the voices are matched (left in, the letter recognised itself), and put back if it fails.
        backend.FailSummary = true;
        await Assert.ThrowsAsync<BackendException>(() => new MeetingRemake(s, () => backend, NoVoices(s)).RunAsync(rec, CancellationToken.None));
        Assert.Equal(["pr-other", "pr-own"], store.Find("Sam")!.Prints.Select(p => p.Id).Order());
        backend.FailSummary = false;

        var r = await new MeetingRemake(s, () => backend, NoVoices(s)).RunAsync(rec, CancellationToken.None);
        Assert.Equal((0, 1, 1), (r.NamesKept, r.NamesLeft, r.PrintsRemoved));
        Assert.Contains("Speaker A: Thanks!", backend.LastTranscriptText);
        Assert.Equal(["pr-other"], store.Find("Sam")!.Prints.Select(p => p.Id));
    }

    [Fact]
    public async Task An_edited_note_or_a_failed_summary_changes_nothing()
    {
        var s = Settings();
        var (rec, state, backend) = await Finished(s, [new(0.2, 0.9, "S1", "I'll write it up."), new(1.1, 1.9, "S2", "Thanks!")]);
        var remake = new MeetingRemake(s, () => backend, NoVoices(s));

        backend.FailSummary = true;
        var labelled = File.ReadAllText(rec.LabelledFile);
        await Assert.ThrowsAsync<BackendException>(() => remake.RunAsync(rec, CancellationToken.None));
        Assert.Equal(labelled, File.ReadAllText(rec.LabelledFile));
        Assert.Equal(state.NoteHash, NoteWriter.Hash(state.NotePath!));

        backend.FailSummary = false;
        File.AppendAllText(state.NotePath!, "\nMy own notes.\n");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => remake.RunAsync(rec, CancellationToken.None));
        Assert.Contains("edited", ex.Message);
        Assert.EndsWith("My own notes.\n", File.ReadAllText(state.NotePath!).ReplaceLineEndings("\n"));
        Assert.DoesNotContain(Directory.GetDirectories(rec.Path), d => Path.GetFileName(d).StartsWith("before-remake"));
    }
}
