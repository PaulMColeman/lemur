using Lemur.Audio;
using Lemur.Backends;
using Lemur.Config;
using Lemur.Pipeline;
using Lemur.Transcripts;

namespace Lemur.Tests;

sealed class FakeBackend : IMeetingBackend
{
    public int Transcribes, Summaries;
    public bool FailTranscribe;
    public string Summary = "## Summary\nTest.\n\n## Action items\n- [ ] Paul - write it up";
    public Dictionary<string, string>? Named;
    public string? LastTranscriptText;
    public string? LastInstructions;

    public string Kind => "fake";

    public Task<TranscriptResult> TranscribeAsync(string audioPath, string title, TimeSpan duration, CancellationToken ct)
    {
        Transcribes++;
        if (FailTranscribe) throw new BackendException("server unreachable");
        return Task.FromResult(new TranscriptResult(
        [
            new(0.2, 0.9, "S1", "I'll write it up."),
            new(1.1, 1.9, "S2", "Thanks!"),
        ], "j1", "https://fake/j1"));
    }

    public Task NameSpeakersAsync(TranscriptResult t, IReadOnlyDictionary<string, string> names, CancellationToken ct)
    {
        Named = new(names);
        return Task.CompletedTask;
    }

    public Task<string> SummarizeAsync(TranscriptResult t, string instructions, string labelled, CancellationToken ct)
    {
        Summaries++;
        LastTranscriptText = labelled;
        LastInstructions = instructions;
        return Task.FromResult(Summary);
    }

    public void Dispose() { }
}

public class MeetingProcessorTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("llmmr-pipe").FullName;

    AppSettings Settings() => new()
    {
        DisplayName = "Paul",
        RecordingsFolder = Path.Combine(_root, "rec"),
        NotesFolder = Path.Combine(_root, "notes"),
        Backend = { Type = BackendTypes.Scriberr, Scriberr = { BaseUrl = "https://x" } },
    };

    RecordingFolder MakeRecording(AppSettings s)
    {
        var rec = RecordingFolder.CreateNew(s.ResolvedRecordingsFolder, new DateTimeOffset(2026, 10, 14, 9, 30, 0, TimeSpan.FromHours(-5)));
        int rate = AnchoredTrackWriter.SampleRate;
        var mic = new float[2 * rate];
        var others = new float[2 * rate];
        for (int i = 0; i < rate; i++) mic[i] = 0.2f * MathF.Sin(i * 0.2f);             // you: first second
        for (int i = rate; i < 2 * rate; i++) others[i] = 0.2f * MathF.Sin(i * 0.3f);    // them: second second
        for (int i = 0; i < 2 * rate; i++) mic[i] += 0.0005f * MathF.Sin(i * 1.7f);     // a little room noise
        using (var a = new WavSampleSink(rec.MicWav)) a.Write(mic);
        using (var b = new WavSampleSink(rec.OthersWav)) b.Write(others);
        rec.SaveMeta(new RecordingMeta { Title = "Design review / Q4", Source = "Teams (Edge)", StartedAt = new DateTimeOffset(2026, 10, 14, 9, 30, 0, TimeSpan.FromHours(-5)), DurationSeconds = 2 });
        rec.SaveState(new ProcessingState());
        return rec;
    }

    [Fact]
    public async Task Runs_every_stage_and_writes_a_named_note()
    {
        var s = Settings();
        var rec = MakeRecording(s);
        var backend = new FakeBackend();
        var state = await new MeetingProcessor(s, () => backend).ProcessAsync(rec, CancellationToken.None);

        Assert.Equal(Stage.Done, state.Stage);
        Assert.Equal("https://fake/j1", state.WebUrl);
        Assert.Equal(new Dictionary<string, string> { ["S1"] = "Paul" }, backend.Named);
        Assert.StartsWith("[00:00:00] Paul: I'll write it up.", backend.LastTranscriptText);
        Assert.Contains("Speaker A: Thanks!", backend.LastTranscriptText);
        var note = File.ReadAllText(state.NotePath!);
        Assert.EndsWith("2026-10-14 0930 Design review - Q4.md", state.NotePath);
        Assert.Contains("- [ ] Paul - write it up", note);
        Assert.Contains("**[00:00:00] Paul:** I'll write it up.", note);
        Assert.Contains("link: https://fake/j1", note);

        var page = File.ReadAllText(NoteWriter.HtmlPath(state.NotePath!));
        Assert.EndsWith("2026-10-14 0930 Design review - Q4.html", NoteWriter.HtmlPath(state.NotePath!));
        Assert.Contains("<title>Design review / Q4</title>", page);
        Assert.Contains("type=\"checkbox\"", page);
        Assert.Contains("href=\"https://fake/j1\"", page);

        // The mix is kept beside the note as an MP3 the page plays, line by line.
        Assert.True(new FileInfo(NoteWriter.AudioPath(state.NotePath!)).Length > 1000);
        Assert.Contains("audio: \"2026-10-14 0930 Design review - Q4.mp3\"", note);
        Assert.Contains("[recording](<2026-10-14 0930 Design review - Q4.mp3>)", note);
        const string src = "2026-10-14%200930%20Design%20review%20-%20Q4.mp3";
        Assert.Contains($"<audio id=\"audio\" controls preload=\"metadata\" src=\"{src}\"></audio>", page);
        Assert.Contains($"<li class=\"me\"><time><a href=\"{src}#t=0.2\">00:00:00</a></time><b>Paul</b>" +
                        "<p><span data-t=\"0.2\" data-e=\"0.9\">I&#39;ll write it up.</span></p></li>", page);
        Assert.Contains($"<li class=\"s0\"><time><a href=\"{src}#t=1.1\">00:00:01</a></time><b>Speaker A</b>" +
                        "<p><span data-t=\"1.1\" data-e=\"1.9\">Thanks!</span></p></li>", page);
    }

    [Fact]
    public void Without_audio_the_page_is_plain_text_with_no_script()
    {
        var meta = new RecordingMeta { Title = "Standup", Source = "Teams", StartedAt = DateTimeOffset.Now, DurationSeconds = 5 };
        var page = NoteHtml.Render(meta, "## Summary\nOne.",
            [new(0, 1, "Paul", "Hello", true, "S1"), new(1, 2, "Paul", "there.", true, "S1")], "fake", null);
        Assert.Contains("<li class=\"me\"><time>00:00:00</time><b>Paul</b><p>Hello there.</p></li>", page);
        Assert.Contains("content=\"default-src 'none'; style-src 'unsafe-inline'\"", page);
        Assert.DoesNotContain("<script", page);
        Assert.DoesNotContain("<audio", page);
    }

    [Fact]
    public void The_player_script_is_the_only_one_and_the_policy_allows_exactly_it()
    {
        var meta = new RecordingMeta { Title = "x", Source = "Teams", StartedAt = DateTimeOffset.Now, DurationSeconds = 5 };
        var page = NoteHtml.Render(meta, "## Summary\n<script>alert(1)</script>",
            [new(0, 1, "Speaker A", "<script>alert(3)</script>", false, "S1")], "fake", null, audio: "a \"b\" & c.mp3");

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(page, "<script"));
        Assert.Contains("&lt;script&gt;alert(3)&lt;/script&gt;", page);
        Assert.Contains("src=\"a%20%22b%22%20%26%20c.mp3\"", page);

        int start = page.IndexOf("<script>") + "<script>".Length;
        var script = page[start..page.IndexOf("</script>", start)];
        Assert.Equal(NoteHtml.PlayerScript, script);
        Assert.DoesNotContain('\r', script); // browsers hash it with LF line endings
        Assert.Contains($"media-src 'self' file:; script-src '{NoteHtml.ScriptHash(script)}'\"", page);
    }

    static string Tone(string folder, double seconds = 2)
    {
        var path = Path.Combine(folder, "mix.wav");
        int rate = AnchoredTrackWriter.SampleRate;
        var samples = new float[(int)(seconds * rate)];
        for (int i = 0; i < samples.Length; i++) samples[i] = 0.2f * MathF.Sin(i * 0.2f);
        using var sink = new WavSampleSink(path);
        sink.Write(samples);
        return path;
    }

    [Fact]
    public void An_MP3_alone_still_claims_the_name_and_rewrites_keep_the_player()
    {
        var s = Settings();
        var meta = new RecordingMeta { Title = "Standup", Source = "Teams", StartedAt = DateTimeOffset.Now, DurationSeconds = 2 };
        var wav = Tone(_root);
        var first = NoteWriter.Write(s.NotesFolder, meta, "## Summary\nOne.", [new(0, 1, "Paul", "Hi.", true, "S1")], "fake", null, audioWav: wav);
        File.Delete(first);
        File.Delete(NoteWriter.HtmlPath(first));   // the MP3 alone still claims the name
        var second = NoteWriter.Write(s.NotesFolder, meta, "## Summary\nTwo.", [new(0, 1, "Paul", "Hi.", true, "S1")], "fake", null, audioWav: wav);
        Assert.EndsWith(" (2).md", second);
        Assert.True(File.Exists(NoteWriter.AudioPath(second)));
        Assert.Empty(Directory.GetFiles(s.NotesFolder, "*.tmp"));

        NoteWriter.Rewrite(second, meta, "## Summary\nThree.", [new(0, 1, "Dana", "Hi.", false, "S1")], "fake", null, ["Dana"], null);
        Assert.Contains("[recording](<", File.ReadAllText(second));
        Assert.Contains("<audio", File.ReadAllText(NoteWriter.HtmlPath(second)));
        File.Delete(NoteWriter.AudioPath(second));
        NoteWriter.RewriteHtml(second, meta, "## Summary\nThree.", [new(0, 1, "Dana", "Hi.", false, "S1")], "fake", null, ["Dana"], null);
        Assert.DoesNotContain("<audio", File.ReadAllText(NoteWriter.HtmlPath(second)));
    }

    [Fact]
    public void Audio_that_cannot_be_encoded_leaves_the_note_without_a_player()
    {
        var s = Settings();
        var meta = new RecordingMeta { Title = "Standup", Source = "Teams", StartedAt = DateTimeOffset.Now, DurationSeconds = 2 };
        var bad = Path.Combine(_root, "not-audio.wav");
        File.WriteAllText(bad, "this is not a wav file");
        var path = NoteWriter.Write(s.NotesFolder, meta, "## Summary\nOne.", [new(0, 1, "Paul", "Hi.", true, "S1")], "fake", null, audioWav: bad);
        Assert.True(File.Exists(path));
        Assert.DoesNotContain("<audio", File.ReadAllText(NoteWriter.HtmlPath(path)));
        Assert.DoesNotContain("audio:", File.ReadAllText(path));
        Assert.Equal(2, Directory.GetFiles(s.NotesFolder).Length); // just the pair: no MP3, no leftover .tmp
    }

    [Fact]
    public void The_page_escapes_whatever_the_transcript_and_summary_contain()
    {
        var meta = new RecordingMeta { Title = "<b>x</b>", Source = "Teams", StartedAt = DateTimeOffset.Now, DurationSeconds = 5 };
        var page = NoteHtml.Render(meta, "## Summary\n<script>alert(1)</script> and <img src=x onerror=alert(2)>",
            [new(0, 1, "Speaker A", "<script>alert(3)</script>", false, "S1")], "fake", null);
        Assert.DoesNotContain("<script", page);
        Assert.DoesNotContain("<img", page);
        Assert.DoesNotContain("<b>x</b>", page);
        Assert.Contains("&lt;script&gt;alert(3)&lt;/script&gt;", page);
        Assert.Contains("default-src 'none'", page);
    }

    [Fact]
    public void A_second_note_with_the_same_name_gets_its_own_pair()
    {
        var s = Settings();
        var meta = new RecordingMeta { Title = "Standup", Source = "Teams", StartedAt = DateTimeOffset.Now, DurationSeconds = 5 };
        var first = NoteWriter.Write(s.NotesFolder, meta, "## Summary\nOne.", [], "fake", null);
        File.Delete(first);   // the page alone still claims the name
        var second = NoteWriter.Write(s.NotesFolder, meta, "## Summary\nTwo.", [], "fake", null);
        Assert.EndsWith(" (2).md", second);
        Assert.Contains("Two.", File.ReadAllText(NoteWriter.HtmlPath(second)));
        Assert.Contains("One.", File.ReadAllText(NoteWriter.HtmlPath(first)));
    }

    /// <summary>Scriberr-like: the first try uploads (job j-7) and then times out waiting; the job must come back on the retry.</summary>
    sealed class SlowJobBackend : IMeetingBackend, IResumableTranscription
    {
        public readonly List<string?> JobsGiven = [];
        public string Kind => "slow";
        public Task<TranscriptResult> TranscribeAsync(string audioPath, string title, TimeSpan duration, CancellationToken ct) =>
            throw new InvalidOperationException("the resumable overload should be used");
        public Task<TranscriptResult> TranscribeAsync(string audioPath, string title, TimeSpan duration, string? job,
            Action<TranscriptionJob>? started, CancellationToken ct)
        {
            JobsGiven.Add(job);
            if (job is null)
            {
                started?.Invoke(new TranscriptionJob("j-7", "https://slow/j-7"));
                throw new BackendException("job j-7 still 'processing' after 21 min");
            }
            return Task.FromResult(new TranscriptResult([new(0.2, 0.9, "S1", "Hi.")], job, "https://slow/" + job));
        }
        public Task NameSpeakersAsync(TranscriptResult t, IReadOnlyDictionary<string, string> names, CancellationToken ct) => Task.CompletedTask;
        public Task<string> SummarizeAsync(TranscriptResult t, string instructions, string labelled, CancellationToken ct) =>
            Task.FromResult("## Summary\nOk.");
        public void Dispose() { }
    }

    [Fact]
    public async Task A_transcription_that_timed_out_resumes_its_job_on_the_retry()
    {
        var s = Settings();
        var rec = MakeRecording(s);
        var backend = new SlowJobBackend();
        var processor = new MeetingProcessor(s, () => backend);

        var first = await processor.ProcessAsync(rec, CancellationToken.None);
        Assert.Equal(Stage.Mixed, first.Stage);
        Assert.Equal("j-7", first.JobId);                  // saved before the wait, so it survives the failure
        Assert.Equal("https://slow/j-7", first.WebUrl);

        var second = await processor.ProcessAsync(rec, CancellationToken.None);
        Assert.Equal(Stage.Done, second.Stage);
        Assert.Equal([null, "j-7"], backend.JobsGiven);
    }

    [Fact]
    public async Task A_failure_is_recorded_and_the_retry_resumes_without_redoing_work()
    {
        var s = Settings();
        var rec = MakeRecording(s);
        var backend = new FakeBackend { FailTranscribe = true };
        var processor = new MeetingProcessor(s, () => backend);

        var first = await processor.ProcessAsync(rec, CancellationToken.None);
        Assert.Equal(Stage.Mixed, first.Stage);           // mixing kept; transcription to retry
        Assert.Equal(1, first.Attempts);
        Assert.Contains("unreachable", first.LastError);
        Assert.NotNull(first.NextAttemptAt);
        Assert.True(File.Exists(rec.MixWav));

        Assert.Equal((1, true), (first.TimeOf(Step.Mix)!.Tries, first.TimeOf(Step.Mix)!.Took is not null));
        Assert.Equal((1, null), (first.TimeOf(Step.Transcribe)!.Tries, first.TimeOf(Step.Transcribe)!.FinishedAt)); // failed: no end

        backend.FailTranscribe = false;
        var second = await processor.ProcessAsync(rec, CancellationToken.None);
        Assert.Equal(Stage.Done, second.Stage);
        Assert.Equal(2, backend.Transcribes);
        Assert.Equal(1, backend.Summaries);

        // Every step's time is kept for the Recordings window, with how many tries it took.
        Assert.Equal(PipelineSteps.All, second.Steps.Select(t => t.Step));
        Assert.All(second.Steps, t => Assert.NotNull(t.Took));
        Assert.Equal([1, 2, 1, 1, 1], second.Steps.Select(t => t.Tries));
        Assert.Equal(PipelineSteps.All, rec.LoadState().Steps.Select(t => t.Step)); // and saved
    }

    [Fact]
    public async Task The_queue_announces_each_attempt_with_its_count_so_far()
    {
        var s = Settings();
        var rec = MakeRecording(s);
        var backend = new FakeBackend { FailTranscribe = true };
        var queue = new ProcessingQueue(() => s, () => new MeetingProcessor(s, () => backend));
        var events = new List<string>();
        queue.Starting += (_, st) => events.Add($"starting {st.Stage} attempts={st.Attempts}");
        queue.Processed += (_, st) => events.Add($"processed {st.Stage}");

        await queue.RunOnceAsync(CancellationToken.None);
        backend.FailTranscribe = false;
        var state = rec.LoadState();
        state.NextAttemptAt = null;   // don't wait for the retry delay
        rec.SaveState(state);
        await queue.RunOnceAsync(CancellationToken.None);

        Assert.Equal(["starting Recorded attempts=0", "processed Mixed", "starting Mixed attempts=1", "processed Done"], events);
    }

    [Fact]
    public async Task A_state_that_cant_be_read_doesnt_stop_the_other_meetings_notes()
    {
        // Every state.json was read outside any try: one that couldn't be failed the whole pass, every minute, for good.
        var s = Settings();
        var broken = MakeRecording(s);
        File.WriteAllText(broken.StateFile, "");
        var rec = RecordingFolder.CreateNew(s.ResolvedRecordingsFolder, new DateTimeOffset(2026, 10, 14, 9, 30, 0, TimeSpan.FromHours(-5)));
        foreach (var f in new[] { broken.MicWav, broken.OthersWav, broken.MetaFile }) File.Copy(f, Path.Combine(rec.Path, Path.GetFileName(f)));
        rec.SaveState(new ProcessingState());

        var queue = new ProcessingQueue(() => s, () => new MeetingProcessor(s, () => new FakeBackend()));
        await queue.RunOnceAsync(CancellationToken.None);
        Assert.Equal(Stage.Done, rec.LoadState().Stage);
        Assert.Equal(0, queue.Waiting);
        Assert.Throws<System.Text.Json.JsonException>(() => broken.LoadState()); // left as it was
    }

    [Fact]
    public void Old_audio_is_deleted_but_notes_and_state_stay()
    {
        var s = Settings();
        s.KeepAudioDays = 14;
        var rec = MakeRecording(s);
        rec.SaveState(new ProcessingState { Stage = Stage.Done, CompletedAt = DateTimeOffset.Now.AddDays(-15) });
        AudioCleanUp.Run(s, deleteMp3: _ => throw new InvalidOperationException("no MP3 goes: the setting is off"));
        Assert.False(File.Exists(rec.MicWav));
        Assert.True(File.Exists(rec.MetaFile));
        Assert.True(rec.LoadState().AudioDeleted);
    }

    [Fact]
    public void Retry_delays_back_off()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), MeetingProcessor.RetryDelay(1));
        Assert.Equal(TimeSpan.FromHours(1), MeetingProcessor.RetryDelay(9));
    }

    public void Dispose() => Directory.Delete(_root, true);
}

public class SettingsTests
{
    [Fact]
    public void Defaults_round_trip_and_need_a_server()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("llmmr-set").FullName, "settings.json");
        var first = SettingsStore.LoadOrCreate(path);
        Assert.True(File.Exists(path));
        Assert.Contains(first.Problems(), p => p.Contains("BaseUrl"));
        Assert.Equal(BackendTypes.OpenAi, first.Backend.Type);              // the shareable backend for new installs
        Assert.Equal(WatchRule.Defaults().Select(r => r.Name), first.Detection.Sources.Select(r => r.Name));

        first.Backend.OpenAi.TranscriptionBaseUrl = "https://gateway.example.com/v1";
        first.Summary.BaseUrl = "https://gateway.example.com/v1";
        first.Summary.Model = "some-model";
        SettingsStore.Save(first, path);
        var again = SettingsStore.LoadOrCreate(path);
        Assert.Empty(again.Problems());
        // Nothing personal is baked in: the only server in the file is the one set above.
        Assert.All(System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(path), @"https?://[^/""\s]+"),
            m => Assert.Equal("https://gateway.example.com", m.Value));
    }

    [Fact]
    public void Built_in_rules_added_later_join_an_older_list_and_a_ticked_off_one_stays_off()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("llmmr-set").FullName, "settings.json");
        File.WriteAllText(path, """
            { "detection": { "sources": [
              { "name": "Teams (Edge)", "appKeyContains": "#msedge.exe", "windowProcess": "msedge", "windowTitleContains": "Microsoft Teams" },
              { "name": "Zoom", "enabled": false, "appKeyContains": "#Zoom.exe" }
            ] } }
            """);
        var s = SettingsStore.LoadOrCreate(path);
        Assert.Equal(["Teams (Edge)", "Zoom", "Teams (Chrome)", "Teams (app)", "Teams (classic)", "Google Meet (Edge)",
                      "Google Meet (Chrome)", "Zoom (Edge)", "Zoom (Chrome)", "GoTo Meeting", "GoTo Meeting (classic)"], s.Detection.Sources.Select(r => r.Name));
        Assert.False(s.Detection.Sources.Single(r => r.Name == "Zoom").Enabled);
        Assert.Equal(["msedge"], s.Detection.Sources[0].AudioProcesses);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_allowed()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            {
              // my settings
              "displayName": "Paul",
              "backend": { "type": "openai", },
            }
            """);
        var s = SettingsStore.LoadOrCreate(path);
        Assert.Equal("openai", s.Backend.Type);
        Assert.Contains(s.Problems(), p => p.Contains("summary.model"));
    }

    [Fact]
    public void Note_file_names_are_safe()
    {
        var name = NoteWriter.FileName(new DateTimeOffset(2026, 1, 2, 3, 4, 0, TimeSpan.Zero), "a/b:c*d?");
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain(':', name);
        Assert.EndsWith(".md", name);
        Assert.EndsWith(" Design review - onboarding flow.md", NoteWriter.FileName(DateTimeOffset.Now, "Design review: onboarding flow"));
    }
}
