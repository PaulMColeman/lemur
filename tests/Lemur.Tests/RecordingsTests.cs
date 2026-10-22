using NAudio.Wave;
using Lemur.Audio;
using Lemur.Backends;
using Lemur.Config;
using Lemur.Pipeline;
using Lemur.Transcripts;

namespace Lemur.Tests;

public class RecordingsTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("llmmr-list").FullName;
    static readonly DateTimeOffset Now = new(2026, 10, 14, 15, 0, 0, TimeSpan.FromHours(-5));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    RecordingFolder Folder(string name, ProcessingState? state, string title = "Standup", double seconds = 600)
    {
        var rec = new RecordingFolder(Path.Combine(_root, name));
        Directory.CreateDirectory(rec.Path);
        if (state is not null)
        {
            rec.SaveMeta(new RecordingMeta { Title = title, Source = "Teams (Edge)", StartedAt = Now.AddHours(-1), DurationSeconds = seconds });
            rec.SaveState(state);
        }
        return rec;
    }

    /// <summary>A WAV as a killed app leaves it: samples written, sizes still zero.</summary>
    static void Unfinished(string path, int samples)
    {
        using (var sink = new WavSampleSink(path)) sink.Write(new float[samples]);
        var bytes = File.ReadAllBytes(path);
        int data = System.Text.Encoding.ASCII.GetString(bytes).IndexOf("data", StringComparison.Ordinal);
        Array.Clear(bytes, 4, 4);          // RIFF size
        Array.Clear(bytes, data + 4, 4);   // data size
        File.WriteAllBytes(path, bytes);
    }

    [Fact]
    public void Each_recording_says_what_is_happening_to_it()
    {
        var note = Path.Combine(_root, "note.md");
        File.WriteAllText(note, "# note");
        Folder("20261014-090000", new ProcessingState { Stage = Stage.Done, NotePath = note, AudioDeleted = true });
        Folder("20261014-100000", new ProcessingState
        {
            Stage = Stage.Identified, Attempts = 2, LastError = "Scriberr summarize: no summary came back",
            NextAttemptAt = Now.AddMinutes(4), WebUrl = "https://scriberr.test/audio/j1",
        });
        Folder("20261014-110000", new ProcessingState { Stage = Stage.Mixed });
        Folder("20261014-120000", new ProcessingState { Stage = Stage.Recorded }, title: "");
        var live = Folder("20261014-130000", null);

        var rows = new RecordingList().Build(_root, new LiveRecording(live.Path, "Weekly sync", "Teams (Edge)", TimeSpan.FromSeconds(754)),
            ("20261014-110000", Stage.Mixed), Now);

        Assert.Equal(["20261014-130000", "20261014-120000", "20261014-110000", "20261014-100000", "20261014-090000"], rows.Select(r => r.Name));
        Assert.Equal((RowKind.Recording, "Recording · 12:34"), (rows[0].Kind, rows[0].Status));
        Assert.Equal((RowKind.Waiting, "Waiting", "Teams (Edge) call", true), (rows[1].Kind, rows[1].Status, rows[1].Title, rows[1].Untitled));
        Assert.Equal((RowKind.Working, "Transcribing… (step 2 of 5)"), (rows[2].Kind, rows[2].Status));
        Assert.Equal(RowKind.Retrying, rows[3].Kind);
        Assert.StartsWith($"Retrying at {Now.AddMinutes(4).ToLocalTime():HH:mm} — Scriberr summarize", rows[3].Status);
        Assert.Contains("Couldn't finish summarizing (try 2)", rows[3].Detail);
        Assert.Contains("https://scriberr.test/audio/j1", rows[3].Detail);
        Assert.True(rows[3].CanRetry);
        Assert.Equal((RowKind.Done, "Done · audio deleted"), (rows[4].Kind, rows[4].Status));
        Assert.True(rows[4].HasNote);
        Assert.False(rows[4].CanRetry);
    }

    static StepTime Took(Step step, DateTimeOffset start, double seconds, int tries = 1) =>
        new() { Step = step, StartedAt = start, FinishedAt = start.AddSeconds(seconds), Tries = tries };

    [Fact]
    public void Each_step_shows_how_long_it_took_or_should_take()
    {
        var ended = Now.AddMinutes(-5);
        var rec = Folder("20261014-130000", new ProcessingState
        {
            Stage = Stage.Transcribed,
            Steps =
            [
                Took(Step.Mix, ended, 0.7),
                Took(Step.Transcribe, ended.AddSeconds(1), 208),
                new StepTime { Step = Step.NameSpeakers, StartedAt = Now.AddSeconds(-70), Tries = 1 },
            ],
        }, seconds: 2900);
        var meta = rec.LoadMeta();
        meta.EndedAt = meta.StartedAt.AddSeconds(2900);
        rec.SaveMeta(meta);
        Folder("20261014-140000", new ProcessingState(), seconds: 600);

        var rows = new RecordingList().Build(_root, null, ("20261014-130000", Stage.Transcribed), Now);
        var working = rows.Single(r => r.Name == "20261014-130000");

        Assert.Equal("Naming speakers… (step 3 of 5)", working.Status);
        Assert.Equal(["Recording", "Mixing the two tracks", "Transcribing", "Naming speakers", "Summarizing", "Writing the note"],
            working.Steps.Select(l => l.Name));
        Assert.Equal([StepState.Done, StepState.Done, StepState.Done, StepState.Working, StepState.Next, StepState.Next],
            working.Steps.Select(l => l.State));
        Assert.Equal(["48:20", "under 1 s", "3 min 28 s", "1 min 10 s"], working.Steps.Take(4).Select(l => l.Time));
        Assert.StartsWith("usually ~", working.Steps[3].Note);
        Assert.StartsWith("~", working.Steps[4].Time);
        Assert.Matches(@"^Notes expected around \d\d:\d\d \(about .+ more\)\.$", working.Expected);

        // Queued behind it: its notes come after the one being worked on.
        var waiting = rows.Single(r => r.Name == "20261014-140000");
        Assert.All(waiting.Steps.Skip(1), l => Assert.Equal(StepState.Next, l.State));
        Assert.StartsWith("Starts in about", waiting.Expected);
    }

    [Fact]
    public void A_finished_meeting_says_how_long_its_notes_took()
    {
        var ended = Now.AddHours(-1);
        var start = ended.AddSeconds(1);
        var rec = Folder("20261014-090000", new ProcessingState
        {
            Stage = Stage.Done,
            CompletedAt = ended.AddSeconds(391),
            Steps =
            [
                Took(Step.Mix, start, 1), Took(Step.Transcribe, start, 208), Took(Step.NameSpeakers, start, 63),
                Took(Step.Summarize, start, 114), Took(Step.WriteNote, start, 5),
            ],
        });
        var meta = rec.LoadMeta();
        meta.EndedAt = ended;
        rec.SaveMeta(meta);

        var row = new RecordingList().Build(_root, null, null, Now).Single();

        Assert.All(row.Steps, l => Assert.Equal(StepState.Done, l.State));
        Assert.Equal("Notes were ready 6 min 31 s after the call ended (6 min 31 s of processing).", row.Expected);
        Assert.Equal(TimeSpan.Zero, row.Remaining);
    }

    [Fact]
    public void Estimates_start_from_measurements_and_follow_this_pcs_meetings()
    {
        var measured = StepEstimates.Measured();
        Assert.InRange(measured.For(Step.Transcribe, 2900).TotalSeconds, 180, 240);   // 48 min: 208 s measured
        Assert.InRange(measured.For(Step.Summarize, 2900).TotalSeconds, 90, 180);     // 114 s measured

        // A server twice as slow: one meeting is enough to show it. A step that needed retries says nothing about speed.
        var slow = new ProcessingState { Steps = [Took(Step.Transcribe, Now, 2 * measured.For(Step.Transcribe, 1200).TotalSeconds)] };
        var retried = new ProcessingState { Steps = [Took(Step.Summarize, Now, 3000, tries: 3)] };
        var estimates = StepEstimates.From([(1200, slow), (1200, retried)]);

        Assert.Equal(1, estimates.Meetings);
        Assert.Equal(2 * measured.For(Step.Transcribe, 3600).TotalSeconds, estimates.For(Step.Transcribe, 3600).TotalSeconds, 0.001);
        Assert.Equal(measured.For(Step.Summarize, 3600), estimates.For(Step.Summarize, 3600));
        Assert.Equal(PipelineSteps.All.Aggregate(TimeSpan.Zero, (sum, s) => sum + estimates.For(s, 3600)), estimates.Total(3600));
    }

    [Fact]
    public void An_unfinished_recording_is_listed_and_can_be_turned_into_notes()
    {
        var rec = Folder("20261014-093000", null);
        Unfinished(rec.MicWav, 16000 * 3);
        Unfinished(rec.OthersWav, 16000 * 3);
        File.SetLastWriteTimeUtc(rec.MicWav, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(rec.OthersWav, DateTime.UtcNow.AddMinutes(-10));

        var row = Assert.Single(new RecordingList().Build(_root, null, null, Now));
        Assert.Equal(RowKind.Interrupted, row.Kind);
        Assert.Equal(3, row.Seconds, 1);
        Assert.ThrowsAny<FormatException>(() => new WaveFileReader(rec.MicWav)); // unreadable before the repair

        var meta = rec.Recover();
        Assert.Equal(3, meta.DurationSeconds, 1);
        Assert.Equal(new DateTime(2026, 10, 14, 9, 30, 0), meta.StartedAt.LocalDateTime);
        Assert.Equal("Recovered", meta.Source);
        using (var fixedUp = new WaveFileReader(rec.MicWav)) Assert.Equal(16000 * 3 * 2, fixedUp.Length);
        Assert.Equal(Stage.Recorded, rec.LoadState().Stage);
        Assert.Single(RecordingFolder.All(_root)); // now an ordinary recording, queued for notes
        Assert.False(WavRepair.FixSizes(rec.MicWav)); // and a repaired file needs nothing more
    }

    [Fact]
    public void A_recording_still_being_written_is_not_called_unfinished()
    {
        var rec = Folder("20261014-093000", null);
        Unfinished(rec.MicWav, 1600);
        Unfinished(rec.OthersWav, 1600); // written just now
        Assert.Empty(RecordingFolder.Interrupted(_root));
        File.SetLastWriteTimeUtc(rec.MicWav, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(rec.OthersWav, DateTime.UtcNow.AddMinutes(-10));
        Assert.Empty(RecordingFolder.Interrupted(_root, exceptPath: rec.Path)); // the tray's live recording
        Assert.Single(RecordingFolder.Interrupted(_root));
    }

    sealed class CountingBackend : IMeetingBackend
    {
        public int Transcribes;
        public string Kind => "count";
        public Task<TranscriptResult> TranscribeAsync(string audioPath, string title, TimeSpan duration, CancellationToken ct)
        {
            Transcribes++;
            throw new BackendException("server unreachable");
        }
        public Task NameSpeakersAsync(TranscriptResult t, IReadOnlyDictionary<string, string> names, CancellationToken ct) => Task.CompletedTask;
        public Task<string> SummarizeAsync(TranscriptResult t, string instructions, string labelled, CancellationToken ct) => Task.FromResult("");
        public void Dispose() { }
    }

    [Fact]
    public async Task Retry_now_skips_the_wait_once_and_reports_each_stage()
    {
        var settings = new AppSettings { RecordingsFolder = _root, NotesFolder = Path.Combine(_root, "notes") };
        var rec = Folder("20261014-100000", new ProcessingState { Stage = Stage.Mixed });
        File.WriteAllBytes(rec.MixWav, []);
        var backend = new CountingBackend();
        var queue = new ProcessingQueue(() => settings, () => new MeetingProcessor(settings, () => backend));
        var stages = new List<Stage>();
        queue.StageStarted += (_, stage) => stages.Add(stage);

        await queue.RunOnceAsync(CancellationToken.None);   // fails; next try in a minute
        await queue.RunOnceAsync(CancellationToken.None);   // still waiting: not tried
        Assert.Equal(1, backend.Transcribes);
        queue.RetryNow(rec.Name);
        await queue.RunOnceAsync(CancellationToken.None);   // asked: tried at once
        await queue.RunOnceAsync(CancellationToken.None);   // and only once
        Assert.Equal(2, backend.Transcribes);
        Assert.Equal([Stage.Mixed, Stage.Mixed], stages);
        Assert.Null(queue.Current);
    }
}
