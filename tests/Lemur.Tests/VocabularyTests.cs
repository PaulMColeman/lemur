using System.Net;
using Lemur.Audio;
using Lemur.Backends;
using Lemur.Config;
using Lemur.Pipeline;
using Lemur.Transcripts;

namespace Lemur.Tests;

/// <summary>Words the transcription gets wrong ("Santavo" for "Zentavo"), fixed in new meetings and in notes already made.</summary>
public class VocabularyTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("llmmr-vocab").FullName;
    static readonly DateTimeOffset Start = new(2026, 10, 14, 13, 0, 0, TimeSpan.FromHours(-5));

    static readonly List<VocabularyEntry> Words =
    [
        new() { Term = "Zentavo", HeardAs = ["Santavo", "Zen Tavo"] },
        new() { Term = "QFN", HeardAs = ["qfn", "Q of N"] },
        new() { Term = "Epic" },
    ];

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Misheard_words_are_fixed_as_whole_words_in_any_case()
    {
        var v = new Vocabulary(Words);
        var text = v.Fix("SANTAVO's rollout: zen   Tavo, Santavos and qfn. QFN is fine. Q of N!", out var count);
        Assert.Equal("Zentavo's rollout: Zentavo, Santavos and QFN. QFN is fine. QFN!", text);
        Assert.Equal(4, count); // "QFN" already right isn't counted
        Assert.True(v.Mentions("talk to santavo"));
        Assert.False(v.Mentions("QFN and Zentavo"));
        Assert.Equal("nothing here", v.Fix("nothing here", out var none));
        Assert.Equal(0, none);
        Assert.False(Vocabulary.Empty.CanFix);
        Assert.False(new Vocabulary([new VocabularyEntry { Term = "Epic" }]).CanFix); // spelled for the model only
    }

    [Fact]
    public void The_summary_model_is_told_the_spellings_of_the_words_said_and_a_whisper_server_all_of_them()
    {
        var v = new Vocabulary([.. Words, new() { Term = "Northwind Traders" }]);
        Assert.Equal(["Zentavo", "QFN", "Epic", "Northwind Traders"], v.Terms);
        Assert.Equal("Zentavo, QFN, Epic, Northwind Traders.", v.Hint());

        const string said = "[00:00:01] Paul: The qfn box runs Zentavo's jobs for northwind\n  traders. Epicurean.";
        Assert.Equal("These words and names are in the transcript. Spell them exactly like this: Zentavo, QFN, Northwind Traders.",
            v.Glossary(said)); // whole words in any case, a phrase across a line break; not "Epic" inside "Epicurean"
        // A term nobody said is never offered: the model listed such terms under Concepts and terms, with made-up meanings.
        Assert.Null(v.Glossary("[00:00:01] Paul: Nothing from the list was said."));
        Assert.Null(Vocabulary.Empty.Glossary(said));

        var day = DateTimeOffset.Now;
        Assert.EndsWith("\n\n" + v.Glossary(said), SummaryPrompt.For("Paul", null, day, v.Glossary(said)));
        Assert.Equal(SummaryPrompt.For("Paul", null, day), SummaryPrompt.For("Paul", null, day, null));
    }

    [Fact]
    public void Adding_a_correction_moves_a_misheard_word_and_problems_are_caught()
    {
        var list = new List<VocabularyEntry> { new() { Term = "Sentavo", HeardAs = ["Santavo"] } };
        Vocabulary.Add(list, "Zentavo", "Santavo");   // the latest correction wins
        Vocabulary.Add(list, "zentavo", "Zen Tavo"); // the same term, whatever its case
        Assert.Equal(["Sentavo", "Zentavo"], list.Select(e => e.Term));
        Assert.Empty(list[0].HeardAs);
        Assert.Equal(["Santavo", "Zen Tavo"], list[1].HeardAs);
        Assert.True(Vocabulary.Remove(list, "SENTAVO"));
        Assert.Single(list);

        var added = Vocabulary.Added([new VocabularyEntry { Term = "Zentavo", HeardAs = ["Santavo"] }], list);
        Assert.Equal("Zentavo", added.Single().Term);
        Assert.Equal(["Zen Tavo"], added.Single().HeardAs);

        Assert.Single(Vocabulary.Problems([new() { Term = "Lemur" }, new() { Term = "Lemma", HeardAs = ["Lemur"] }]));
        Assert.Throws<ArgumentException>(() => Vocabulary.Add(list, "  "));
    }

    [Fact]
    public void Correcting_a_correction_replaces_the_old_spelling()
    {
        // Paul, 2026-10-14: a term turned out to be spelled differently; Fix a word refused it and said it hadn't.
        var list = new List<VocabularyEntry> { new() { Term = "Zentavo", HeardAs = ["Santavo"] }, new() { Term = "Moravia", HeardAs = ["More of ya"] } };
        Assert.Null(Vocabulary.WhyNot(list, "Moravya", "Moravia"));
        Vocabulary.Add(list, "Moravya", "Moravia");
        Assert.Equal(["Zentavo", "Moravya"], list.Select(e => e.Term));
        Assert.Equal(["More of ya", "Moravia"], list[1].HeardAs);
        Assert.Empty(Vocabulary.Problems(list));
        Assert.Equal("Moravya was here", new Vocabulary(list).Fix("More of ya was here"));

        Assert.Contains("comma", Vocabulary.WhyNot(list, "Zentavo", "Zen, Tavo"));
        Assert.Contains("listed as a correct spelling", Vocabulary.WhyNot(list, "Santavo", "Zentavo fixes")); // a term misheard as another's
        Assert.Equal(2, list.Count); // asking changes nothing
    }

    // ---- in the pipeline ----

    sealed class WordsBackend(string mine, string theirs, string summary) : IMeetingBackend
    {
        public string? Instructions, Transcript;
        public string Kind => "fake";
        public Task<TranscriptResult> TranscribeAsync(string audioPath, string title, TimeSpan duration, CancellationToken ct) =>
            Task.FromResult(new TranscriptResult([new(0.2, 0.9, "S1", mine), new(1.1, 1.9, "S2", theirs)], "j1", "https://fake/j1"));
        public Task NameSpeakersAsync(TranscriptResult t, IReadOnlyDictionary<string, string> names, CancellationToken ct) => Task.CompletedTask;
        public Task<string> SummarizeAsync(TranscriptResult t, string instructions, string labelled, CancellationToken ct)
        {
            Instructions = instructions;
            Transcript = labelled;
            return Task.FromResult(summary);
        }
        public void Dispose() { }
    }

    const string Reply = "Title: Santavo migration\nAbout: Moving Santavo to the new server.\n\n## Summary\nThe team moved Santavo to the new server on Friday.";

    AppSettings Settings(List<VocabularyEntry>? words = null) => new()
    {
        DisplayName = "Paul",
        RecordingsFolder = Path.Combine(_root, "rec"),
        NotesFolder = Path.Combine(_root, "notes"),
        Backend = { Type = BackendTypes.Scriberr, Scriberr = { BaseUrl = "https://x" } },
        Vocabulary = words ?? [],
    };

    RecordingFolder Recording(AppSettings s, string title = "")
    {
        var rec = RecordingFolder.CreateNew(s.ResolvedRecordingsFolder, Start);
        int rate = AnchoredTrackWriter.SampleRate;
        var mic = new float[2 * rate];
        var others = new float[2 * rate];
        for (int i = 0; i < rate; i++) mic[i] = 0.2f * MathF.Sin(i * 0.2f);
        for (int i = rate; i < 2 * rate; i++) others[i] = 0.2f * MathF.Sin(i * 0.3f);
        using (var a = new WavSampleSink(rec.MicWav)) a.Write(mic);
        using (var b = new WavSampleSink(rec.OthersWav)) b.Write(others);
        rec.SaveMeta(new RecordingMeta { Title = title, Source = "GoTo", StartedAt = Start, EndedAt = Start.AddSeconds(2), DurationSeconds = 2 });
        rec.SaveState(new ProcessingState());
        return rec;
    }

    async Task<(RecordingFolder Rec, string Note)> Made(AppSettings s, string title = "")
    {
        var rec = Recording(s, title);
        var backend = new WordsBackend("We moved Santavo to the new server.", "Is zen  Tavo live yet?", Reply);
        var state = await new MeetingProcessor(s, () => backend).ProcessAsync(rec, CancellationToken.None);
        Assert.Equal(Stage.Done, state.Stage);
        return (rec, state.NotePath!);
    }

    [Fact]
    public async Task A_new_meeting_has_the_words_right_everywhere()
    {
        var s = Settings(Words);
        var rec = Recording(s);
        var backend = new WordsBackend("We moved Santavo to the new server.", "Is zen  Tavo live yet?", Reply);
        var state = await new MeetingProcessor(s, () => backend).ProcessAsync(rec, CancellationToken.None);

        Assert.Contains("Paul: We moved Zentavo to the new server.", backend.Transcript); // fixed before the summary
        Assert.Contains("Is Zentavo live yet?", backend.Transcript);
        Assert.EndsWith("Spell them exactly like this: Zentavo.", backend.Instructions); // QFN and Epic weren't said
        Assert.Equal(("Zentavo migration", "Moving Zentavo to the new server."), (rec.LoadMeta().Title, rec.LoadMeta().Description));
        var note = File.ReadAllText(state.NotePath!);
        Assert.DoesNotContain("Santavo", note);
        Assert.Contains("The team moved Zentavo", note);
        Assert.EndsWith("1300 Zentavo migration.md", state.NotePath);
    }

    [Fact]
    public async Task Notes_already_made_are_fixed_and_renamed_when_lemur_chose_the_title()
    {
        var s = Settings();
        var (rec, before) = await Made(s);
        Assert.EndsWith("1300 Santavo migration.md", before);
        var v = new Vocabulary(Words);
        Assert.True(MeetingFixes.Needs(rec, v));

        Assert.True(MeetingFixes.Apply(rec, v, "Paul") > 0);

        var state = rec.LoadState();
        Assert.EndsWith("1300 Zentavo migration.md", state.NotePath);
        Assert.False(File.Exists(before));
        var note = File.ReadAllText(state.NotePath!);
        Assert.DoesNotContain("Santavo", note);
        Assert.Contains("**[00:00:00] Paul:** We moved Zentavo to the new server.", note);
        Assert.Contains("The team moved Zentavo", note);
        Assert.DoesNotContain("Santavo", File.ReadAllText(NoteWriter.HtmlPath(state.NotePath!)));
        Assert.Equal(NoteWriter.Hash(state.NotePath!), state.NoteHash); // still Lemur's own note
        Assert.Equal("Moving Zentavo to the new server.", rec.LoadMeta().Description);
        Assert.False(MeetingFixes.Needs(rec, v));
        Assert.Equal(0, MeetingFixes.Apply(rec, v, "Paul")); // nothing left to do
    }

    [Fact]
    public async Task An_edited_note_keeps_its_edits_and_a_users_title_stays()
    {
        var s = Settings();
        var (rec, note) = await Made(s, "");
        MeetingRename.Apply(rec, "Santavo sync"); // the user's own title
        note = rec.LoadState().NotePath!;
        File.AppendAllText(note, "\nMy own line: call Dana about Santavo.\n");
        var hash = rec.LoadState().NoteHash;

        MeetingFixes.Apply(rec, new Vocabulary(Words), "Paul");

        var text = File.ReadAllText(note);
        Assert.Contains("My own line: call Dana about Zentavo.", text);
        Assert.Contains("We moved Zentavo to the new server.", text);
        Assert.Equal(hash, rec.LoadState().NoteHash); // still "edited"
        Assert.Equal("Santavo sync", rec.LoadMeta().Title);
        Assert.Equal(note, rec.LoadState().NotePath);
        // The user's title, and the file names and links that carry it, are left exactly as they were.
        Assert.Contains("title: \"Santavo sync\"", text);
        Assert.Contains("# Santavo sync", text);
        var mp3 = Path.GetFileName(NoteWriter.AudioPath(note));
        Assert.Contains($"[recording](<{mp3}>)", text);
        Assert.True(File.Exists(NoteWriter.AudioPath(note)));
        Assert.Contains("description: \"Moving Zentavo to the new server.\"", text);
        // ...and nothing is left to do, so it isn't "fixed" again on every pass.
        Assert.False(MeetingFixes.Needs(rec, new Vocabulary(Words)));
        Assert.Equal(0, MeetingFixes.Apply(rec, new Vocabulary(Words), "Paul"));
    }

    [Fact]
    public void A_word_inside_a_term_is_left_alone()
    {
        var v = new Vocabulary([new() { Term = "Kubernetes", HeardAs = ["Cooper"] }, new() { Term = "Alice Cooper" }]);
        Assert.Equal("Alice Cooper sang; Kubernetes runs.", v.Fix("Alice Cooper sang; Cooper runs."));
        var inc = new Vocabulary([new() { Term = "Zentavo Inc", HeardAs = ["Zentavo"] }]);
        var once = inc.Fix("Zentavo and Zentavo Inc");
        Assert.Equal("Zentavo Inc and Zentavo Inc", once);
        Assert.Equal(once, inc.Fix(once)); // again: nothing more
        Assert.False(inc.Mentions(once));
    }

    [Fact]
    public void Odd_input_never_breaks_a_meeting()
    {
        // The Kelvin sign is a K ignoring case the Unicode way, but not to the lookup: it stays as it is.
        var v = new Vocabulary([new() { Term = "Kubernetes", HeardAs = ["kube"] }]);
        Assert.Equal("\u212Aube and Kubernetes", v.Fix("\u212Aube and kube"));
        Assert.Equal("x", new Vocabulary([new VocabularyEntry { Term = "X", HeardAs = null! }]).Fix("x"));
        var path = Path.Combine(_root, "odd.json");
        File.WriteAllText(path, """{ "vocabulary": [ { "term": "Zentavo", "heardAs": null } ] }""");
        Assert.Empty(SettingsStore.LoadOrCreate(path).Vocabulary[0].HeardAs);

        var list = new List<VocabularyEntry>();
        Assert.Throws<ArgumentException>(() => Vocabulary.Add(list, "Zentavo", "Zen, Tavo")); // commas separate them
        Assert.Throws<ArgumentException>(() => Vocabulary.Add(list, "Zentavo", "-"));
        Assert.Equal(2, Vocabulary.Problems([new() { Term = "Zentavo", HeardAs = ["Zen; Tavo", "--"] }]).Count());
        Assert.False(new Vocabulary([new() { Term = "Zentavo", HeardAs = ["-"] }]).CanFix);
    }

    [Fact]
    public async Task A_misheard_about_doesnt_swallow_the_about_line()
    {
        var s = Settings([new() { Term = "Abbott", HeardAs = ["about"] }]);
        var rec = Recording(s);
        var backend = new WordsBackend("Hello.", "Hi.", "Title: Budget review\nAbout: The budget is agreed.\n\n## Summary\nWe talked about the budget.");
        await new MeetingProcessor(s, () => backend).ProcessAsync(rec, CancellationToken.None);
        Assert.Equal(("Budget review", "The budget is agreed."), (rec.LoadMeta().Title, rec.LoadMeta().Description));
        Assert.StartsWith("## Summary\nWe talked Abbott the budget.", File.ReadAllText(rec.SummaryFile));
    }

    [Fact]
    public async Task A_meeting_past_its_transcript_still_gets_a_word_added_later()
    {
        var s = Settings();
        var (rec, _) = await Made(s);
        rec.UpdateState(st => st.Stage = Stage.Summarized); // as if its note were still to be written
        s.Vocabulary = Words;
        var state = await new MeetingProcessor(s, () => new WordsBackend("", "", "")).ProcessAsync(rec, CancellationToken.None);
        Assert.DoesNotContain("Santavo", File.ReadAllText(state.NotePath!));
        Assert.Equal(("Zentavo migration", "Moving Zentavo to the new server."), (rec.LoadMeta().Title, rec.LoadMeta().Description));
        Assert.DoesNotContain(rec.Read<List<LabelledSegment>>(rec.LabelledFile)!, x => x.Text.Contains("Santavo"));
    }

    [Fact]
    public async Task A_rewrite_that_cant_finish_leaves_the_note_as_it_was()
    {
        var s = Settings();
        var (rec, note) = await Made(s);
        var before = File.ReadAllText(note);
        using (new FileStream(NoteWriter.HtmlPath(note), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.ThrowsAny<IOException>(() => MeetingFixes.Apply(rec, new Vocabulary([new() { Term = "Zentavo", HeardAs = ["Santavo"] }]), "Paul"));
        Assert.Equal(before, File.ReadAllText(note)); // the note didn't change without its page
        Assert.Empty(Directory.GetFiles(s.NotesFolder, "*.lemur-tmp"));
    }

    [Fact]
    public async Task Every_meeting_that_needs_it_is_fixed_at_once()
    {
        var s = Settings();
        await Made(s);
        await Made(s, "Weekly sync");
        var (done, failed) = MeetingFixes.ApplyAll(s.ResolvedRecordingsFolder, new Vocabulary(Words), "Paul");
        Assert.Equal((2, 0), (done, failed));
        Assert.Equal((0, 0), MeetingFixes.ApplyAll(s.ResolvedRecordingsFolder, new Vocabulary(Words), "Paul"));
    }

    // ---- hints for the transcription ----

    [Fact]
    public async Task A_whisper_server_gets_the_words_as_its_prompt()
    {
        var server = new FakeServer((req, _) => (HttpStatusCode.OK, """{"text":"x","segments":[{"start":0,"end":1,"text":"Hi","speaker":"A"}]}"""));
        var stt = new OpenAiSettings { TranscriptionBaseUrl = "https://wlk.test/v1" };
        using var backend = new OpenAiBackend(stt, null, new SummarySettings(), null, server, hint: "Zentavo, QFN.");
        var path = Path.GetTempFileName();
        await backend.TranscribeAsync(path, "t", TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.EndsWith(",prompt", server.Requests[0].Body);

        stt.ExtraFields["prompt"] = "my own";
        using var own = new OpenAiBackend(stt, null, new SummarySettings(), null, server, hint: "Zentavo, QFN.");
        await own.TranscribeAsync(path, "t", TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Single(server.Requests[1].Body.Split(','), f => f == "prompt"); // the user's own prompt, not both

        stt.ExtraFields["prompt"] = ""; // a server that refuses the field: send none
        using var none = new OpenAiBackend(stt, null, new SummarySettings(), null, server, hint: "Zentavo, QFN.");
        await none.TranscribeAsync(path, "t", TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.DoesNotContain("prompt", server.Requests[2].Body);
        File.Delete(path);
    }

    [Fact]
    public void A_scriberr_whisper_profile_gets_them_as_its_initial_prompt_but_parakeet_takes_none()
    {
        Assert.Contains("\"initial_prompt\":\"Zentavo.\"", ScriberrBackend.WithHint("""{"model_family":"whisper","hf_token":"x"}""", "Zentavo."));
        Assert.Contains("\"initial_prompt\":\"Zentavo.\"", ScriberrBackend.WithHint("""{"model":"small"}""", "Zentavo.")); // Scriberr's default family
        const string parakeet = """{"model_family":"nvidia_parakeet"}""";
        Assert.Equal(parakeet, ScriberrBackend.WithHint(parakeet, "Zentavo."));
        const string own = """{"model_family":"whisper","initial_prompt":"mine"}""";
        Assert.Equal(own, ScriberrBackend.WithHint(own, "Zentavo."));
        Assert.Equal("not json", ScriberrBackend.WithHint("not json", "Zentavo."));
    }

    [Fact]
    public void The_vocabulary_round_trips_through_settings()
    {
        var path = Path.Combine(_root, "settings.json");
        SettingsStore.Save(Settings(Words), path);
        Assert.Contains("\"heardAs\"", File.ReadAllText(path));
        var back = SettingsStore.LoadOrCreate(path);
        Assert.Equal(["Santavo", "Zen Tavo"], back.Vocabulary[0].HeardAs);
        Assert.Equal("Epic", back.Vocabulary[2].Term);
    }
}
