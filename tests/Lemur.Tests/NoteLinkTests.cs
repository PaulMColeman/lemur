using Lemur.Pipeline;

namespace Lemur.Tests;

public class NoteLinkTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("llmmr-links").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    string Recordings => Path.Combine(_root, "rec");
    string Notes => Path.Combine(_root, "notes");

    [Fact]
    public void A_link_names_the_meeting_and_the_moment()
    {
        Assert.Equal("lemur://note/20261014-093000", NoteLinks.For("20261014-093000"));
        Assert.Equal("lemur://note/20261014-093000-2?t=754", NoteLinks.For("20261014-093000-2", 754.9));
        Assert.Throws<ArgumentException>(() => NoteLinks.For("..\\secrets"));

        Assert.True(NoteLinks.TryParse("lemur://note/20261014-093000?t=754", out var rec, out var t));
        Assert.Equal(("20261014-093000", (int?)754), (rec, t));
        Assert.True(NoteLinks.TryParse("LEMUR://note/20261014-093000/", out rec, out t));
        Assert.Equal(("20261014-093000", (int?)null), (rec, t));
    }

    [Theory]
    [InlineData("lemur://note/20261014-093000?t=0", "20261014-093000", 0)]
    [InlineData("lemur://note/20261014-093000-12?t=999999", "20261014-093000-12", 999999)]
    [InlineData("lemur://note/20261014-093000/?t=007", "20261014-093000", 7)]
    [InlineData("  lemur://note/20261014-093000?T=5 \r\n", "20261014-093000", 5)] // as a shell or a chat may hand it over
    public void Edges_that_are_still_note_links(string link, string recording, int seconds)
    {
        Assert.True(NoteLinks.TryParse(link, out var rec, out var t));
        Assert.Equal((recording, (int?)seconds), (rec, t));
    }

    [Theory]
    [InlineData("lemur://name/20261014-093000")]
    [InlineData("lemur://note/../../Windows")]
    [InlineData("lemur://note/20261014-093000?t=-5")]
    [InlineData("lemur://note/20261014-093000?t=1234567")]
    [InlineData("lemur://note/20261014-093000?t=5&x=1")]
    [InlineData("https://note/20261014-093000")]
    [InlineData("")]
    [InlineData("lemur://note/20261014-093000?t=")]
    [InlineData("lemur://note/20261014-093000?t=5.5")]
    [InlineData("lemur://note/20261014-093000#t=5")]
    [InlineData("lemur://note/20261014-093000?x=5")]
    [InlineData("lemur://note/20261014-093000-1234")]
    [InlineData("lemur://note/2026101-093000")]
    [InlineData("lemur://note//20261014-093000")]
    [InlineData("lemur://note/20261014-093000\n?t=5")]
    [InlineData("lemur://note/20261014%2D093000")]
    [InlineData("lemur://note/20261014-093000/x")]
    [InlineData("teamsrecorder://note/20261014-093000")] // newer than the old name: no legacy form
    [InlineData("lemur://note/20261014-093000?t=٧٥٤")] // other scripts' digits: refused, not a crash
    [InlineData("lemur://note/٢٠٢٦١٠١٤-093000")]
    public void Anything_else_is_not_a_note_link(string link) => Assert.False(NoteLinks.TryParse(link, out _, out _));

    [Fact]
    public void Null_is_not_a_note_link() => Assert.False(NoteLinks.TryParse(null, out _, out _));

    [Fact]
    public void A_note_that_wont_open_is_logged_without_its_name()
    {
        // .NET 10 puts the file in a failed Process.Start's message, and a note's file name is its meeting's title.
        var shell = new System.ComponentModel.Win32Exception(1155, @"An error occurred trying to start process 'C:\notes\2026-10-14 0930 Budget review.md'.");
        Assert.Equal("Win32Exception 1155", Lemur.Logging.Log.TypeAndCode(shell));
        var locked = new IOException(@"The process cannot access the file 'C:\notes\2026-10-14 0930 Budget review.json'.", unchecked((int)0x80070020));
        Assert.Equal("IOException 0x80070020", Lemur.Logging.Log.TypeAndCode(locked));
    }

    /// <summary>A note as NoteWriter writes it, with its page beside it unless told otherwise.</summary>
    string Note(string name, string recording, bool page = true, string? folder = null, DateTime? written = null)
    {
        var path = Path.Combine(folder ?? Notes, name + ".md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"---\ntitle: \"{name}\"\ndate: 2026-10-14T09:30:00-05:00\nrecording: {recording}\ntags: [meeting]\n---\n\n# {name}\n");
        if (page) File.WriteAllText(NoteWriter.HtmlPath(path), "<!doctype html>");
        if (written is { } w) File.SetLastWriteTimeUtc(path, w);
        return path;
    }

    RecordingFolder Recording(string name, string? notePath)
    {
        var rec = new RecordingFolder(Path.Combine(Recordings, name));
        Directory.CreateDirectory(rec.Path);
        rec.SaveMeta(new RecordingMeta { Title = "Design review", StartedAt = DateTimeOffset.Now, DurationSeconds = 60 });
        rec.SaveState(new ProcessingState { Stage = Stage.Done, NotePath = notePath });
        return rec;
    }

    [Fact]
    public void The_recordings_state_says_where_the_note_is_now_renamed_or_not()
    {
        // Renamed: the state follows the note; a stale copy under the old name, front matter and all, isn't taken.
        Note("2026-10-14 0930 Design review", "20261014-093000");
        var renamed = Note("2026-10-14 0930 Onboarding flow", "20261014-093000", written: DateTime.UtcNow.AddDays(-1));
        Recording("20261014-093000", renamed);
        Assert.Equal(NoteWriter.HtmlPath(renamed), NoteLinks.FindNote(Recordings, Notes, "20261014-093000"));

        // A note without a page: its Markdown. A note elsewhere than the notes folder now: where the state says.
        var elsewhere = Note("2026-10-14 1100 Standup", "20261014-110000", page: false, folder: Path.Combine(_root, "old notes"));
        Recording("20261014-110000", elsewhere);
        Assert.Equal(elsewhere, NoteLinks.FindNote(Recordings, Notes, "20261014-110000"));
    }

    [Fact]
    public void Without_the_recording_the_note_is_found_by_its_front_matter()
    {
        var note = Note("2026-10-14 0930 Design review", "20261014-093000");
        Note("2026-10-14 0930 Design review (2)", "20261014-093000-2"); // the same minute, another recording
        Assert.Equal(NoteWriter.HtmlPath(note), NoteLinks.FindNote(Recordings, Notes, "20261014-093000"));
        Assert.Equal(NoteWriter.HtmlPath(Path.Combine(Notes, "2026-10-14 0930 Design review (2).md")),
            NoteLinks.FindNote(Recordings, Notes, "20261014-093000-2"));

        // A recording folder whose note was deleted or moved by hand: the front matter still finds it.
        Recording("20261014-093000", Path.Combine(Notes, "gone.md"));
        Assert.Equal(NoteWriter.HtmlPath(note), NoteLinks.FindNote(Recordings, Notes, "20261014-093000"));

        // Renamed by hand, date and all.
        var byHand = Note("Onboarding notes", "20261015-140000", page: false);
        Assert.Equal(byHand, NoteLinks.FindNote(Recordings, Notes, "20261015-140000"));

        // Front matter an editor rewrote with quotes.
        File.WriteAllText(Path.Combine(Notes, "2026-10-16 0800 Quoted.md"), "---\nrecording: \"20261016-080000\"\n---\n");
        Assert.Equal(Path.Combine(Notes, "2026-10-16 0800 Quoted.md"), NoteLinks.FindNote(Recordings, Notes, "20261016-080000"));
    }

    [Fact]
    public void The_original_note_is_chosen_over_a_OneDrive_conflict_copy()
    {
        var original = Note("2026-10-14 0930 Design review", "20261014-093000", written: DateTime.UtcNow.AddHours(-2));
        Note("2026-10-14 0930 Design review-PAULS-LAPTOP", "20261014-093000", written: DateTime.UtcNow);
        Note("2026-10-14 0930 Design review-PAULS-LAPTOP-2", "20261014-093000", written: DateTime.UtcNow);
        Assert.Equal(NoteWriter.HtmlPath(original), NoteLinks.FindNote(Recordings, Notes, "20261014-093000"));

        // Two that aren't copies of each other: the newest.
        Note("2026-10-15 1000 Planning", "20261015-100000", written: DateTime.UtcNow.AddHours(-1));
        var newer = Note("2026-10-15 1000 Planning again", "20261015-100000", written: DateTime.UtcNow);
        Assert.Equal(NoteWriter.HtmlPath(newer), NoteLinks.FindNote(Recordings, Notes, "20261015-100000"));
    }

    [Fact]
    public void Nothing_found_is_null()
    {
        Assert.Null(NoteLinks.FindNote(Recordings, Notes, "20261014-093000")); // no folders at all
        Note("2026-10-14 0930 Design review", "20261014-093000-2");
        // "recording:" in the body, not the front matter.
        File.WriteAllText(Path.Combine(Notes, "2026-10-14 0930 Body.md"), "# Notes\n\nrecording: 20261014-093000\n");
        // An unclosed front matter that never says it.
        File.WriteAllText(Path.Combine(Notes, "2026-10-14 0930 Open.md"), "---\ntitle: x\n");
        Assert.Null(NoteLinks.FindNote(Recordings, Notes, "20261014-093000"));
        Recording("20261014-093000", null); // recorded, but no note yet
        Assert.Null(NoteLinks.FindNote(Recordings, Notes, "20261014-093000"));
    }

    [Fact]
    public void A_link_cant_reach_outside_the_notes_folder()
    {
        // Notes that say the recording but aren't in the notes folder itself: beside it, below it.
        Note("2026-10-14 0930 Design review", "20261014-093000", folder: Path.Combine(_root, "notes2"));
        Note("2026-10-14 0930 Design review", "20261014-093000", folder: Path.Combine(Notes, "archive"));
        Assert.Null(NoteLinks.FindNote(Recordings, Notes, "20261014-093000"));

        // A name that isn't a recording's.
        Note("x", "..\\..\\x");
        Assert.Null(NoteLinks.FindNote(Recordings, Notes, "..\\..\\x"));
        Assert.Null(NoteLinks.FindNote(Recordings, Notes, "..\\notes2\\20261014-093000"));

        // A state that names something other than a note opens nothing of it.
        var exe = Path.Combine(_root, "tool.exe");
        File.WriteAllText(exe, "MZ");
        Recording("20261014-120000", exe);
        Assert.Null(NoteLinks.FindNote(Recordings, Notes, "20261014-120000"));
    }
}
