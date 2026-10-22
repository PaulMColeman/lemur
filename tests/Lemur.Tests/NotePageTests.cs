using Lemur.Audio;
using Lemur.Config;
using Lemur.Pipeline;
using Lemur.Transcripts;

namespace Lemur.Tests;

/// <summary>The note page's attendees list, and older pages brought up to date.</summary>
public class NotePageTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("llmmr-page").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    static readonly RecordingMeta Meta = new() { Title = "Design review", Source = "Teams", StartedAt = DateTimeOffset.Now, DurationSeconds = 300 };

    static LabelledSegment Said(double at, double seconds, string who, bool me = false) =>
        new(at, at + seconds, who, "Words.", me, "S" + who.Length);

    [Fact]
    public void Everyone_heard_is_listed_you_first_then_by_how_long_they_talked()
    {
        LabelledSegment[] segments =
        [
            Said(0, 90, "Dana Whitfield"), Said(100, 30, "Paul", me: true), Said(140, 10, "Speaker B"),
            Said(160, 5, "Other audio"), Said(170, 200, "Sam Okafor?"), Said(380, 30, "Paul", me: true),
        ];
        var page = NoteHtml.Render(Meta, "## Summary\nx", segments, "fake", null, people: ["Dana Whitfield"], recording: "20261014-091800");

        Assert.Contains("<h2 id=\"attendees\">Attendees <span class=\"n\">4</span></h2>", page);
        var chips = System.Text.RegularExpressions.Regex.Matches(page, "<li class=\"([^\"]+)\"><b>([^<]+)</b><span>([^<]+)</span></li>")
            .Select(m => $"{m.Groups[1].Value}|{m.Groups[2].Value}|{m.Groups[3].Value}").ToList();
        // Colours as in the transcript (others by first appearance: Dana s0, Speaker B s1, Other audio s2, Sam s3).
        Assert.Equal(["me|Paul|you · 1 min", "s3 unnamed|Sam Okafor?|3 min", "s0|Dana Whitfield|2 min", "s1 unnamed|Speaker B|10 s"], chips);
        var attendees = page[page.IndexOf("class=\"attendees\"")..page.IndexOf("</section>", page.IndexOf("class=\"attendees\""))];
        Assert.DoesNotContain("Other audio", attendees); // in the transcript, but not an attendee
        Assert.DoesNotContain("<p class=\"meta\">With ", page);
        int list = page.IndexOf("class=\"attendees\""), link = page.IndexOf("lemur://name/20261014-091800");
        Assert.True(list > 0 && link > list && link < page.IndexOf("</header>")); // the link to name them, under the list
        Assert.Contains("<meta name=\"generator\" content=\"Lemur note page 3\">", page);
    }

    const string ActionItems = "## Summary\nWe met.\n\n## Action items\n" +
                               "- [ ] Paul - send the deck - Fri 2026-10-16 [00:02:34]\n" +
                               "- [x] <b>Dana</b> & \"Sam\" - fix [the] <script>alert(1)</script> page [00:00:05]\n" +
                               "- [ ] Sam - check the numbers\n" +
                               "- [ ] Paul - past the end [00:06:00]\n" +
                               "- [ ] Paul - a time inside [00:01:00] the text\n\n" +
                               "## Decisions\nAgreed at [00:01:00], said in a paragraph.";

    [Fact]
    public void A_summary_items_time_plays_from_then_and_its_text_stays_escaped()
    {
        var page = NoteHtml.Render(Meta, ActionItems, [Said(0, 5, "Paul", me: true)], "fake", null, audio: "2026-10-14 0930 Design #1 & co.mp3");
        var summary = page[page.IndexOf("<section class=\"summary\">")..page.IndexOf("</section>", page.IndexOf("<section class=\"summary\">"))];

        Assert.Contains("Paul - send the deck - Fri 2026-10-16 <a href=\"2026-10-14%200930%20Design%20%231%20%26%20co.mp3#t=154\" " +
                        "class=\"moment\" data-t=\"154\" title=\"Play from here\">00:02:34</a></li>", summary);
        // The summary's own markup is text, before the link as anywhere else.
        Assert.Contains("&lt;b&gt;Dana&lt;/b&gt; &amp; &quot;Sam&quot; - fix [the] &lt;script&gt;alert(1)&lt;/script&gt; page <a href=", summary);
        Assert.Contains("data-t=\"5\"", summary);
        Assert.DoesNotContain("<script", summary);
        Assert.DoesNotContain("<b>", summary);
        // Only a time ending an item within the meeting (300 s) becomes a link.
        Assert.Contains("Sam - check the numbers</li>", summary);
        Assert.Contains("Paul - past the end [00:06:00]</li>", summary);
        Assert.Contains("Paul - a time inside [00:01:00] the text</li>", summary);
        Assert.Contains("Agreed at [00:01:00], said in a paragraph.", summary);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(summary, "class=\"moment\"").Count);
    }

    [Theory]
    [InlineData("[02:34]", 154, "02:34")]
    [InlineData("(00:02:34)", 154, "00:02:34")]
    [InlineData("[00:02:34].", 154, "00:02:34")]
    [InlineData("[75:30]", 4530, "75:30")]
    [InlineData("[1:02:03]", 3723, "1:02:03")]
    public void A_time_the_meeting_file_reads_plays_from_the_page_too(string time, int seconds, string clock)
    {
        var meta = new RecordingMeta { Title = "Design review", Source = "Teams", StartedAt = DateTimeOffset.Now, DurationSeconds = 5000 };
        var summary = $"## Action items\n- [ ] Paul - send the deck {time}";
        var item = Assert.Single(Lemur.Pipeline.ActionItems.Read(summary, new DateOnly(2026, 10, 14), 5000, ["Paul"], []).Items);
        Assert.Equal(seconds, item.At);

        var page = Between(NoteHtml.Render(meta, summary, [Said(0, 5, "Paul", me: true)], "fake", null, audio: "a.mp3"),
            "<section class=\"summary\">", "</section>");
        Assert.Contains($"Paul - send the deck <a href=\"a.mp3#t={seconds}\" class=\"moment\" data-t=\"{seconds}\" title=\"Play from here\">{clock}</a></li>", page);
    }

    [Fact]
    public void Without_the_recording_times_stay_as_written_and_there_is_no_script()
    {
        var page = NoteHtml.Render(Meta, ActionItems, [Said(0, 5, "Paul", me: true)], "fake", null);
        Assert.Contains("Paul - send the deck - Fri 2026-10-16 [00:02:34]</li>", page);
        Assert.DoesNotContain("class=\"moment\"", page);
        Assert.DoesNotContain("<script", page);
        Assert.DoesNotContain("script-src", page);
        Assert.Contains("content=\"default-src 'none'; style-src 'unsafe-inline'\"", page);

        // Items without times, with the recording: as before there were any.
        var plain = Between(NoteHtml.Render(Meta, "## Action items\n- [ ] Paul - send the deck - Fri 2026-10-16", [], "fake", null, audio: "a.mp3"),
            "<section class=\"summary\">", "</section>");
        Assert.Contains("<input disabled=\"disabled\" type=\"checkbox\" /> Paul - send the deck - Fri 2026-10-16</li>", plain);
        Assert.DoesNotContain("<a ", plain);
    }

    [Fact]
    public void With_the_recording_the_player_runs_by_its_hash_alone()
    {
        var page = NoteHtml.Render(Meta, ActionItems, [Said(0, 5, "Paul", me: true), Said(6, 5, "Dana")], "fake", null, audio: "a.mp3");
        Assert.Equal(3, NoteHtml.PageVersion);

        var script = Between(page, "<script>", "</script>");
        Assert.Equal(NoteHtml.PlayerScript, script);
        Assert.DoesNotContain("\r", script); // browsers hash it with LF
        Assert.True(script.All(char.IsAscii)); // and its bytes can't depend on the page's encoding
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(page, "<script"));
        var csp = Between(page, "<meta http-equiv=\"Content-Security-Policy\" content=\"", "\">");
        Assert.Equal($"default-src 'none'; style-src 'unsafe-inline'; media-src 'self' file:; script-src '{NoteHtml.ScriptHash(script)}'", csp);
        var expected = "sha256-" + Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(script)));
        Assert.Contains(expected, csp);

        // The page opens at a moment from its address, and plays an item's time; nothing else is read from the page.
        Assert.Contains("location.hash", script);
        Assert.Contains("hashchange", script);
        Assert.Contains("a.moment", script);
        Assert.DoesNotContain("innerHTML", script);
        Assert.DoesNotContain("eval", script);
    }

    static string Between(string s, string start, string end)
    {
        int from = s.IndexOf(start, StringComparison.Ordinal) + start.Length;
        return s[from..s.IndexOf(end, from, StringComparison.Ordinal)];
    }

    [Fact]
    public void Without_the_named_list_labels_decide_and_names_are_escaped()
    {
        var page = NoteHtml.Render(Meta, "## Summary\nx", [Said(0, 5, "<b>Dana</b>"), Said(5, 5, "Speaker C"), Said(10, 5, "Sam?")], "fake", null);
        Assert.Contains("<li class=\"s0\"><b>&lt;b&gt;Dana&lt;/b&gt;</b><span>5 s</span></li>", page);
        Assert.Contains("<li class=\"s1 unnamed\"><b>Speaker C</b>", page);
        Assert.Contains("<li class=\"s2 unnamed\"><b>Sam?</b>", page);
        Assert.DoesNotContain("class=\"attendees\"", NoteHtml.Render(Meta, "## Summary\nx", [], "fake", null));
    }

    [Fact]
    public async Task Older_pages_are_made_again_from_the_meetings_files_but_the_note_is_left_alone()
    {
        var s = new AppSettings
        {
            DisplayName = "Paul", RecordingsFolder = Path.Combine(_root, "rec"), NotesFolder = Path.Combine(_root, "notes"),
            Backend = { Type = BackendTypes.Scriberr, Scriberr = { BaseUrl = "https://x" } },
        };
        var rec = RecordingFolder.CreateNew(s.ResolvedRecordingsFolder, DateTimeOffset.Now);
        int rate = AnchoredTrackWriter.SampleRate;
        var mic = new float[2 * rate];
        var others = new float[2 * rate];
        for (int i = 0; i < rate; i++) mic[i] = 0.2f * MathF.Sin(i * 0.2f);
        for (int i = rate; i < 2 * rate; i++) others[i] = 0.2f * MathF.Sin(i * 0.3f);
        using (var a = new WavSampleSink(rec.MicWav)) a.Write(mic);
        using (var b = new WavSampleSink(rec.OthersWav)) b.Write(others);
        rec.SaveMeta(new RecordingMeta { Title = "Standup", Source = "Teams", StartedAt = DateTimeOffset.Now, DurationSeconds = 2 });
        rec.SaveState(new ProcessingState());
        var state = await new MeetingProcessor(s, () => new FakeBackend()).ProcessAsync(rec, CancellationToken.None);
        Assert.Equal(NoteHtml.PageVersion, state.PageVersion);

        // As a page from before the attendees list: no layout recorded.
        var page = NoteWriter.HtmlPath(state.NotePath!);
        File.WriteAllText(page, "<!doctype html><title>old</title>");
        rec.UpdateState(st => st.PageVersion = 0);
        File.AppendAllText(state.NotePath!, "\nMy edit.\n");
        var note = File.ReadAllText(state.NotePath!);

        Assert.Equal(1, NotePages.RefreshAll(s.ResolvedRecordingsFolder, "Paul"));
        Assert.Contains("class=\"attendees\"", File.ReadAllText(page));
        Assert.Equal(note, File.ReadAllText(state.NotePath!));
        Assert.Equal(NoteHtml.PageVersion, rec.LoadState().PageVersion);
        Assert.Equal(0, NotePages.RefreshAll(s.ResolvedRecordingsFolder, "Paul")); // done once
    }
}
