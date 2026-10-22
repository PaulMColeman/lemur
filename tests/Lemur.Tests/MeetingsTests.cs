using Lemur.Audio;
using Lemur.Config;
using Lemur.Pipeline;
using Lemur.Speakers;
using Lemur.Transcripts;

namespace Lemur.Tests;

/// <summary>Titles, descriptions, renaming and search: finding a meeting again.</summary>
public class MeetingsTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("llmmr-meetings").FullName;
    static readonly DateTimeOffset Start = new(2026, 10, 14, 9, 18, 0, TimeSpan.FromHours(-5));

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
    };

    RecordingFolder Recording(AppSettings s, string title, string source = "Google Meet (Edge)")
    {
        var rec = RecordingFolder.CreateNew(s.ResolvedRecordingsFolder, Start);
        int rate = AnchoredTrackWriter.SampleRate;
        var mic = new float[2 * rate];
        var others = new float[2 * rate];
        for (int i = 0; i < rate; i++) mic[i] = 0.2f * MathF.Sin(i * 0.2f);
        for (int i = rate; i < 2 * rate; i++) others[i] = 0.2f * MathF.Sin(i * 0.3f);
        using (var a = new WavSampleSink(rec.MicWav)) a.Write(mic);
        using (var b = new WavSampleSink(rec.OthersWav)) b.Write(others);
        rec.SaveMeta(new RecordingMeta { Title = title, Source = source, StartedAt = Start, EndedAt = Start.AddSeconds(2), DurationSeconds = 2 });
        rec.SaveState(new ProcessingState());
        return rec;
    }

    /// <summary>A summary without its first lines, with enough in it to describe the meeting from.</summary>
    const string Plain = "## Summary\nPaul and Dana went over the firewall rules and agreed to move them on Friday.\n\n## Action items\n- [ ] Paul - write it up";

    const string Reply = """
        Title: Vendor contract and firewall migration
        About: Agreed to sign the Acme contract this week; the firewall move waits for the new rules.

        ## Summary
        Paul and Speaker A went over the vendor contract.
        """;

    // ---- the summary's first lines ----

    [Theory]
    [InlineData("Title: Budget review\nAbout: Cut travel by 10%.\n\n## Summary\nText.")]
    [InlineData("**Title:** Budget review\n**About:** Cut travel by 10%.\n\n## Summary\nText.")]
    [InlineData("- Title: \"Budget review\"\n- About: Cut travel by 10%.\n## Summary\nText.")]
    [InlineData("\n\nTitle: Budget review.\r\nAbout: Cut travel by 10%.\r\n\r\n## Summary\r\nText.")]
    public void The_title_and_about_lines_come_off_the_summary(string reply)
    {
        var (summary, title, about) = MeetingAbout.Split(reply);
        Assert.Equal("Budget review", title);
        Assert.Equal("Cut travel by 10%.", about);
        Assert.Equal("## Summary\nText.", summary);
    }

    [Fact]
    public void A_summary_without_them_is_left_alone()
    {
        var reply = "## Summary\nThe title of the deck was discussed.\n\n## Decisions\n- About: none";
        var (summary, title, about) = MeetingAbout.Split(reply);
        Assert.Equal((reply, null, null), (summary, title, about));
        // Placeholders echoed from the prompt, and a title that says nothing, count as none.
        Assert.Equal((null, null), (MeetingAbout.Split("Title: <3 to 8 words>\nAbout: <one sentence>\n## Summary\nx").Title,
            MeetingAbout.Split("Title: Meeting\n## Summary\nx").Title));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("Meeting", true)]
    [InlineData("Zoom Meeting", true)]
    [InlineData("Meet - abc-defg-hij", true)]
    [InlineData("Meeting 2026-10-14 09:18", true)]
    [InlineData("Darrell Jones", false)]
    [InlineData("Ticketing Solution needs and Ideas for Next year.", false)]
    public void Untitled_means_no_title_or_one_that_says_nothing(string title, bool untitled) =>
        Assert.Equal(untitled, MeetingAbout.IsUntitled(title));

    [Fact]
    public void Without_an_about_line_the_summary_describes_the_meeting()
    {
        const string first = "The team reviewed the onboarding flow. Research found most people dropped off at the calendar step, " +
                             "so it becomes optional. The invite step ships with a share link first, and suggested teammates wait for November.";
        var summary = "## Summary\n" + first.Replace("onboarding", "**onboarding**") + " Priya runs five quick tests next week.\n\n## Decisions\n- x";
        Assert.Equal(first, MeetingAbout.FromSummary(summary)); // whole sentences, up to about 220 characters
        Assert.Null(MeetingAbout.FromSummary("## Summary\nNone.\n"));
        Assert.Equal("Short one.", MeetingAbout.FromSummary("Short one."));
    }

    [Fact]
    public void The_describer_reads_json_however_it_is_wrapped()
    {
        Assert.Equal(("Budget review", "Cut travel."),
            MeetingDescriber.Parse("```json\n{\"title\": \"Budget review.\", \"about\": \"Cut travel.\"}\n```"));
        Assert.Equal((null, null), MeetingDescriber.Parse("I can't do that."));
        Assert.Equal((null, "Cut travel."), MeetingDescriber.Parse("{\"title\": \"Meeting\", \"description\": \"Cut travel.\"}"));
    }

    [Fact]
    public void Front_matter_lines_are_read_set_added_and_removed()
    {
        var note = "---\r\ntitle: \"x\"\r\ntags: [meeting]\r\n---\r\n\r\n# x\r\n";
        Assert.Equal("x", FrontMatter.Get(note, "title"));
        var added = FrontMatter.Set(note, "description", FrontMatter.Quote("Said \"hi\"."));
        Assert.Contains("description: \"Said \\\"hi\\\".\"\r\ntags: [meeting]", added);
        Assert.Equal("Said \"hi\".", FrontMatter.Get(added, "description"));
        Assert.Equal(note, FrontMatter.Set(added, "description", null));
        Assert.Equal("# no front matter", FrontMatter.Set("# no front matter", "title", "\"y\""));
    }

    // ---- in the pipeline ----

    [Fact]
    public async Task An_untitled_meeting_is_named_from_its_summary()
    {
        var s = Settings();
        var rec = Recording(s, "");
        var state = await new MeetingProcessor(s, () => new FakeBackend { Summary = Reply }).ProcessAsync(rec, CancellationToken.None);

        var meta = rec.LoadMeta();
        Assert.Equal("Vendor contract and firewall migration", meta.Title);
        Assert.Equal("Vendor contract and firewall migration", meta.SuggestedTitle);
        Assert.StartsWith("Agreed to sign the Acme contract", meta.Description);
        Assert.EndsWith("2026-10-14 0918 Vendor contract and firewall migration.md", state.NotePath);
        var note = File.ReadAllText(state.NotePath!);
        Assert.Contains("description: \"Agreed to sign the Acme contract this week;", note);
        Assert.DoesNotContain("Title:", note);
        Assert.DoesNotContain("About:", File.ReadAllText(rec.SummaryFile));
        Assert.StartsWith("## Summary", File.ReadAllText(rec.SummaryFile));
    }

    [Fact]
    public async Task A_meeting_the_call_app_named_keeps_its_name()
    {
        var s = Settings();
        var rec = Recording(s, "Weekly sync", "Teams (Edge)");
        var state = await new MeetingProcessor(s, () => new FakeBackend { Summary = Reply }).ProcessAsync(rec, CancellationToken.None);
        var meta = rec.LoadMeta();
        Assert.Equal(("Weekly sync", "Vendor contract and firewall migration"), (meta.Title, meta.SuggestedTitle));
        Assert.EndsWith("0918 Weekly sync.md", state.NotePath);
    }

    [Fact]
    public async Task A_summary_without_its_first_lines_is_described_by_asking_again()
    {
        var s = Settings();
        var rec = Recording(s, "");
        var chat = new FakeChat("{\"title\": \"Firewall rules review\", \"about\": \"The new rules go live Friday.\"}");
        var state = await new MeetingProcessor(s, () => new FakeBackend { Summary = Plain }, chat: () => chat).ProcessAsync(rec, CancellationToken.None);

        Assert.Equal(Stage.Done, state.Stage);
        Assert.Equal(("Firewall rules review", "The new rules go live Friday."), (rec.LoadMeta().Title, rec.LoadMeta().Description));
        Assert.StartsWith("Meeting notes:\n## Summary", chat.LastTranscript); // the summary, not the transcript
        Assert.EndsWith("0918 Firewall rules review.md", state.NotePath);

        // Without a model to ask, the notes are still written, under the old name.
        var other = Recording(s, "");
        var plain = await new MeetingProcessor(s, () => new FakeBackend { Summary = Plain }).ProcessAsync(other, CancellationToken.None);
        Assert.EndsWith("0918 Meeting.md", plain.NotePath);
        Assert.Null(other.LoadMeta().Description);
    }

    // ---- renaming ----

    async Task<(RecordingFolder Rec, string Note)> Finished(AppSettings s, string title = "Weekly sync")
    {
        var rec = Recording(s, title, "Teams (Edge)");
        var state = await new MeetingProcessor(s, () => new FakeBackend { Summary = Plain }).ProcessAsync(rec, CancellationToken.None);
        Assert.Equal(Stage.Done, state.Stage);
        return (rec, state.NotePath!);
    }

    [Fact]
    public async Task Renaming_moves_the_notes_files_and_changes_the_title_inside_them()
    {
        var s = Settings();
        var (rec, old) = await Finished(s);
        Assert.True(File.Exists(NoteWriter.AudioPath(old)));

        var note = MeetingRename.Apply(rec, "Budget / Q4 review");

        Assert.EndsWith("2026-10-14 0918 Budget - Q4 review.md", note);
        Assert.False(File.Exists(old));
        Assert.False(File.Exists(NoteWriter.HtmlPath(old)));
        Assert.False(File.Exists(NoteWriter.AudioPath(old)));
        Assert.True(File.Exists(NoteWriter.AudioPath(note!)));
        var md = File.ReadAllText(note!);
        Assert.Contains("title: \"Budget / Q4 review\"", md);
        Assert.Contains("# Budget / Q4 review", md);
        Assert.Contains("audio: \"2026-10-14 0918 Budget - Q4 review.mp3\"", md);
        Assert.Contains("[recording](<2026-10-14 0918 Budget - Q4 review.mp3>)", md);
        Assert.DoesNotContain("Weekly sync", md);
        var page = File.ReadAllText(NoteWriter.HtmlPath(note!));
        Assert.Contains("<title>Budget / Q4 review</title>", page);
        Assert.Contains("<h1>Budget / Q4 review</h1>", page);
        Assert.Contains("src=\"2026-10-14%200918%20Budget%20-%20Q4%20review.mp3\"", page);
        Assert.DoesNotContain("Weekly%20sync", page);

        var state = rec.LoadState();
        Assert.Equal(note, state.NotePath);
        Assert.Equal(NoteWriter.Hash(note!), state.NoteHash); // still untouched by the user: later changes may rewrite it
        Assert.Equal("Budget / Q4 review", rec.LoadMeta().Title);
    }

    [Fact]
    public async Task Renaming_an_edited_note_keeps_the_edits()
    {
        var s = Settings();
        var (rec, old) = await Finished(s);
        File.AppendAllText(old, "\nMy own note: call Dana back.\n");
        var hash = rec.LoadState().NoteHash;

        var note = MeetingRename.Apply(rec, "Dana follow-up");

        var md = File.ReadAllText(note!);
        Assert.Contains("My own note: call Dana back.", md);
        Assert.Contains("# Dana follow-up", md);
        Assert.Equal(hash, rec.LoadState().NoteHash); // still marked as edited
    }

    [Fact]
    public async Task A_taken_name_gets_a_number_and_a_locked_file_changes_nothing()
    {
        var s = Settings();
        var (first, _) = await Finished(s, "Budget review");
        var (second, note) = await Finished(s, "Weekly sync");

        Assert.EndsWith("0918 Budget review (2).md", MeetingRename.Apply(second, "Budget review"));
        Assert.EndsWith("0918 Budget review.md", first.LoadState().NotePath);

        var current = second.LoadState().NotePath!;
        using (new FileStream(NoteWriter.AudioPath(current), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => MeetingRename.Apply(second, "Something else"));
        Assert.True(File.Exists(current));                       // moved back
        Assert.True(File.Exists(NoteWriter.HtmlPath(current)));
        Assert.Equal("Budget review", second.LoadMeta().Title);  // and the title is unchanged
        Assert.Equal(current, second.LoadState().NotePath);
        _ = note;
    }

    [Fact]
    public async Task A_title_with_quotes_is_still_found_in_the_heading()
    {
        var s = Settings();
        var (rec, _) = await Finished(s, "Say \"hi\" to Dana");
        var md = File.ReadAllText(MeetingRename.Apply(rec, "Hello Dana")!);
        Assert.Contains("# Hello Dana", md);
        Assert.DoesNotContain("# Say", md);
    }

    [Fact]
    public void A_meeting_without_notes_yet_just_changes_its_title()
    {
        var s = Settings();
        var rec = Recording(s, "");
        Assert.Null(MeetingRename.Apply(rec, "  Standup \t with  Dana "));
        Assert.Equal("Standup with Dana", rec.LoadMeta().Title);
        Assert.Throws<ArgumentException>(() => MeetingRename.Apply(rec, "   "));
    }

    // ---- describing older meetings ----

    [Fact]
    public async Task Older_meetings_get_a_description_and_untitled_ones_a_title()
    {
        var s = Settings();
        var (titled, _) = await Finished(s, "Weekly sync");
        var (untitled, before) = await Finished(s, "");
        Assert.EndsWith("0918 Meeting.md", before);
        var chat = new FakeChat("{\"title\": \"Firewall rules review\", \"about\": \"The new rules go live Friday.\"}");
        var queue = new ProcessingQueue(() => s, () => new MeetingProcessor(s, () => new FakeBackend()), () => chat, Later);

        await queue.DescribeOneAsync(s, CancellationToken.None); // the newest first: one per pass
        await queue.DescribeOneAsync(s, CancellationToken.None);
        await queue.DescribeOneAsync(s, CancellationToken.None); // nothing left to do

        Assert.Equal(("Weekly sync", "The new rules go live Friday."), (titled.LoadMeta().Title, titled.LoadMeta().Description));
        Assert.Equal("Firewall rules review", untitled.LoadMeta().Title);
        Assert.EndsWith("0918 Firewall rules review.md", untitled.LoadState().NotePath);
        Assert.False(File.Exists(before));
    }

    [Fact]
    public async Task A_describer_that_fails_is_tried_again_later_not_at_once()
    {
        var s = Settings();
        var (rec, _) = await Finished(s, "Weekly sync");
        var chat = new FakeChat("no json here");
        var queue = new ProcessingQueue(() => s, () => new MeetingProcessor(s, () => new FakeBackend()), () => chat, Later);
        await queue.DescribeOneAsync(s, CancellationToken.None);
        chat.LastTranscript = null;
        await queue.DescribeOneAsync(s, CancellationToken.None);
        Assert.Null(chat.LastTranscript); // waiting half an hour before the next try
        Assert.Null(rec.LoadMeta().Description);
    }

    static DateTimeOffset Later() => DateTimeOffset.Now.AddHours(1);

    [Fact]
    public async Task The_background_pass_never_renames_an_edited_note_or_a_title_the_user_chose()
    {
        var s = Settings();
        var (edited, editedNote) = await Finished(s, "");
        File.AppendAllText(editedNote, "\nMy own line.\n");
        var (chosen, _) = await Finished(s, "");
        MeetingRename.Apply(chosen, "Meeting"); // the user really wants it called that
        var chat = new FakeChat("{\"title\": \"Firewall rules review\", \"about\": \"The rules move on Friday.\"}");
        var queue = new ProcessingQueue(() => s, () => new MeetingProcessor(s, () => new FakeBackend()), () => chat, Later);

        await queue.DescribeOneAsync(s, CancellationToken.None);
        await queue.DescribeOneAsync(s, CancellationToken.None);

        Assert.Equal("Meeting", chosen.LoadMeta().Title);
        Assert.Equal(("", "Firewall rules review", "The rules move on Friday."),
            (edited.LoadMeta().Title, edited.LoadMeta().SuggestedTitle, edited.LoadMeta().Description));
        Assert.Equal(editedNote, edited.LoadState().NotePath);
        Assert.Contains("My own line.", File.ReadAllText(editedNote));
    }

    [Fact]
    public async Task The_background_pass_waits_after_a_meeting_finishes_and_after_a_failure()
    {
        var s = Settings();
        var (rec, _) = await Finished(s, "");
        var chat = new FakeChat("{\"title\": \"Firewall rules review\", \"about\": \"The rules move on Friday.\"}");
        var now = DateTimeOffset.Now;
        var queue = new ProcessingQueue(() => s, () => new MeetingProcessor(s, () => new FakeBackend()), () => chat, () => now);

        await queue.DescribeOneAsync(s, CancellationToken.None);
        Assert.Null(chat.LastTranscript); // just finished: its note may be open, and the toast points at it
        now = now.AddMinutes(31);
        await queue.DescribeOneAsync(s, CancellationToken.None);
        Assert.Equal("Firewall rules review", rec.LoadMeta().Title);

        // A failure pauses the whole job, not just that meeting.
        var (other, _) = await Finished(s, "Weekly sync");
        var (third, _) = await Finished(s, "Standup");
        var broken = new FakeChat("no json");
        var paused = new ProcessingQueue(() => s, () => new MeetingProcessor(s, () => new FakeBackend()), () => broken, () => now.AddHours(1));
        await paused.DescribeOneAsync(s, CancellationToken.None);
        broken.LastTranscript = null;
        await paused.DescribeOneAsync(s, CancellationToken.None);
        Assert.Null(broken.LastTranscript);
        Assert.Null(other.LoadMeta().Description);
        Assert.Null(third.LoadMeta().Description);
    }

    [Fact]
    public async Task A_rename_that_fails_part_way_puts_everything_back()
    {
        var s = Settings();
        var (rec, note) = await Finished(s);
        var before = (File.ReadAllText(note), File.ReadAllText(NoteWriter.HtmlPath(note)), rec.LoadState().NoteHash);
        File.SetAttributes(note, FileAttributes.ReadOnly); // it moves, but can't be rewritten
        try
        {
            Assert.Throws<IOException>(() => MeetingRename.Apply(rec, "Budget review"));
        }
        finally
        {
            File.SetAttributes(note, FileAttributes.Normal);
        }
        Assert.Equal(note, rec.LoadState().NotePath);
        Assert.Equal(before, (File.ReadAllText(note), File.ReadAllText(NoteWriter.HtmlPath(note)), rec.LoadState().NoteHash));
        Assert.True(File.Exists(NoteWriter.AudioPath(note)));
        Assert.Equal(("Weekly sync", false), (rec.LoadMeta().Title, rec.LoadMeta().TitleByUser));
        Assert.Equal(4, Directory.GetFiles(s.NotesFolder).Length); // .md, .html, .mp3, .json: nothing left behind under the new name
        Assert.True(File.Exists(MeetingFile.PathFor(note)));       // the meeting file moved back too
    }

    [Fact]
    public async Task A_reply_cut_off_after_its_title_is_not_a_summary()
    {
        var s = Settings();
        var rec = Recording(s, "");
        var state = await new MeetingProcessor(s, () => new FakeBackend { Summary = "Title: Budget review\nAbout: The team agre" })
            .ProcessAsync(rec, CancellationToken.None);
        Assert.Equal(Stage.Identified, state.Stage); // will be retried
        Assert.Contains("no summary", state.LastError);
        Assert.False(File.Exists(rec.SummaryFile));
    }

    [Theory]
    [InlineData("None")]
    [InlineData("N/A")]
    [InlineData("TBD")]
    [InlineData("[Meeting title]")]
    [InlineData("No meeting notes provided")]
    [InlineData("12:30")]
    public void Non_answers_are_not_titles(string title) => Assert.Null(MeetingAbout.ModelTitle(title));

    [Fact]
    public void Only_the_lines_a_summary_starts_with_are_taken_off()
    {
        var custom = "Short overview of the call.\n- Description: the new pricing page\n- About: nothing else";
        Assert.Equal((custom, null, null), MeetingAbout.Split(custom));
        Assert.Equal("No meeting next week; the launch moves to May.", MeetingAbout.ModelDescription("No meeting next week; the launch moves to May."));
    }

    [Fact]
    public void A_list_written_as_a_block_is_replaced_whole()
    {
        var note = "---\ntitle: \"x\"\npeople:\n  - Speaker A\n  - Dana\ntags: [meeting]\n---\n";
        Assert.Equal("---\ntitle: \"x\"\npeople: [\"Dana\"]\ntags: [meeting]\n---\n", FrontMatter.Set(note, "people", "[\"Dana\"]"));
        Assert.Equal("---\ntitle: \"x\"\ntags: [meeting]\n---\n", FrontMatter.Set(note, "people", null));
        // A title with a backslash or quotes stays valid YAML.
        var md = NoteWriter.Render(new RecordingMeta { Title = "Q3\\Q4 \"plan\"", StartedAt = DateTimeOffset.Now }, "## Summary\nx", [], "fake", null);
        Assert.Contains("title: \"Q3\\\\Q4 \\\"plan\\\"\"", md);
        Assert.Equal("Q3\\Q4 \"plan\"", FrontMatter.Get(md, "title"));
    }

    // ---- the Recordings window's list and search ----

    [Fact]
    public async Task Rows_carry_what_the_meeting_was_about_and_who_was_there()
    {
        var s = Settings();
        var (rec, _) = await Finished(s, "Weekly sync");
        rec.Write(rec.IdentifiedFile, new IdentifiedResult
        {
            Speakers = [new IdentifiedSpeaker { Key = "S2", Label = "Speaker A", Status = SpeakerStatus.Named, Name = "Dana Whitfield" }],
        });
        var row = Assert.Single(new RecordingList().Build(s.ResolvedRecordingsFolder, null, null, DateTimeOffset.Now));
        Assert.Equal("Paul and Dana went over the firewall rules and agreed to move them on Friday.", row.Description); // from the summary
        Assert.Equal(["Dana Whitfield"], row.People);
        Assert.Equal(("Teams (Edge)", false), (row.Source, row.Untitled));

        rec.UpdateMeta(m => m.Description = "Agreed the budget.");
        Assert.Equal("Agreed the budget.", new RecordingList().Build(s.ResolvedRecordingsFolder, null, null, DateTimeOffset.Now).Single().Description);
    }

    [Fact]
    public void Search_words_must_all_appear_and_quotes_keep_words_together()
    {
        Assert.Equal(["firewall", "acme contract", "Q4"], SearchQuery.Parse(" firewall \"acme   contract\" Q4 FIREWALL").Terms);
        Assert.True(SearchQuery.Parse("  ").IsEmpty);
        Assert.Equal(["unclosed quote"], SearchQuery.Parse("\"unclosed quote").Terms);
    }

    [Fact]
    public async Task Search_finds_meetings_by_what_was_said_not_by_the_notes_boilerplate()
    {
        var s = Settings();
        var (rec, note) = await Finished(s, "Weekly sync");
        File.WriteAllText(note, File.ReadAllText(note).Replace("I'll write it up.", "I'll move the firewall plans on Friday, that's a long line " +
            new string('x', 10) + " about the rules and what they block, which is mostly the old VPN and the test lab."));
        var row = new RecordingList().Build(s.ResolvedRecordingsFolder, null, null, DateTimeOffset.Now).Single() with { People = ["Dana Whitfield"] };
        var search = new MeetingSearch();

        Assert.True(search.Matches(row, SearchQuery.Parse("firewall plans friday")));
        Assert.True(search.Matches(row, SearchQuery.Parse("weekly dana")));       // title and people
        Assert.False(search.Matches(row, SearchQuery.Parse("firewall budget")));  // every word must be there
        Assert.False(search.Matches(row, SearchQuery.Parse("transcript")));      // a heading
        Assert.False(search.Matches(row, SearchQuery.Parse("fake")));            // front matter and the date line (backend: fake)
        Assert.True(search.Matches(row, SearchQuery.Parse("teams")));            // ...but where it was recorded counts

        var lines = search.Lines(row, SearchQuery.Parse("firewall"));
        Assert.Equal("[00:00:00] Paul: I'll move the firewall plans on Friday, that's a long line xxxxxxxxxx about the rules and what they block, which is mostly the old…",
            lines[1]);
        Assert.Equal("Paul and Dana went over the firewall rules and agreed to move them on Friday.", lines[0]); // the summary's line
        Assert.Equal(2, search.Count(row, SearchQuery.Parse("firewall")));

        // Without its note, the meeting's own summary and transcript are searched.
        File.Delete(note);
        var bare = new RecordingList().Build(s.ResolvedRecordingsFolder, null, null, DateTimeOffset.Now).Single();
        Assert.True(search.Matches(bare, SearchQuery.Parse("write it up")));
    }

    [Fact]
    public void Date_ranges_cover_whole_days()
    {
        var today = new DateTime(2026, 10, 14);
        Assert.Equal((today, today.AddDays(1)), DateRanges.Days(DateRange.Today, today.AddHours(15)));
        Assert.Equal((new DateTime(2026, 9, 1), new DateTime(2026, 10, 1)), DateRanges.Days(DateRange.LastMonth, today));
        Assert.Equal((new DateTime(2026, 10, 8), today.AddDays(1)), DateRanges.Days(DateRange.Last7Days, today));
        Assert.Equal((new DateTime(2026, 10, 1), new DateTime(2026, 10, 4)),
            DateRanges.Days(DateRange.Between, today, new DateTime(2026, 10, 3), new DateTime(2026, 10, 1)));
        Assert.Null(DateRanges.Days(DateRange.AnyTime, today));

        var days = DateRanges.Days(DateRange.Yesterday, today);
        Assert.True(DateRanges.Contains(days, new DateTimeOffset(new DateTime(2026, 10, 13, 23, 59, 0))));
        Assert.False(DateRanges.Contains(days, new DateTimeOffset(today)));
        Assert.False(DateRanges.Contains(days, null));
        Assert.True(DateRanges.Contains(null, null));
    }
}
