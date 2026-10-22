using Lemur.Audio;
using Lemur.Transcripts;

namespace Lemur.Speakers;

/// <summary>How a voice reached the PC: through the call (codec, noise suppression) or straight into a mic.</summary>
public enum VoiceKind { Call, Local }

public enum Track { Mic, Others }

public sealed record ClipSpan(double Start, double End)
{
    public double Seconds => End - Start;
}

/// <param name="Key">"me", or the engine's speaker label.</param>
/// <param name="Label">What the notes call this speaker before naming ("Speaker B", or the user's name).</param>
/// <param name="TalkSeconds">All of their speech, for the naming window; clips are a careful subset.</param>
public sealed record SpeakerPlan(
    string Key, string Label, string EngineSpeaker, bool IsMe, VoiceKind Kind, Track Track,
    IReadOnlyList<ClipSpan> Clips, double TalkSeconds)
{
    public double ClipSeconds => Clips.Sum(c => c.Seconds);
    public bool TooLittle => ClipSeconds < ClipPlanner.MinUsableSeconds;
}

/// <summary>
/// Picks the stretches of each speaker that make trustworthy voiceprints (design 3.2): from the track
/// that carries only their side, with nobody else talking, long enough, and spread across the meeting.
/// Bad clips are where wrong names come from, so this errs towards fewer, cleaner clips.
/// </summary>
public static class ClipPlanner
{
    public const double JoinGap = 0.3;
    public const double Trim = 0.2;
    public const double MinRun = 2.0;
    public const double MaxPiece = 6.0;
    public const double MinUsableSeconds = 4.0;
    public const double OverlapGuard = 0.2;
    public const int MaxClips = 6;
    const double SpreadSeconds = 60;
    const double TalkingAboveFloorDb = 6;

    /// <param name="callAudio">False for in-person recordings: every voice is on the mic, nothing on "others". True whenever the
    /// call's track has sound, even with the mic silent (a call you sat through muted).</param>
    public static IReadOnlyList<SpeakerPlan> Plan(IReadOnlyList<LabelledSegment> segments, Envelope mic, Envelope others, bool callAudio)
    {
        var groups = segments
            .Where(s => s.End > s.Start && !string.IsNullOrWhiteSpace(s.Text))
            .GroupBy(s => callAudio && s.IsMe ? "me" : s.EngineSpeaker)
            .ToList();

        // On a call, frames where one side is clearly talking — used to drop crosstalk from the other side's clips.
        var userTalking = callAudio ? Frames(mic, others) : [];
        var remoteTalking = callAudio ? Frames(others, mic) : [];

        var plans = new List<SpeakerPlan>();
        foreach (var g in groups)
        {
            bool isMe = g.Key == "me";
            var track = callAudio && !isMe ? Track.Others : Track.Mic;
            var kind = track == Track.Others ? VoiceKind.Call : VoiceKind.Local;
            var envelope = track == Track.Others ? others : mic;

            var mine = Merge(g.Select(s => new ClipSpan(s.Start, s.End)), 0);
            var theirs = Merge(segments.Where(s => !g.Contains(s)).Select(s => new ClipSpan(s.Start - OverlapGuard, s.End + OverlapGuard)), 0);
            var usable = Subtract(mine, theirs);
            if (callAudio) usable = Subtract(usable, isMe ? remoteTalking : userTalking);

            var pieces = new List<ClipSpan>();
            foreach (var run in Merge(usable, JoinGap))
            {
                var trimmed = new ClipSpan(run.Start + Trim, run.End - Trim);
                if (trimmed.Seconds < MinRun) continue;
                int n = (int)Math.Ceiling(trimmed.Seconds / MaxPiece);
                double len = trimmed.Seconds / n;
                for (int i = 0; i < n; i++) pieces.Add(new ClipSpan(trimmed.Start + i * len, trimmed.Start + (i + 1) * len));
            }

            // Never a clip of silence (a muted mic, a track gated to nothing): its voiceprint matches every other one.
            pieces.RemoveAll(p => envelope.MeanDb(p.Start, p.End) <= Mixer.SilentTrackDb);
            double floor = envelope.Percentile(10);
            var chosen = Choose(pieces, p => envelope.MeanDb(p.Start, p.End) - floor);
            var first = g.First();
            plans.Add(new SpeakerPlan(
                Key: g.Key,
                Label: first.Speaker,
                EngineSpeaker: isMe ? g.GroupBy(s => s.EngineSpeaker).OrderByDescending(x => x.Count()).First().Key : g.Key,
                IsMe: isMe,
                Kind: kind,
                Track: track,
                Clips: chosen,
                TalkSeconds: g.Sum(s => s.End - s.Start)));
        }
        return plans;
    }

    /// <summary>Best first, but each new clip at least a minute from the others while that's possible.</summary>
    static List<ClipSpan> Choose(List<ClipSpan> pieces, Func<ClipSpan, double> score)
    {
        var ranked = pieces.OrderByDescending(p => score(p) + 0.5 * p.Seconds).ToList();
        var chosen = new List<ClipSpan>();
        foreach (var p in ranked)
        {
            if (chosen.Count >= MaxClips) break;
            if (chosen.All(c => Math.Abs(Mid(c) - Mid(p)) >= SpreadSeconds)) chosen.Add(p);
        }
        foreach (var p in ranked)
        {
            if (chosen.Count >= MaxClips) break;
            if (!chosen.Contains(p)) chosen.Add(p);
        }
        return chosen.OrderBy(c => c.Start).ToList();
    }

    static double Mid(ClipSpan c) => (c.Start + c.End) / 2;

    /// <summary>Spans where <paramref name="talker"/> is above its noise floor and louder than <paramref name="other"/>.</summary>
    static List<ClipSpan> Frames(Envelope talker, Envelope other)
    {
        var spans = new List<ClipSpan>();
        if (talker.Db.Length == 0) return spans;
        double floor = talker.Percentile(10) + TalkingAboveFloorDb;
        double f = talker.FrameSeconds;
        for (int i = 0; i < talker.Db.Length; i++)
        {
            double o = i < other.Db.Length ? other.Db[i] : Envelope.SilenceDb;
            if (talker.Db[i] >= floor && talker.Db[i] >= o) spans.Add(new ClipSpan(i * f, (i + 1) * f));
        }
        return Merge(spans, 0);
    }

    /// <summary>Sorted, overlapping or nearly-touching spans joined.</summary>
    internal static List<ClipSpan> Merge(IEnumerable<ClipSpan> spans, double gap)
    {
        var result = new List<ClipSpan>();
        foreach (var s in spans.Where(s => s.End > s.Start).OrderBy(s => s.Start))
        {
            if (result.Count > 0 && s.Start <= result[^1].End + gap)
                result[^1] = result[^1] with { End = Math.Max(result[^1].End, s.End) };
            else
                result.Add(s);
        }
        return result;
    }

    /// <summary><paramref name="from"/> minus <paramref name="cut"/>; both sorted and merged.</summary>
    internal static List<ClipSpan> Subtract(List<ClipSpan> from, List<ClipSpan> cut)
    {
        var result = new List<ClipSpan>();
        int j = 0;
        foreach (var s in from)
        {
            double start = s.Start;
            while (j < cut.Count && cut[j].End <= start) j++;
            int k = j;
            while (k < cut.Count && cut[k].Start < s.End)
            {
                if (cut[k].Start > start) result.Add(new ClipSpan(start, cut[k].Start));
                start = Math.Max(start, cut[k].End);
                k++;
            }
            if (start < s.End) result.Add(new ClipSpan(start, s.End));
        }
        return result;
    }
}
