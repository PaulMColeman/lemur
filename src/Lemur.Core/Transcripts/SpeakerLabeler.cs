using Lemur.Audio;

namespace Lemur.Transcripts;

/// <param name="MeEngineSpeaker">The engine label that is mostly the user (to rename in the backend), if any.</param>
/// <param name="ThresholdDb">How much louder than the call the mic must be for a moment to count as the user; NaN when not used.</param>
/// <param name="UsedTrackComparison">False for recordings with nothing from the PC (in-person), where every voice is on the mic.</param>
/// <param name="SplitLines">Lines the engine made of the user's and someone else's words, split where the track changed.</param>
public sealed record LabelResult(
    IReadOnlyList<LabelledSegment> Segments,
    string? MeEngineSpeaker,
    double ThresholdDb,
    bool UsedTrackComparison,
    int SplitLines = 0);

/// <summary>
/// Names the user without any voice enrollment. During a call the user's own voice is on the mic track and absent from the
/// "everyone else" track (Teams never plays you back to yourself), while remote voices are the opposite. So each word goes to
/// the track it was spoken on: the one that led in most of the word's 50 ms moments where someone was talking. A line the
/// engine made of two people's words is split where the track changes; a line without word times (other engines, older
/// transcripts) goes whole to the track that led in most of its moments. How far the mic must lead is found per meeting:
/// across a meeting the moments fall into two clumps, and the cut between them (Otsu's method) adapts to headsets, speakers
/// and mic gain without tuning.
/// It counts moments rather than averaging loudness over a line: an average is ruled by its loudest moments, and the call is
/// often recorded louder than the mic, so one "mm-hmm" from the call in five seconds of the user gave the whole line to
/// someone else. On Paul's calls of 2026-10-14 that was up to 7 minutes of his speech in one meeting.
/// </summary>
public static class SpeakerLabeler
{
    /// <summary>Never call a moment "me" unless the mic is at least this much louder than the call audio.</summary>
    public const double MinThresholdDb = 3;

    /// <summary>...and a line "me" unless the mic is at least this far above its own noise floor.</summary>
    public const double AboveNoiseFloorDb = 6;

    /// <summary>An engine speaker is "the user's cluster" when this share of its speech was the user.</summary>
    public const double ClusterShare = 0.6;

    /// <summary>A track is talking in a moment within this much of its loud moments (95th percentile)...</summary>
    public const double TalkingBelowPeakDb = 30;

    /// <summary>...and at least this far above its quiet ones (10th percentile), so a noisy mic's hiss isn't talking.</summary>
    public const double TalkingAboveFloorDb = 10;

    /// <summary>A line is split only where this many words in a row came from the other track: a single word is more likely
    /// timed across a turn boundary, or an "mm-hmm" not worth a line of its own.</summary>
    public const int MinWordsToSplit = 2;

    /// <param name="letters">Engine speaker → "Speaker X" to keep from an earlier labelling (a meeting made again), so
    /// people keep the letters its notes had; anyone else gets the next letter free.</param>
    public static LabelResult Label(IReadOnlyList<TranscriptSegment> segments, Envelope mic, Envelope others, string displayName,
        IReadOnlyDictionary<string, string>? letters = null)
    {
        // In-person recordings (or a muted PC): nothing came out of the speakers, so every voice in
        // the room is on the mic and loudness says nothing about who is who.
        bool comparable = segments.Count > 0 &&
                          others.Percentile(95) > Mixer.SilentTrackDb &&
                          mic.Percentile(95) > Mixer.SilentTrackDb;
        var parts = new List<Part>(segments.Count);
        double threshold = double.NaN;
        int split = 0;
        if (comparable)
        {
            var moments = new Moments(mic, others);
            threshold = Math.Max(moments.Cut(segments), MinThresholdDb);
            double micFloor = mic.Percentile(10);
            foreach (var s in segments)
            {
                var pieces = Pieces(s, moments, threshold);
                if (pieces.Count > 1) split++;
                foreach (var p in pieces)
                    parts.Add(p.Me && !(mic.MeanDb(p.Start, p.End) > micFloor + AboveNoiseFloorDb) ? p with { Me = false } : p);
            }
        }
        else
        {
            parts.AddRange(segments.Select(s => new Part(s.Start, s.End, s.Text, false, s.Speaker)));
        }

        // Which engine label is (mostly) the user? Rename that one in the backend's UI. Of the labels that are mostly the
        // user, the one with the most of them: a half-second scrap the engine gave no speaker ("?") is all user too.
        string? meCluster = comparable
            ? parts
                .GroupBy(p => p.Engine)
                .Select(g => (Speaker: g.Key, Mine: g.Sum(p => p.Me ? p.End - p.Start : 0), All: g.Sum(p => p.End - p.Start)))
                .Where(x => x.Mine >= ClusterShare * Math.Max(1e-6, x.All))
                .OrderByDescending(x => x.Mine)
                .Select(x => x.Speaker)
                .FirstOrDefault()
            : null;

        // Everyone else becomes Speaker A, B, … in order of first appearance.
        var given = new Dictionary<string, string>(letters ?? new Dictionary<string, string>());
        var taken = new HashSet<string>(given.Values);
        int next = 0;
        var labelled = new List<LabelledSegment>(parts.Count);
        foreach (var p in parts)
        {
            string name;
            if (p.Me)
            {
                name = displayName;
            }
            else if (!given.TryGetValue(p.Engine, out var letterName))
            {
                do name = $"Speaker {LetterFor(next++)}"; while (taken.Contains(name));
                given[p.Engine] = name;
                taken.Add(name);
            }
            else
            {
                name = letterName;
            }
            labelled.Add(new LabelledSegment(p.Start, p.End, name, p.Text, p.Me, p.Engine));
        }

        return new LabelResult(labelled, meCluster, threshold, comparable, split);
    }

    /// <summary>A line, or part of one, and whether it was the user.</summary>
    sealed record Part(double Start, double End, string Text, bool Me, string Engine);

    /// <summary>The segment whole, or cut where its words change track.</summary>
    static List<Part> Pieces(TranscriptSegment s, Moments moments, double cut)
    {
        var whole = new Part(s.Start, s.End, s.Text, moments.Leads(s.Start, s.End, cut), s.Speaker);
        if (s.Words is not { Count: > 0 } words) return [whole];
        var owners = words.Select(w => moments.Owner(w.Start, w.End, cut)).ToList();
        if (owners.All(o => o is null)) return [whole];

        var runs = Runs(owners);
        if (runs.Count == 1) return [whole with { Me = runs[0].Mine }];
        if (TranscriptWords.Spelled(words, s.Text) is null)
        {
            // Words that don't say the line exactly (without its punctuation, say) decide, but can't be cut into lines.
            double mine = 0, theirs = 0;
            for (int i = 0; i < words.Count; i++)
            {
                if (owners[i] == true) mine += words[i].End - words[i].Start;
                else if (owners[i] == false) theirs += words[i].End - words[i].Start;
            }
            return [whole with { Me = mine > theirs }];
        }

        var parts = new List<Part>(runs.Count);
        foreach (var (from, to, mineRun) in runs)
        {
            var ws = words.Skip(from).Take(to - from + 1).ToList();
            double start = from == 0 ? s.Start : ws[0].Start;
            double end = to == words.Count - 1 ? s.End : ws[^1].End;
            // The engine's speaker for these words, where it gives one per word: the part may be someone else than the line was.
            var engine = ws.Where(w => w.Speaker is not null).GroupBy(w => w.Speaker!).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
            parts.Add(new Part(start, Math.Max(start, end), string.Join(" ", ws.Select(w => w.Text)), mineRun, engine ?? s.Speaker));
        }
        return parts;
    }

    /// <summary>
    /// Consecutive words from the same track, as word ranges. Undecided words (nobody audibly talking) go with the word
    /// before them, or after them at the start of a line, but don't count: a run with fewer than
    /// <see cref="MinWordsToSplit"/> decided words takes its neighbours' track and joins them, the least decided first.
    /// (At 5:10 in Paul's call, one word under someone's murmur and one too quiet to judge made a run of two.)
    /// </summary>
    internal static List<(int From, int To, bool Mine)> Runs(IReadOnlyList<bool?> owners)
    {
        var filled = owners.ToArray();
        for (int i = 1; i < filled.Length; i++) filled[i] ??= filled[i - 1];
        for (int i = filled.Length - 2; i >= 0; i--) filled[i] ??= filled[i + 1];

        var runs = new List<(int From, int To, bool Mine)>();
        for (int i = 0; i < filled.Length; i++)
        {
            bool mine = filled[i] ?? false;
            if (runs.Count > 0 && runs[^1].Mine == mine) runs[^1] = runs[^1] with { To = i };
            else runs.Add((i, i, mine));
        }

        int Decided((int From, int To, bool Mine) run)
        {
            int n = 0;
            for (int i = run.From; i <= run.To; i++) if (owners[i] is not null) n++;
            return n;
        }

        while (runs.Count > 1)
        {
            int weakest = -1;
            for (int i = 0; i < runs.Count; i++)
                if (Decided(runs[i]) < MinWordsToSplit && (weakest < 0 || Decided(runs[i]) < Decided(runs[weakest]))) weakest = i;
            if (weakest < 0) break;
            // Runs alternate between the tracks, so its neighbours are on the other one: taking theirs joins all three.
            int first = Math.Max(0, weakest - 1), last = Math.Min(runs.Count - 1, weakest + 1);
            var joined = (runs[first].From, runs[last].To, !runs[weakest].Mine);
            runs.RemoveRange(first, last - first + 1);
            runs.Insert(first, joined);
        }
        return runs;
    }

    /// <summary>The two tracks moment by moment (the envelopes' 50 ms frames).</summary>
    sealed class Moments
    {
        readonly Envelope _mic, _others;
        readonly int _count;
        readonly double _micTalking, _othersTalking;

        public Moments(Envelope mic, Envelope others)
        {
            _mic = mic;
            _others = others;
            _count = Math.Min(mic.Db.Length, others.Db.Length);
            _micTalking = TalkingLevel(mic);
            _othersTalking = TalkingLevel(others);
        }

        static double TalkingLevel(Envelope e) =>
            Math.Max(e.Percentile(95) - TalkingBelowPeakDb, e.Percentile(10) + TalkingAboveFloorDb);

        (int From, int To) Frames(double start, double end)
        {
            var (a, b) = _mic.Frames(start, end);
            return (Math.Min(a, _count), Math.Min(b, _count));
        }

        bool Talking(int k) => _mic.Db[k] > _micTalking || _others.Db[k] > _othersTalking;

        double Lead(int k) => _mic.Db[k] - _others.Db[k];

        /// <summary>The meeting's cut: Otsu over how far the mic led in each moment someone talked during a line.</summary>
        public double Cut(IReadOnlyList<TranscriptSegment> segments)
        {
            var seen = new bool[_count];
            var leads = new List<double>();
            foreach (var s in segments)
            {
                var (a, b) = Frames(s.Start, s.End);
                for (int k = a; k < b; k++)
                {
                    if (seen[k] || !Talking(k)) continue;
                    seen[k] = true;
                    leads.Add(Lead(k));
                }
            }
            return Otsu(leads, leads.Select(_ => 1.0).ToList());
        }

        /// <summary>True when the mic led by more than the cut in most of the moments someone was talking, false when it
        /// didn't, null when nobody was audibly talking (or it was even).</summary>
        public bool? Owner(double start, double end, double cut)
        {
            var (a, b) = Frames(start, end);
            int talking = 0, mine = 0;
            for (int k = a; k < b; k++)
            {
                if (!Talking(k)) continue;
                talking++;
                if (Lead(k) > cut) mine++;
            }
            return talking == 0 || 2 * mine == talking ? null : 2 * mine > talking;
        }

        /// <summary>A whole line: the mic led in most of its talking moments; with none, its average loudness decides.</summary>
        public bool Leads(double start, double end, double cut) =>
            Owner(start, end, cut) ?? _mic.MeanDb(start, end) - _others.MeanDb(start, end) > cut;
    }

    static string LetterFor(int index) =>
        index < 26 ? ((char)('A' + index)).ToString() : $"{(char)('A' + index / 26 - 1)}{(char)('A' + index % 26)}";

    /// <summary>Weighted Otsu threshold over 1 dB bins: the cut that best separates two clumps.</summary>
    internal static double Otsu(IReadOnlyList<double> values, IReadOnlyList<double> weights)
    {
        if (values.Count == 0) return 0;
        const double lo = -80, hi = 80;
        const int bins = 160;
        var hist = new double[bins];
        double total = 0, sumAll = 0;
        for (int i = 0; i < values.Count; i++)
        {
            double v = Math.Clamp(values[i], lo, hi - 1e-9);
            int b = (int)(v - lo);
            hist[b] += weights[i];
            total += weights[i];
            sumAll += weights[i] * (lo + b + 0.5);
        }

        double bestBetween = -1, best = 0, weightBelow = 0, sumBelow = 0;
        for (int b = 0; b < bins - 1; b++)
        {
            weightBelow += hist[b];
            sumBelow += hist[b] * (lo + b + 0.5);
            double weightAbove = total - weightBelow;
            if (weightBelow <= 0 || weightAbove <= 0) continue;
            double meanBelow = sumBelow / weightBelow;
            double meanAbove = (sumAll - sumBelow) / weightAbove;
            double between = weightBelow * weightAbove * (meanBelow - meanAbove) * (meanBelow - meanAbove);
            if (between > bestBetween)
            {
                bestBetween = between;
                best = lo + b + 1; // boundary above bin b
            }
        }
        return best;
    }
}
