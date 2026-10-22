using System.Text.Json;
using Lemur.Audio;
using Lemur.Config;
using Lemur.Pipeline;
using Lemur.Speakers;
using Lemur.Transcripts;

namespace Lemur.Tests;

/// <summary>The meeting file beside the note: what it holds, and that it follows the note through every change.</summary>
[Collection("Log echo")]
public class MeetingFileTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("llmmr-file").FullName;

    /// <summary>Wednesday 2026-10-14 09:30 on this PC's clock, so the meeting's day is the same wherever the tests run.</summary>
    static readonly DateTimeOffset Start = new(new DateTime(2026, 10, 14, 9, 30, 0, DateTimeKind.Local));

    const string Reply = """
        Title: Onboarding review
        About: The calendar step becomes optional.

        ## Summary
        We reviewed the onboarding flow.

        ## Action items
        - [ ] Paul - send Speaker A the deck - Fri 2026-10-16 [00:00:00]
        - [ ] Speaker A - say thanks [00:00:01]
        - [x] Speaker A - check the numbers - next week [00:59:00]

        ## Decisions
        - The calendar step becomes optional.

        ## Open questions
        None.
        """;

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

    /// <summary>Two seconds: you on the mic, then someone on the call.</summary>
    RecordingFolder Recording(AppSettings s, string title = "Design review")
    {
        var rec = RecordingFolder.CreateNew(s.ResolvedRecordingsFolder, Start);
        int rate = AnchoredTrackWriter.SampleRate;
        var mic = new float[2 * rate];
        var others = new float[2 * rate];
        for (int i = 0; i < rate; i++) mic[i] = 0.2f * MathF.Sin(i * 0.2f);
        for (int i = rate; i < 2 * rate; i++) others[i] = 0.2f * MathF.Sin(i * 0.3f);
        for (int i = 0; i < 2 * rate; i++) mic[i] += 0.0005f * MathF.Sin(i * 1.7f);
        using (var a = new WavSampleSink(rec.MicWav)) a.Write(mic);
        using (var b = new WavSampleSink(rec.OthersWav)) b.Write(others);
        rec.SaveMeta(new RecordingMeta { Title = title, Source = "Teams (Edge)", StartedAt = Start, EndedAt = Start.AddSeconds(2), DurationSeconds = 2 });
        rec.SaveState(new ProcessingState());
        return rec;
    }

    /// <summary>Untitled, so the summary's "Title:" line names it.</summary>
    async Task<(RecordingFolder Rec, string Note, FakeBackend Backend)> Finished(AppSettings s, string summary = Reply)
    {
        var rec = Recording(s, "");
        var backend = new FakeBackend { Summary = summary };
        var state = await new MeetingProcessor(s, () => backend).ProcessAsync(rec, CancellationToken.None);
        Assert.Equal(Stage.Done, state.Stage);
        return (rec, state.NotePath!, backend);
    }

    static MeetingFile.Content Load(string note) =>
        JsonSerializer.Deserialize<MeetingFile.Content>(File.ReadAllText(MeetingFile.PathFor(note)), Json.Options)!;

    /// <summary>The items' ids without the meeting's: "/1", "/2"…</summary>
    static List<string> Ids(RecordingFolder rec, string note) => Load(note).ActionItems.Select(i => i.Id[rec.Name.Length..]).ToList();

    [Fact]
    public async Task The_meeting_file_is_written_with_the_note()
    {
        var s = Settings();
        var (rec, note, backend) = await Finished(s);
        Assert.Contains("recorded this meeting on Wednesday 2026-10-14", backend.LastInstructions); // the model can work out "Friday"
        Assert.EndsWith("2026-10-14 0930 Onboarding review.json", MeetingFile.PathFor(note));

        var file = Load(note);
        Assert.Equal(("lemur.meeting/1", rec.Name, 16), (file.Format, file.Id, file.Revision!.Length));
        Assert.NotNull(file.RevisedAt);
        Assert.Equal(("Onboarding review", "The calendar step becomes optional.", "Teams (Edge)", "Paul", 2.0),
            (file.Title, file.Description, file.Source, file.RecordedBy, file.DurationSeconds));
        Assert.Equal((Start, Start.AddSeconds(2)), (file.StartedAt, file.EndedAt));
        Assert.Equal([new MeetingFile.Person("Paul", true, 1, true), new MeetingFile.Person("Speaker A", false, 1, false)], file.People);
        Assert.Equal("We reviewed the onboarding flow.", file.Summary);
        Assert.True(file.ActionItemsFound);
        var link = "lemur://note/" + rec.Name;
        Assert.Equal(
        [
            new MeetingFile.Item(rec.Name + "/1", "send Speaker A the deck", "Paul", true, "2026-10-16", "Fri 2026-10-16", 0, link + "?t=0", false),
            new MeetingFile.Item(rec.Name + "/2", "say thanks", "Speaker A", false, null, null, 1, link + "?t=1", false),
            new MeetingFile.Item(rec.Name + "/3", "check the numbers", "Speaker A", false, null, "next week", null, link, true), // 59 min: past the end
        ], file.ActionItems);
        Assert.Equal(link, file.Link);
        Assert.Equal(["The calendar step becomes optional."], file.Decisions);
        Assert.Empty(file.OpenQuestions);
        var name = Path.GetFileNameWithoutExtension(note);
        Assert.Equal(new MeetingFile.Notes(name + ".md", name + ".html", name + ".mp3"), file.Notes);
        Assert.StartsWith("## Summary\nWe reviewed", file.SummaryMarkdown);
        Assert.Equal([new MeetingFile.Line(0.2, 0.9, "Paul", true, "I'll write it up."), new MeetingFile.Line(1.1, 1.9, "Speaker A", false, "Thanks!")],
            file.Transcript);

        var json = File.ReadAllText(MeetingFile.PathFor(note));
        Assert.Contains("\"actionItemsFound\": true", json);   // Lemur's JSON: camelCase
        Assert.Contains("\"openQuestions\": []", json);
        var state = rec.LoadState();
        Assert.Equal((3, 4), (state.ActionItemNumbers.Count, state.NextActionItem));
        Assert.NotNull(state.MeetingFileStamp);
        Assert.Empty(Directory.GetFiles(s.NotesFolder, "*.lemur-tmp"));
    }

    [Fact]
    public async Task The_revision_and_its_time_stay_until_something_changes()
    {
        var s = Settings();
        var (rec, note, _) = await Finished(s);
        var path = MeetingFile.PathFor(note);
        var first = Load(note);
        var written = File.GetLastWriteTimeUtc(path);

        MeetingFile.Write(rec, "Paul", now: DateTimeOffset.Now.AddHours(1));
        Assert.Equal((first.Revision, first.RevisedAt), (Load(note).Revision, Load(note).RevisedAt));
        Assert.Equal(written, File.GetLastWriteTimeUtc(path)); // the same file: not touched

        rec.UpdateMeta(m => m.Description = "Something else came out of it.");
        var later = new DateTimeOffset(2026, 10, 15, 8, 0, 0, 500, TimeSpan.FromHours(-5));
        MeetingFile.Write(rec, "Paul", now: later);
        var second = Load(note);
        Assert.NotEqual(first.Revision, second.Revision);
        Assert.Equal(later.AddMilliseconds(-500), second.RevisedAt);
    }

    [Fact]
    public async Task Naming_a_speaker_rewrites_it_and_the_items_keep_their_ids()
    {
        var s = Settings();
        var (rec, note, _) = await Finished(s);
        var namer = new SpeakerNamer(s);

        await namer.ApplyAsync(rec, [new NameChange("Speaker A", "Dana Whitfield")], CancellationToken.None);
        var file = Load(note);
        Assert.Equal(["Paul", "Dana Whitfield", "Dana Whitfield"], file.ActionItems.Select(i => i.Owner));
        Assert.Equal("send Dana Whitfield the deck", file.ActionItems[0].Text);
        Assert.Equal([rec.Name + "/1", rec.Name + "/2", rec.Name + "/3"], file.ActionItems.Select(i => i.Id));
        Assert.Equal(new MeetingFile.Person("Dana Whitfield", false, 1, true), file.People[1]);

        // That voice was you all along: its items are yours now, under the same ids.
        await namer.ApplyAsync(rec, [new NameChange("Dana Whitfield", "Paul")], CancellationToken.None);
        file = Load(note);
        Assert.Equal([true, true, true], file.ActionItems.Select(i => i.OwnerIsMe));
        Assert.Equal([rec.Name + "/1", rec.Name + "/2", rec.Name + "/3"], file.ActionItems.Select(i => i.Id));
        Assert.Equal(4, rec.LoadState().NextActionItem);
    }

    [Fact]
    public async Task Items_with_the_same_text_keep_their_own_ids_and_a_fixed_word_keeps_one()
    {
        var s = Settings();
        var (rec, note, _) = await Finished(s, """
            ## Summary
            Two decks.

            ## Action items
            - [ ] Paul - send the deck
            - [ ] Speaker A - send the deck
            - [ ] Paul - move the Santavo cluster to the new rack
            """);
        Assert.Equal(["/1", "/2", "/3"], Load(note).ActionItems.Select(i => i.Id[rec.Name.Length..]));

        // The second "send the deck" goes first: still occurrence 1 and 2 of that text, so the ids follow the order.
        File.WriteAllText(rec.SummaryFile, File.ReadAllText(rec.SummaryFile).Replace("Paul - send the deck\n- [ ] Speaker A", "Speaker A - send the deck\n- [ ] Paul"));
        MeetingFile.Write(rec, "Paul");
        Assert.Equal(["Speaker A", "Paul"], Load(note).ActionItems.Take(2).Select(i => i.Owner));
        Assert.Equal(["/1", "/2", "/3"], Load(note).ActionItems.Select(i => i.Id[rec.Name.Length..]));

        // Fix a word: the item it was in keeps its id.
        var vocabulary = new Vocabulary([new VocabularyEntry { Term = "Zentavo", HeardAs = ["Santavo"] }]);
        Assert.True(MeetingFixes.Apply(rec, vocabulary, "Paul") > 0);
        var fixedItem = Load(note).ActionItems[2];
        Assert.Equal(("move the Zentavo cluster to the new rack", rec.Name + "/3"), (fixedItem.Text, fixedItem.Id));

        // A new item takes the next number; a number once given is never given again.
        File.AppendAllText(rec.SummaryFile, "\n- [ ] Paul - book the room");
        MeetingFile.Write(rec, "Paul");
        Assert.Equal(rec.Name + "/4", Load(note).ActionItems[3].Id);
        File.WriteAllText(rec.SummaryFile, "## Summary\nx\n\n## Action items\n- [ ] Paul - order lunch");
        MeetingFile.Write(rec, "Paul");
        Assert.Equal(rec.Name + "/5", Assert.Single(Load(note).ActionItems).Id);
    }

    [Fact]
    public async Task Naming_someone_the_summary_already_mentions_keeps_the_ids()
    {
        var s = Settings();
        var (rec, note, _) = await Finished(s, """
            ## Summary
            Pricing.

            ## Action items
            - [ ] Paul - send Dana Whitfield the pricing sheet
            - [ ] Paul - call Dana
            - [ ] Speaker A - book the room
            """);
        Assert.Equal(["/1", "/2", "/3"], Ids(rec, note));

        // Until Speaker A is named Dana, "Dana" is just a word; then it is taken out of the old text and the new alike.
        await new SpeakerNamer(s).ApplyAsync(rec, [new NameChange("Speaker A", "Dana Whitfield")], CancellationToken.None);
        Assert.Equal(["/1", "/2", "/3"], Ids(rec, note));
        Assert.Equal("Dana Whitfield", Load(note).ActionItems[2].Owner);
        Assert.Equal(4, rec.LoadState().NextActionItem);
    }

    [Fact]
    public async Task A_phrase_fixed_into_one_word_keeps_its_id_and_the_fix_is_remembered()
    {
        var s = Settings();
        var (rec, note, _) = await Finished(s, """
            ## Summary
            Racks.

            ## Action items
            - [ ] Paul - move the Zen Tavo cluster to the new rack
            - [ ] Paul - label the Santavo cables
            """);
        var vocabulary = new Vocabulary([new VocabularyEntry { Term = "Zentavo", HeardAs = ["Zen Tavo", "Santavo"] }]);
        Assert.True(MeetingFixes.Apply(rec, vocabulary, "Paul") > 0);
        Assert.Equal(["move the Zentavo cluster to the new rack", "label the Zentavo cables"], Load(note).ActionItems.Select(i => i.Text));
        Assert.Equal(["/1", "/2"], Ids(rec, note));

        // Fixed in this meeting only (Fix a word, not remembered): what is written later, without that word, keeps the ids.
        await new SpeakerNamer(s).ApplyAsync(rec, [new NameChange("Speaker A", "Dana Whitfield")], CancellationToken.None);
        Assert.Equal(["/1", "/2"], Ids(rec, note));
    }

    [Fact]
    public async Task A_reworded_item_is_a_new_item_and_never_takes_another_ones_id()
    {
        var s = Settings();
        var (rec, note, _) = await Finished(s, """
            ## Summary
            Vendors.

            ## Action items
            - [ ] Paul - renew the vendor contract
            - [ ] Paul - email the Q3 report
            """);
        // As a remake may leave it: one item reworded; one dropped, and another much like it new.
        File.WriteAllText(rec.SummaryFile, "## Summary\nVendors.\n\n## Action items\n- [ ] Paul - cancel the vendor contract\n- [ ] Paul - email the Q5 report\n");
        MeetingFile.Write(rec, "Paul");
        Assert.Equal(["/3", "/4"], Ids(rec, note));
    }

    [Theory]
    [InlineData("\"not a table\"", "/4", "/5", "/6")]
    [InlineData("[{\"text\": 5, \"number\": \"one\"}, null]", "/4", "/5", "/6")]
    [InlineData("[null, {\"number\": 9}]", "/4", "/5", "/6")]
    // As the first meeting files kept it, names already taken out: still the same items.
    [InlineData("[{\"text\": \"send the deck\", \"occurrence\": 1, \"number\": 1}, {\"text\": \"say thanks\", \"occurrence\": 1, \"number\": 2}, " +
                "{\"text\": \"check the numbers\", \"occurrence\": 1, \"number\": 3}]", "/1", "/2", "/3")]
    public async Task A_table_of_ids_that_cant_be_read_starts_again_without_a_crash(string table, string first, string second, string third)
    {
        var s = Settings();
        var (rec, note, _) = await Finished(s);
        var state = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(rec.StateFile))!;
        state["actionItemNumbers"] = System.Text.Json.Nodes.JsonNode.Parse(table);
        File.WriteAllText(rec.StateFile, state.ToJsonString());

        Assert.Equal(Stage.Done, rec.LoadState().Stage);
        MeetingFile.Write(rec, "Paul");
        Assert.Equal([first, second, third], Ids(rec, note)); // a number once given isn't given again
    }

    [Fact]
    public async Task A_new_display_name_brings_the_files_up_to_date()
    {
        var s = Settings();
        var (rec, note, _) = await Finished(s);
        Assert.Equal(0, MeetingFile.RefreshAll(s.ResolvedRecordingsFolder, "Paul"));

        Assert.Equal(1, MeetingFile.RefreshAll(s.ResolvedRecordingsFolder, "Paul Smith"));
        var file = Load(note);
        Assert.Equal("Paul Smith", file.RecordedBy);
        Assert.True(file.ActionItems[0].OwnerIsMe); // "Paul" is still you
        Assert.Equal(0, MeetingFile.RefreshAll(s.ResolvedRecordingsFolder, "Paul Smith"));
    }

    [Fact]
    public async Task An_unexpected_error_is_logged_as_a_bug_and_not_tried_again_until_the_files_change()
    {
        var s = Settings();
        var (rec, note, _) = await Finished(s);
        File.Delete(MeetingFile.PathFor(note));
        Assert.False(File.Exists(rec.IdentifiedFile)); // speakers weren't named by voice
        File.WriteAllText(rec.IdentifiedFile, "{\"speakers\": null}"); // nothing Lemur writes: Build trips over it

        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        Lemur.Logging.Log.Echo = lines.Enqueue;
        try
        {
            Assert.False(MeetingFile.TryWrite(rec, "Paul"));
        }
        finally
        {
            Lemur.Logging.Log.Echo = null;
        }
        var line = Assert.Single(lines, l => l.Contains(rec.Name) && l.Contains("meeting file"));
        Assert.Contains("ERROR", line);
        Assert.Matches(@"ArgumentNullException 0x[0-9A-F]{8}", line);
        Assert.DoesNotContain("Onboarding", string.Concat(lines));
        Assert.StartsWith("none:", rec.LoadState().MeetingFileStamp);
        Assert.Null(rec.LoadState().MeetingFileRetryAt);
        Assert.Equal(0, MeetingFile.RefreshAll(s.ResolvedRecordingsFolder, "Paul", now: DateTimeOffset.Now.AddHours(1)));

        File.Delete(rec.IdentifiedFile);
        Assert.Equal(1, MeetingFile.RefreshAll(s.ResolvedRecordingsFolder, "Paul"));
        Assert.True(File.Exists(MeetingFile.PathFor(note)));
    }

    [Fact]
    public async Task A_meeting_file_held_open_elsewhere_doesnt_stop_a_rename()
    {
        var s = Settings();
        var (rec, note, _) = await Finished(s);
        var old = MeetingFile.PathFor(note);

        string renamed;
        using (new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.None)) // a sync tool or an indexer reading it
            renamed = MeetingRename.Apply(rec, "Budget review")!;
        Assert.EndsWith("2026-10-14 0930 Budget review.md", renamed);
        Assert.False(File.Exists(note));
        Assert.True(File.Exists(NoteWriter.HtmlPath(renamed)));
        Assert.True(File.Exists(NoteWriter.AudioPath(renamed)));
        Assert.Equal(("Budget review", renamed), (rec.LoadMeta().Title, rec.LoadState().NotePath));
        Assert.True(File.Exists(old)); // couldn't be moved or deleted: left where it was

        // The pass writes it under the note's new name.
        Assert.Equal(1, MeetingFile.RefreshAll(s.ResolvedRecordingsFolder, "Paul"));
        Assert.Equal(("Budget review", Path.GetFileName(renamed)), (Load(renamed).Title, Load(renamed).Notes!.Markdown));
    }

    [Fact]
    public async Task A_custom_prompt_without_action_items_says_so()
    {
        var s = Settings();
        var (_, note, _) = await Finished(s, "## Overview\nShort call.\n\nNext: Paul writes it up.");
        var file = Load(note);
        Assert.False(file.ActionItemsFound);
        Assert.Empty(file.ActionItems);
        Assert.Null(file.Summary);
        Assert.Equal("## Overview\nShort call.\n\nNext: Paul writes it up.", file.SummaryMarkdown);
    }

    [Fact]
    public async Task Renaming_moves_it_with_the_note_and_the_pass_brings_it_up_to_date()
    {
        var s = Settings();
        var (rec, note, _) = await Finished(s);
        var old = MeetingFile.PathFor(note);

        var renamed = MeetingRename.Apply(rec, "Budget review")!;
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(MeetingFile.PathFor(renamed)));
        Assert.Equal("Onboarding review", Load(renamed).Title); // moved as it was: the rename itself has no settings

        Assert.Equal(1, MeetingFile.RefreshAll(s.ResolvedRecordingsFolder, "Paul"));
        var file = Load(renamed);
        Assert.Equal(("Budget review", Path.GetFileName(renamed)), (file.Title, file.Notes!.Markdown));
        Assert.Equal(0, MeetingFile.RefreshAll(s.ResolvedRecordingsFolder, "Paul")); // up to date: left alone
    }

    [Fact]
    public async Task A_file_that_cannot_be_written_leaves_the_note_done_and_is_tried_again()
    {
        var s = Settings();
        var rec = Recording(s, "Onboarding review");
        var blocked = Path.Combine(s.NotesFolder, Path.GetFileNameWithoutExtension(NoteWriter.FileName(Start, "Onboarding review")) + ".json");
        Directory.CreateDirectory(blocked); // something that isn't a file is in its way

        var state = await new MeetingProcessor(s, () => new FakeBackend { Summary = Reply }).ProcessAsync(rec, CancellationToken.None);
        Assert.Equal(Stage.Done, state.Stage);
        Assert.Null(state.LastError);
        Assert.True(File.Exists(state.NotePath));
        Assert.Equal(blocked, MeetingFile.PathFor(state.NotePath!));
        Assert.NotNull(rec.LoadState().MeetingFileRetryAt);
        Assert.Empty(Directory.GetFiles(s.NotesFolder, "*.lemur-tmp"));

        Directory.Delete(blocked);
        Assert.Equal(0, MeetingFile.RefreshAll(s.ResolvedRecordingsFolder, "Paul"));                                    // not yet
        Assert.Equal(1, MeetingFile.RefreshAll(s.ResolvedRecordingsFolder, "Paul", now: DateTimeOffset.Now.AddMinutes(16)));
        Assert.True(Load(state.NotePath!).ActionItemsFound);
        Assert.Null(rec.LoadState().MeetingFileRetryAt);
    }

    [Fact]
    public async Task A_read_only_file_never_fails_naming()
    {
        var s = Settings();
        var (rec, note, _) = await Finished(s);
        var path = MeetingFile.PathFor(note);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var outcome = await new SpeakerNamer(s).ApplyAsync(rec, [new NameChange("Speaker A", "Dana Whitfield")], CancellationToken.None);
            Assert.Contains("Note updated.", outcome.Messages);
            Assert.Contains("**[00:00:01] Dana Whitfield:**", File.ReadAllText(note));
            Assert.Equal("Speaker A", Load(note).ActionItems[1].Owner); // as it was
            Assert.NotNull(rec.LoadState().MeetingFileRetryAt);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
        Assert.Equal(1, MeetingFile.RefreshAll(s.ResolvedRecordingsFolder, "Paul", now: DateTimeOffset.Now.AddMinutes(16)));
        Assert.Equal("Dana Whitfield", Load(note).ActionItems[1].Owner);
    }

    [Fact]
    public async Task Older_meetings_get_one_from_the_pass_and_a_deleted_recording_keeps_its_own()
    {
        var s = Settings();
        var (rec, note, _) = await Finished(s);
        var path = MeetingFile.PathFor(note);
        File.Delete(path);
        rec.UpdateState(st => st.MeetingFileStamp = null); // as a meeting from before there were meeting files

        Assert.Equal(1, MeetingFile.RefreshAll(s.ResolvedRecordingsFolder, "Paul", max: 20));
        Assert.True(File.Exists(path));

        // Nothing to make it from: not tried at every pass, but again once its files change.
        var summary = File.ReadAllText(rec.SummaryFile);
        File.Delete(rec.SummaryFile);
        File.Delete(path);
        Assert.Equal(0, MeetingFile.RefreshAll(s.ResolvedRecordingsFolder, "Paul"));
        Assert.StartsWith("none:", rec.LoadState().MeetingFileStamp);
        File.WriteAllText(rec.SummaryFile, summary);
        Assert.Equal(1, MeetingFile.RefreshAll(s.ResolvedRecordingsFolder, "Paul"));

        rec.Delete(); // Recordings → Delete: the note and its meeting file stay
        Assert.Equal(0, MeetingFile.RefreshAll(s.ResolvedRecordingsFolder, "Paul"));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Export_writes_it_into_another_folder_and_a_file_left_behind_keeps_its_name()
    {
        var s = Settings();
        var (rec, note, _) = await Finished(s);
        var other = Path.Combine(_root, "out");
        Directory.CreateDirectory(other);
        var stamp = rec.LoadState().MeetingFileStamp;

        var exported = MeetingFile.Write(rec, "Paul", other);
        Assert.Equal(Path.Combine(other, Path.GetFileName(MeetingFile.PathFor(note))), exported);
        Assert.Equal(File.ReadAllText(MeetingFile.PathFor(note)), File.ReadAllText(exported));
        Assert.Equal(stamp, rec.LoadState().MeetingFileStamp);

        // A meeting file whose note was deleted still claims its name: the next note with that name takes " (2)".
        File.Delete(note);
        File.Delete(NoteWriter.HtmlPath(note));
        File.Delete(NoteWriter.AudioPath(note));
        Assert.Throws<InvalidOperationException>(() => MeetingFile.Write(rec, "Paul")); // nor is it written again beside nothing
        var meta = rec.LoadMeta();
        Assert.EndsWith(" (2).md", NoteWriter.Write(s.NotesFolder, meta, "## Summary\nx", [], "fake", null));
    }

    [Fact]
    public async Task A_transcript_left_out_isnt_in_the_file_and_the_file_says_so()
    {
        var s = Settings();
        s.MeetingFile.IncludeTranscript = false;
        var (rec, note, _) = await Finished(s);

        var json = File.ReadAllText(MeetingFile.PathFor(note));
        Assert.DoesNotContain("\"transcript\":", json);
        Assert.DoesNotContain("write it up", json);
        Assert.Contains("\"leftOut\": [\n    \"transcript\"\n  ]", json.ReplaceLineEndings("\n"));
        var file = Load(note);
        Assert.Null(file.Transcript);
        Assert.Equal([MeetingFile.TranscriptPart], file.LeftOut);
        // Still read: who was heard and whose the items are come from it.
        Assert.Equal([new MeetingFile.Person("Paul", true, 1, true), new MeetingFile.Person("Speaker A", false, 1, false)], file.People);
        Assert.Equal([true, false, false], file.ActionItems.Select(i => i.OwnerIsMe));
        Assert.Contains("I'll write it up.", File.ReadAllText(note)); // the note keeps its own

        // Naming a speaker and fixing a word write it again, still without.
        await new SpeakerNamer(s).ApplyAsync(rec, [new NameChange("Speaker A", "Dana Whitfield")], CancellationToken.None);
        Assert.Equal("Dana Whitfield", Load(note).People[1].Name);
        Assert.DoesNotContain("\"transcript\":", File.ReadAllText(MeetingFile.PathFor(note)));
        var vocabulary = new Vocabulary([new VocabularyEntry { Term = "Writeup", HeardAs = ["write it up"] }]);
        Assert.True(MeetingFixes.Apply(rec, vocabulary, "Paul", transcript: false) > 0);
        Assert.Contains("I'll Writeup.", File.ReadAllText(note));
        Assert.DoesNotContain("Writeup", File.ReadAllText(MeetingFile.PathFor(note)));
    }

    [Fact]
    public async Task Changing_the_choice_brings_every_file_up_to_date()
    {
        var s = Settings();
        var (rec, note, _) = await Finished(s);
        var path = MeetingFile.PathFor(note);
        Assert.Equal(0, MeetingFile.RefreshAll(s.ResolvedRecordingsFolder, "Paul")); // the transcript in, as before there was a choice
        var with = Load(note).Revision;

        Assert.Equal(1, MeetingFile.RefreshAll(s.ResolvedRecordingsFolder, "Paul", transcript: false));
        Assert.Null(Load(note).Transcript);
        Assert.NotEqual(with, Load(note).Revision); // an update to whoever has the old one
        Assert.Equal(0, MeetingFile.RefreshAll(s.ResolvedRecordingsFolder, "Paul", transcript: false));

        // An export can be made either way, the file beside the note left as it is.
        var other = Path.Combine(_root, "out");
        Directory.CreateDirectory(other);
        var exported = MeetingFile.Write(rec, "Paul", other, transcript: true);
        Assert.Equal(2, JsonSerializer.Deserialize<MeetingFile.Content>(File.ReadAllText(exported), Json.Options)!.Transcript!.Count);
        Assert.Null(Load(note).Transcript);

        Assert.Equal(1, MeetingFile.RefreshAll(s.ResolvedRecordingsFolder, "Paul", transcript: true));
        var file = Load(note);
        Assert.Equal(2, file.Transcript!.Count);
        Assert.Null(file.LeftOut);
        Assert.DoesNotContain("leftOut", File.ReadAllText(path));
        Assert.Equal(with, file.Revision); // the same file as before it was left out
    }

    [Theory]
    [InlineData("""{ "displayName": "Paul" }""", true)] // settings from before the choice
    [InlineData("""{ "displayName": "Paul", "meetingFile": null }""", true)]
    [InlineData("""{ "displayName": "Paul", "meetingFile": { "includeTranscript": false } }""", false)]
    public void The_choice_is_in_settings_and_the_transcript_is_in_unless_it_says_otherwise(string json, bool transcript)
    {
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, json);
        var settings = SettingsStore.LoadOrCreate(path);
        Assert.Equal(transcript, settings.MeetingFile.IncludeTranscript);

        SettingsStore.Save(settings, path);
        Assert.Contains($"\"includeTranscript\": {(transcript ? "true" : "false")}", File.ReadAllText(path));
    }
}
