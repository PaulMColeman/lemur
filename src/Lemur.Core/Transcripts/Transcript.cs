namespace Lemur.Transcripts;

/// <summary>One word and when it was said, from engines that time their words (Scriberr's word_segments, OpenAI's words).</summary>
/// <param name="Speaker">The engine's speaker for this word, if it gives one per word.</param>
public sealed record TranscriptWord(double Start, double End, string Text, string? Speaker = null);

/// <summary>One stretch of speech as the engine returned it. Speaker is the engine's own label.</summary>
/// <param name="Words">Its words with their times, when the engine gives them and they spell out <paramref name="Text"/>;
/// null otherwise (and in transcripts from before Lemur read them, 2026-10-14).</param>
public sealed record TranscriptSegment(double Start, double End, string Speaker, string Text, IReadOnlyList<TranscriptWord>? Words = null);

/// <param name="JobId">Backend's id for the job, if it keeps one (Scriberr does).</param>
/// <param name="WebUrl">Where the user can see the job in a browser, if anywhere.</param>
public sealed record TranscriptResult(IReadOnlyList<TranscriptSegment> Segments, string? JobId, string? WebUrl);

/// <summary>A segment after naming: Speaker is the display name or "Speaker A/B/…".</summary>
public sealed record LabelledSegment(double Start, double End, string Speaker, string Text, bool IsMe, string EngineSpeaker);

public static class TranscriptWords
{
    /// <summary>A word this far outside its segment's times still belongs to it (engines round their times).</summary>
    const double Slack = 0.05;

    /// <summary>
    /// Gives each segment the words timed inside it (or, for a word on a boundary, the segment it overlaps most), but only
    /// when they spell out its text: the same letters and digits in the same order. Words that don't match their line
    /// could only mislead whoever reads them, so that segment gets none.
    /// </summary>
    public static IReadOnlyList<TranscriptSegment> Attach(IReadOnlyList<TranscriptSegment> segments, IReadOnlyList<TranscriptWord> words)
    {
        if (words.Count == 0 || segments.Count == 0) return segments;
        var lists = segments.Select(_ => new List<TranscriptWord>()).ToArray();
        foreach (var w in words)
        {
            int best = -1;
            double most = 0;
            for (int i = 0; i < segments.Count; i++)
            {
                var s = segments[i];
                if (w.Start >= s.Start - Slack && w.End <= s.End + Slack)
                {
                    best = i;
                    break;
                }
                double overlap = Math.Min(w.End, s.End) - Math.Max(w.Start, s.Start);
                if (overlap > most)
                {
                    most = overlap;
                    best = i;
                }
            }
            if (best >= 0) lists[best].Add(w);
        }
        return segments
            .Select((s, i) => lists[i].Count > 0 && Letters(string.Join(" ", lists[i].Select(w => w.Text))) == Letters(s.Text)
                ? s with { Words = lists[i] }
                : s)
            .ToList();
    }

    /// <summary>The words joined as the engine wrote the line, when they say it exactly (punctuation included), else null.</summary>
    public static string? Spelled(IReadOnlyList<TranscriptWord> words, string text)
    {
        var joined = Spaced(string.Join(" ", words.Select(w => w.Text)));
        return joined == Spaced(text) ? joined : null;
    }

    static string Letters(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    static string Spaced(string s) => string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

public static class TranscriptFormat
{
    public static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }

    /// <summary>Consecutive segments by the same speaker become one turn.</summary>
    public static IReadOnlyList<LabelledSegment> MergeTurns(IReadOnlyList<LabelledSegment> segments)
    {
        var turns = new List<LabelledSegment>();
        foreach (var s in segments)
        {
            if (string.IsNullOrWhiteSpace(s.Text)) continue;
            if (turns.Count > 0 && turns[^1].Speaker == s.Speaker)
            {
                var last = turns[^1];
                turns[^1] = last with { End = s.End, Text = $"{last.Text.TrimEnd()} {s.Text.Trim()}" };
            }
            else
            {
                turns.Add(s with { Text = s.Text.Trim() });
            }
        }
        return turns;
    }

    /// <summary>"[00:01:23] Paul: …" — the form the summary model reads.</summary>
    public static string ToPlainText(IReadOnlyList<LabelledSegment> segments) =>
        string.Join("\n", MergeTurns(segments).Select(t => $"[{Clock(t.Start)}] {t.Speaker}: {t.Text}"));
}
