using Lemur.Audio;
using Lemur.Transcripts;

namespace Lemur.Tests;

public class SpeakerLabelerTests
{
    const double Frame = 0.05;

    /// <summary>Builds mic/others envelopes from (start, end, micDb, othersDb) spans over a quiet floor.</summary>
    static (Envelope Mic, Envelope Others) Envelopes(double seconds, params (double Start, double End, float Mic, float Others)[] spans)
    {
        int n = (int)(seconds / Frame);
        var mic = Enumerable.Repeat(-75f, n).ToArray();
        var others = Enumerable.Repeat(-90f, n).ToArray();
        foreach (var s in spans)
            for (int i = (int)(s.Start / Frame); i < (int)(s.End / Frame); i++)
            {
                mic[i] = s.Mic;
                others[i] = s.Others;
            }
        return (new Envelope(Frame, mic), new Envelope(Frame, others));
    }

    [Fact]
    public void Headset_call_names_me_and_letters_everyone_else()
    {
        var (mic, others) = Envelopes(40,
            (0, 10, -22, -90),   // me
            (10, 20, -70, -20),  // remote 1 (faint headset bleed on mic)
            (20, 30, -24, -90),  // me again
            (30, 40, -72, -18)); // remote 2
        var segments = new[]
        {
            new TranscriptSegment(0, 10, "SPEAKER_01", "I'll send the draft."),
            new TranscriptSegment(10, 20, "SPEAKER_00", "Great, thanks."),
            new TranscriptSegment(20, 30, "SPEAKER_01", "And book the room."),
            new TranscriptSegment(30, 40, "SPEAKER_02", "I can review it Friday."),
        };
        var r = SpeakerLabeler.Label(segments, mic, others, "Paul");
        Assert.True(r.UsedTrackComparison);
        Assert.Equal(["Paul", "Speaker A", "Paul", "Speaker B"], r.Segments.Select(s => s.Speaker));
        Assert.Equal("SPEAKER_01", r.MeEngineSpeaker);
    }

    [Fact]
    public void Speakers_with_echo_still_separate_by_loudness()
    {
        // No headset: remote voices reach the mic, but quieter than the call audio itself.
        var (mic, others) = Envelopes(30,
            (0, 10, -20, -60),
            (10, 20, -38, -20),
            (20, 30, -21, -58));
        var segments = new[]
        {
            new TranscriptSegment(0, 10, "A", "me"),
            new TranscriptSegment(10, 20, "B", "them"),
            new TranscriptSegment(20, 30, "A", "me"),
        };
        var r = SpeakerLabeler.Label(segments, mic, others, "Paul");
        Assert.Equal(["Paul", "Speaker A", "Paul"], r.Segments.Select(s => s.Speaker));
    }

    [Fact]
    public void Silent_user_is_never_invented()
    {
        // You only listened. All segments are remote; the threshold floor stops any "me".
        var (mic, others) = Envelopes(30, (0, 10, -70, -20), (10, 20, -72, -22), (20, 30, -71, -19));
        var segments = new[]
        {
            new TranscriptSegment(0, 10, "A", "one"),
            new TranscriptSegment(10, 20, "B", "two"),
            new TranscriptSegment(20, 30, "A", "three"),
        };
        var r = SpeakerLabeler.Label(segments, mic, others, "Paul");
        Assert.DoesNotContain(r.Segments, s => s.IsMe);
        Assert.Null(r.MeEngineSpeaker);
        Assert.Equal(["Speaker A", "Speaker B", "Speaker A"], r.Segments.Select(s => s.Speaker));
    }

    [Fact]
    public void In_person_recording_does_not_compare_tracks()
    {
        // Nothing came out of the PC: everyone in the room is on the mic.
        var (mic, others) = Envelopes(20, (0, 10, -20, -95), (10, 20, -25, -95));
        var segments = new[] { new TranscriptSegment(0, 10, "A", "hi"), new TranscriptSegment(10, 20, "B", "hello") };
        var r = SpeakerLabeler.Label(segments, mic, others, "Paul");
        Assert.False(r.UsedTrackComparison);
        Assert.True(double.IsNaN(r.ThresholdDb));
        Assert.Equal(["Speaker A", "Speaker B"], r.Segments.Select(s => s.Speaker));
    }

    [Fact]
    public void A_short_sound_from_the_call_does_not_take_your_line()
    {
        // Paul's call of 2026-10-14 at 5:10: someone on the call murmured over the first second of six seconds of him, and
        // the call is recorded louder than his mic. Averaged over the line that gave it to "Speaker B"; counted moment by
        // moment it is his.
        var (mic, others) = Envelopes(20,
            (1, 2, -30, -20),    // he starts; someone murmurs over him
            (2, 7, -30, -90),    // just him
            (8, 14, -75, -16));  // someone else's line
        var segments = new[]
        {
            new TranscriptSegment(1, 7, "SPEAKER_03", "So the plan is to ship it on Friday."),
            new TranscriptSegment(8, 14, "SPEAKER_05", "Sounds good."),
        };
        var r = SpeakerLabeler.Label(segments, mic, others, "Paul");
        Assert.Equal(["Paul", "Speaker A"], r.Segments.Select(s => s.Speaker));
        Assert.True(mic.MeanDb(1, 7) - others.MeanDb(1, 7) < SpeakerLabeler.MinThresholdDb); // what the old average saw
    }

    static TranscriptWord W(double start, double end, string text, string? speaker = "SPEAKER_01") => new(start, end, text, speaker);

    [Fact]
    public void A_line_holding_two_people_is_split_where_the_track_changes()
    {
        var (mic, others) = Envelopes(10,
            (0, 1.6, -75, -20),   // someone on the call asks
            (2, 3.4, -25, -90));  // the user answers
        var segments = new[]
        {
            new TranscriptSegment(0, 3.5, "SPEAKER_01", "Can you send it? Yes, I will.",
                [W(0, 0.4, "Can"), W(0.4, 0.8, "you"), W(0.8, 1.2, "send"), W(1.2, 1.6, "it?"),
                 W(2.0, 2.4, "Yes,"), W(2.4, 2.8, "I"), W(2.8, 3.4, "will.")]),
        };
        var r = SpeakerLabeler.Label(segments, mic, others, "Paul");
        Assert.Equal(1, r.SplitLines);
        Assert.Equal([("Speaker A", "Can you send it?", 0.0, 1.6, false), ("Paul", "Yes, I will.", 2.0, 3.5, true)],
            r.Segments.Select(s => (s.Speaker, s.Text, s.Start, s.End, s.IsMe)));
    }

    [Fact]
    public void A_part_said_by_someone_else_takes_their_speaker_from_the_words()
    {
        // The engine put an interruption in the user's line; its own word labels say who it was.
        var (mic, others) = Envelopes(20, (0, 2, -25, -90), (2, 3.2, -75, -20), (3.2, 5, -25, -90), (6, 10, -75, -18));
        var segments = new[]
        {
            new TranscriptSegment(0, 5, "SPEAKER_00", "We ship Friday. Wait, which Friday? The next one.",
                [W(0, 0.6, "We", "SPEAKER_00"), W(0.6, 1.2, "ship", "SPEAKER_00"), W(1.2, 2.0, "Friday.", "SPEAKER_00"),
                 W(2.0, 2.4, "Wait,", "SPEAKER_02"), W(2.4, 2.8, "which", "SPEAKER_02"), W(2.8, 3.2, "Friday?", "SPEAKER_02"),
                 W(3.2, 3.6, "The", "SPEAKER_00"), W(3.6, 4.2, "next", "SPEAKER_00"), W(4.2, 5.0, "one.", "SPEAKER_00")]),
            new TranscriptSegment(6, 10, "SPEAKER_02", "Got it."),
        };
        var r = SpeakerLabeler.Label(segments, mic, others, "Paul");
        Assert.Equal(["Paul", "Speaker A", "Paul", "Speaker A"], r.Segments.Select(s => s.Speaker));
        Assert.Equal("Wait, which Friday?", r.Segments[1].Text);
        Assert.Equal("SPEAKER_00", r.MeEngineSpeaker);
    }

    [Fact]
    public void One_word_from_the_other_track_does_not_split_a_line()
    {
        // The first word timed over the end of someone else's sentence: noise at a turn boundary, not a turn.
        var (mic, others) = Envelopes(10, (0, 0.4, -75, -20), (0.4, 3, -25, -90), (4, 8, -75, -18));
        var segments = new[]
        {
            new TranscriptSegment(0, 3, "SPEAKER_00", "Right, so here is the plan.",
                [W(0, 0.4, "Right,"), W(0.4, 0.8, "so"), W(0.8, 1.2, "here"), W(1.2, 1.6, "is"), W(1.6, 2.0, "the"), W(2.0, 2.6, "plan.")]),
            new TranscriptSegment(4, 8, "SPEAKER_01", "Great."),
        };
        var r = SpeakerLabeler.Label(segments, mic, others, "Paul");
        Assert.Equal(0, r.SplitLines);
        Assert.Equal([("Paul", "Right, so here is the plan."), ("Speaker A", "Great.")], r.Segments.Select(s => (s.Speaker, s.Text)));
    }

    [Fact]
    public void Words_that_do_not_say_the_line_exactly_decide_whose_it_is_but_do_not_cut_it()
    {
        // OpenAI-style words carry no punctuation: cutting with them would lose it, so the line stays whole.
        var (mic, others) = Envelopes(10, (0, 0.8, -75, -20), (1.2, 3.8, -25, -90), (5, 8, -75, -18));
        var segments = new[]
        {
            new TranscriptSegment(0, 4, "A", "Okay, good. Then I will send the deck today.",
                [W(0, 0.4, "Okay"), W(0.4, 0.8, "good"), W(1.2, 1.6, "Then"), W(1.6, 1.8, "I"), W(1.8, 2.2, "will"),
                 W(2.2, 2.6, "send"), W(2.6, 2.8, "the"), W(2.8, 3.2, "deck"), W(3.2, 3.8, "today")]),
            new TranscriptSegment(5, 8, "B", "Thanks."),
        };
        var r = SpeakerLabeler.Label(segments, mic, others, "Paul");
        Assert.Equal([("Paul", "Okay, good. Then I will send the deck today."), ("Speaker A", "Thanks.")],
            r.Segments.Select(s => (s.Speaker, s.Text)));
    }

    [Fact]
    public void The_users_engine_label_is_the_one_with_most_of_them_not_a_scrap()
    {
        // Paul's 2026-10-14 call: a half-second segment with no speaker ("?") was all him, so it beat his real label on share.
        var (mic, others) = Envelopes(30, (0, 8, -25, -90), (8, 9, -75, -20), (9, 10, -25, -90), (10, 10.5, -25, -90), (12, 20, -75, -18));
        var segments = new[]
        {
            new TranscriptSegment(0, 8, "SPEAKER_03", "a"),
            new TranscriptSegment(8, 9, "SPEAKER_03", "b"),   // someone else, put with his voice by the engine
            new TranscriptSegment(9, 10, "SPEAKER_03", "c"),
            new TranscriptSegment(10, 10.5, "?", "d"),
            new TranscriptSegment(12, 20, "SPEAKER_05", "e"),
        };
        Assert.Equal("SPEAKER_03", SpeakerLabeler.Label(segments, mic, others, "Paul").MeEngineSpeaker);
    }

    /// <summary>m = the mic led in the word, t = the call did, . = nobody audible; the result is each word's track.</summary>
    [Theory]
    [InlineData("tttmttt", "ttttttt")]     // one word between others joins them
    [InlineData("tttmmttt", "tttmmttt")]   // two words in a row are a run of their own
    [InlineData("tmmmm", "mmmmm")]         // a single word at the start joins the run after it
    [InlineData("mttmm", "tttmm")]
    [InlineData("tt.mmm.tt", "tttmmmmtt")] // undecided words go with the word before them
    [InlineData("..mm", "mmmm")]           // ...or after them, at the start
    [InlineData("t.mmmm", "mmmmmm")]       // ...but don't count: Paul's 5:10, one murmured-over word and one too quiet
    public void Single_words_join_their_neighbours(string owners, string expected)
    {
        var runs = SpeakerLabeler.Runs(owners.Select(c => c switch { 'm' => true, 't' => (bool?)false, _ => null }).ToList());
        Assert.Equal(expected, string.Concat(runs.Select(r => new string(r.Mine ? 'm' : 't', r.To - r.From + 1))));
        Assert.Equal(owners.Length, runs[^1].To + 1);
    }

    [Fact]
    public void Otsu_splits_two_clumps_between_them()
    {
        double[] values = [-30, -28, -32, -29, 25, 27, 30, 26];
        double t = SpeakerLabeler.Otsu(values, values.Select(_ => 1.0).ToArray());
        Assert.InRange(t, -28, 25);
    }

    [Fact]
    public void Plain_text_merges_turns_and_stamps_time()
    {
        var segs = new List<LabelledSegment>
        {
            new(61, 62, "Paul", "Hello", true, "A"),
            new(62, 63, "Paul", "everyone.", true, "A"),
            new(64, 66, "Speaker A", "Hi.", false, "B"),
        };
        Assert.Equal("[00:01:01] Paul: Hello everyone.\n[00:01:04] Speaker A: Hi.", TranscriptFormat.ToPlainText(segs));
    }
}
