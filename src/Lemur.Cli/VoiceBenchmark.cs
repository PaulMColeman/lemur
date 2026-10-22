using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using Lemur.Audio;
using Lemur.Config;
using Lemur.Speakers;
using Lemur.Transcripts;

namespace Lemur.Cli;

/// <summary>
/// S1's benchmark (design §9): the AMI scenario meetings, four meetings per series with the same four
/// people. Each series' voices are learned from meeting a, using AMI's own speaker labels, then every
/// speaker in meetings b–d is matched against everyone learned — about 40 voices, so each test is also a
/// test against many voices it must not match. Uses the recorder's real clip selection and matching;
/// fits the five Careful → Eager levels and prints them as speakers.thresholds JSON.
/// </summary>
static class VoiceBenchmark
{
    sealed record Meeting(string Id, string Series, char Session, string Wav, List<LabelledSegment> Segments);

    /// <param name="Short">One clip (≤ 6 s) rather than the meeting's whole voiceprint for that speaker.</param>
    sealed record Trial(string Truth, string Meeting, float[] Vector, bool Short, bool Call);

    sealed record Scored(Trial Trial, string Best, double BestScore, double Second, double TruthScore);

    /// <summary>Allowed wrong names per level: share of short clips, and count of speaker-meetings.</summary>
    static readonly (double Clips, int Meetings)[] Budgets = [(0.001, 0), (0.005, 1), (0.01, 1), (0.02, 2), (0.04, 3)];

    /// <summary>Percentile of wrong-person short-clip scores each level must stay above (Careful → Eager).</summary>
    static readonly double[] Floors = [99.9, 99.5, 99, 98, 95];

    static (double Named, int WrongMeetings, double WrongClips) Evaluate(List<Scored> scored, double accept, double margin)
    {
        bool Named(Scored s) => s.BestScore >= accept && s.BestScore - s.Second >= margin;
        var meetings = scored.Where(s => !s.Trial.Short).ToList();
        var clips = scored.Where(s => s.Trial.Short).ToList();
        return (meetings.Count(s => Named(s) && s.Best == s.Trial.Truth) / (double)Math.Max(1, meetings.Count),
            meetings.Count(s => Named(s) && s.Best != s.Trial.Truth),
            clips.Count == 0 ? 0 : clips.Count(s => Named(s) && s.Best != s.Trial.Truth) / (double)clips.Count);
    }

    public static async Task<int> Run(AppSettings settings, string[] args)
    {
        string Opt(string name) => Program_Opt(args, name) ?? throw new ArgumentException($"{name} is required");
        var audioDir = Opt("--ami");
        var zipPath = Opt("--annotations");
        var url = Program_Opt(args, "--url") ?? settings.Speakers.ResolvedBaseUrl(settings);
        var model = Program_Opt(args, "--model") ?? settings.Speakers.Model;
        var key = args.Contains("--no-key") ? null : VoiceprintClient.KeyFor(settings, new WindowsCredentialStore());
        bool opus = args.Contains("--opus");
        var outFile = Program_Opt(args, "--out");

        var meetings = LoadMeetings(audioDir, zipPath, Program_Opt(args, "--series")?.Split(','));
        var seriesCount = meetings.Select(m => m.Series).Distinct().Count();
        Console.WriteLine($"{meetings.Count} meetings in {seriesCount} series; service {url} model {model}{(opus ? "; tests also through a Teams-like codec" : "")}");

        using var client = new VoiceprintClient(url, model, key);
        var sw = Stopwatch.StartNew();
        var calibration = (await client.EmbedAsync([Calibration.SignalWav()], CancellationToken.None))[0];

        var trials = new List<Trial>();
        var enrolled = new Dictionary<string, float[]>();
        var enrolledCall = new Dictionary<string, float[]>();
        int clipCount = 0;
        double clipSeconds = 0;
        foreach (var m in meetings)
        {
            var env = Mixer.MeasureEnvelope(m.Wav);
            var plans = ClipPlanner.Plan(m.Segments, env, new Envelope(env.FrameSeconds, []), callAudio: false)
                .Where(p => !p.TooLittle).ToList();
            var clips = plans.SelectMany(p => p.Clips.Select(c => (Plan: p, Clip: c, Samples: Wav.Read(m.Wav, c.Start, c.End)))).ToList();
            clipCount += clips.Count;
            clipSeconds += clips.Sum(c => c.Clip.Seconds);

            var direct = await client.EmbedAsync(clips.Select(c => Wav.Encode(c.Samples)).ToList(), CancellationToken.None);
            AddTrials(m, clips.Select(c => (c.Plan, c.Clip)).ToList(), direct, call: false, trials, enrolled);
            if (opus)
            {
                var degraded = await client.EmbedAsync(clips.Select(c => Wav.Encode(TeamsLike.Degrade(c.Samples))).ToList(), CancellationToken.None);
                AddTrials(m, clips.Select(c => (c.Plan, c.Clip)).ToList(), degraded, call: true, trials, enrolledCall);
            }
            Console.Error.Write($"\r{m.Id}: {plans.Count} speakers, {clips.Count} clips   ");
        }
        Console.Error.WriteLine();
        Console.WriteLine($"embedded {clipCount} clips ({clipSeconds / 60:0} min of speech) in {sw.Elapsed.TotalSeconds:0}s " +
                          $"= {sw.Elapsed.TotalMilliseconds / Math.Max(1, clipCount * (opus ? 2 : 1)):0} ms per clip; {enrolled.Count} people learned");

        var scored = trials.Where(t => enrolled.ContainsKey(t.Truth)).Select(t => Score(t, enrolled)).ToList();
        var clean = scored.Where(s => !s.Trial.Call).ToList();
        Report("clean", clean);
        var callToCall = trials.Where(t => t.Call && enrolledCall.ContainsKey(t.Truth)).Select(t => Score(t, enrolledCall)).ToList();
        if (callToCall.Count > 0) Report("call voice vs call voiceprints (the usual Teams case)", callToCall);

        var levels = new List<ThresholdSet>();
        double penalty = 0.05;
        if (opus)
        {
            var call = scored.Where(s => s.Trial.Call).ToList();
            // How much a voice through the codec loses against its own clean voiceprint, typically.
            var drop = clean.Where(s => !s.Trial.Short).Join(call.Where(s => !s.Trial.Short),
                a => (a.Trial.Truth, a.Trial.Meeting), b => (b.Trial.Truth, b.Trial.Meeting), (a, b) => a.TruthScore - b.TruthScore).ToList();
            if (drop.Count > 0) penalty = Math.Round(Math.Max(0, Median(drop)), 3);
            Console.WriteLine($"\nTeams-like codec: true-match score drops by {penalty:0.000} (median) → kind penalty");
            Report("through codec, compared with clean voiceprints, penalty applied",
                call.Select(s => s with { BestScore = s.BestScore - penalty, Second = s.Second - penalty, TruthScore = s.TruthScore - penalty }).ToList());
        }

        var fitOn = clean.Concat(callToCall).ToList();
        // The highest scores any wrong person reached on a short clip: each level stays above a share of them,
        // so Careful stays careful even when the fit found no mistakes to avoid.
        var wrongTail = fitOn.Where(s => s.Trial.Short).Select(s => s.Best == s.Trial.Truth ? s.Second : s.BestScore).ToList();
        Console.WriteLine($"\nLevels (fitted on clean{(callToCall.Count > 0 ? " + call-to-call" : "")} audio, each kept above a percentile of wrong-person scores):");
        Console.WriteLine("  level  accept  margin  maybe   named(speaker-mtg)  wrong(speaker-mtg)  wrong(short clips)  floor");
        for (int i = 0; i < Budgets.Length; i++)
        {
            var (fitted, margin, _, _, _) = Fit(fitOn, Budgets[i]);
            double accept = Math.Max(fitted, Pct(wrongTail, Floors[i]));
            var (named, wrongMeetings, wrongClips) = Evaluate(fitOn, accept, margin);
            var set = new ThresholdSet(Math.Round(accept, 3), Math.Round(accept - 0.1, 3), margin, penalty);
            levels.Add(set);
            Console.WriteLine($"  {i + 1,5}  {set.Accept,6:0.000}  {margin,6:0.000}  {set.Maybe,6:0.000}  {named,17:P1}  {wrongMeetings,18}  {wrongClips,17:P2}  p{Floors[i]}");
        }

        var thresholds = new ModelThresholds { Name = model, Calibration = Vectors.ToBase64(calibration), Levels = levels };
        var json = JsonSerializer.Serialize(thresholds, Json.Options);
        Console.WriteLine("\nspeakers.thresholds for this model:\n" + json);
        if (outFile is not null) File.WriteAllText(outFile, json);
        return 0;
    }

    static void AddTrials(Meeting m, List<(SpeakerPlan Plan, ClipSpan Clip)> clips, IReadOnlyList<float[]> vectors, bool call,
        List<Trial> trials, Dictionary<string, float[]>? enrolled)
    {
        foreach (var group in clips.Select((c, i) => (c.Plan, c.Clip, Vector: vectors[i])).GroupBy(x => x.Plan.Key))
        {
            var mean = Vectors.WeightedMean(group.Select(x => (x.Vector, x.Clip.Seconds)).ToList());
            if (m.Session == 'a')
            {
                enrolled?.TryAdd(group.Key, mean); // someone in two series is learned from their first
                continue;
            }
            trials.Add(new Trial(group.Key, m.Id, mean, Short: false, call));
            foreach (var x in group) trials.Add(new Trial(group.Key, m.Id, x.Vector, Short: true, call));
        }
    }

    static Scored Score(Trial t, Dictionary<string, float[]> enrolled)
    {
        var ranked = enrolled.Select(e => (e.Key, Score: Vectors.Cosine(t.Vector, e.Value))).OrderByDescending(x => x.Score).ToList();
        return new Scored(t, ranked[0].Key, ranked[0].Score, ranked.Count > 1 ? ranked[1].Score : -1, Vectors.Cosine(t.Vector, enrolled[t.Truth]));
    }

    static void Report(string title, List<Scored> scored)
    {
        foreach (var (name, set) in new[] { ("speaker-meetings", scored.Where(s => !s.Trial.Short).ToList()), ("short clips", scored.Where(s => s.Trial.Short).ToList()) })
        {
            if (set.Count == 0) continue;
            double top1 = set.Count(s => s.Best == s.Trial.Truth) / (double)set.Count;
            var truth = set.Select(s => s.TruthScore).ToList();
            var impostor = set.Select(s => s.Best == s.Trial.Truth ? s.Second : s.BestScore).ToList();
            Console.WriteLine($"{title}, {name}: n={set.Count}, right person ranked first {top1:P1}; " +
                              $"true-match score median {Median(truth):0.000} (5th pct {Pct(truth, 5):0.000}); " +
                              $"best wrong person median {Median(impostor):0.000} (95th pct {Pct(impostor, 95):0.000})");
        }
    }

    /// <summary>The (accept, margin) that names the most speaker-meetings within the level's wrong-name budget.</summary>
    static (double Accept, double Margin, double Named, int WrongMeetings, double WrongClips) Fit(List<Scored> scored, (double Clips, int Meetings) budget)
    {
        var meetings = scored.Where(s => !s.Trial.Short).ToList();
        var clips = scored.Where(s => s.Trial.Short).ToList();
        (double, double, double, int, double) best = (1, 0, 0, 0, 0);
        foreach (var margin in new[] { 0.0, 0.02, 0.04, 0.06, 0.08, 0.1 })
        {
            for (double accept = 0.95; accept >= 0.2; accept -= 0.005)
            {
                bool Named(Scored s) => s.BestScore >= accept && s.BestScore - s.Second >= margin;
                int wrongM = meetings.Count(s => Named(s) && s.Best != s.Trial.Truth);
                double wrongC = clips.Count == 0 ? 0 : clips.Count(s => Named(s) && s.Best != s.Trial.Truth) / (double)clips.Count;
                if (wrongM > budget.Meetings || wrongC > budget.Clips) break; // lower accept only adds names, right and wrong
                double named = meetings.Count(s => Named(s) && s.Best == s.Trial.Truth) / (double)Math.Max(1, meetings.Count);
                if (named > best.Item3) best = (accept, margin, named, wrongM, wrongC);
            }
        }
        return best;
    }

    static double Median(List<double> x) => Pct(x, 50);

    static double Pct(List<double> x, double p)
    {
        if (x.Count == 0) return double.NaN;
        var s = x.Order().ToList();
        return s[(int)Math.Clamp(Math.Round(p / 100 * (s.Count - 1)), 0, s.Count - 1)];
    }

    /// <summary>Meetings with audio and all speakers' segments, keyed by AMI's global participant ids.</summary>
    static List<Meeting> LoadMeetings(string audioDir, string zipPath, string[]? series)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var meetingsXml = XDocument.Load(zip.GetEntry("corpusResources/meetings.xml")!.Open());
        var speakers = meetingsXml.Descendants("meeting")
            .ToDictionary(m => (string)m.Attribute("observation")!,
                m => m.Elements("speaker").ToDictionary(s => (string)s.Attribute("nxt_agent")!, s => (string)s.Attribute("global_name")!));

        var result = new List<Meeting>();
        foreach (var wav in Directory.EnumerateFiles(audioDir, "*.Mix-Headset.wav").Order())
        {
            var id = Path.GetFileName(wav).Split('.')[0];
            var s = id[..^1];
            if (series is not null && !series.Contains(s)) continue;
            if (!speakers.TryGetValue(id, out var agents)) continue;
            using (var probe = new NAudio.Wave.WaveFileReader(wav))
            {
                var f = probe.WaveFormat;
                if (f.SampleRate != 16000 || f.Channels != 1 || f.BitsPerSample != 16)
                {
                    Console.WriteLine($"skipping {id}: {f.SampleRate} Hz, {f.Channels} channels (the recorder's own tracks are 16 kHz mono)");
                    continue;
                }
            }
            var segments = new List<LabelledSegment>();
            foreach (var (agent, person) in agents)
            {
                var entry = zip.GetEntry($"segments/{id}.{agent}.segments.xml");
                if (entry is null) continue;
                foreach (var seg in XDocument.Load(entry.Open()).Descendants("segment"))
                {
                    double start = double.Parse((string)seg.Attribute("transcriber_start")!, CultureInfo.InvariantCulture);
                    double end = double.Parse((string)seg.Attribute("transcriber_end")!, CultureInfo.InvariantCulture);
                    if (end > start) segments.Add(new LabelledSegment(start, end, person, "-", false, person));
                }
            }
            result.Add(new Meeting(id, s, id[^1], wav, segments.OrderBy(x => x.Start).ToList()));
        }
        // Only series with a meeting a to learn from.
        var learnable = result.Where(m => m.Session == 'a').Select(m => m.Series).ToHashSet();
        return result.Where(m => learnable.Contains(m.Series)).ToList();
    }

    static string? Program_Opt(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}

/// <summary>
/// What a Teams call does to a voice, approximately: Opus at a low voice bitrate, 20 ms frames.
/// (Teams also runs noise suppression; that part isn't modelled.)
/// </summary>
static class TeamsLike
{
    public const int Bitrate = 12000;

    public static short[] Degrade(short[] samples)
    {
        const int frame = 320; // 20 ms at 16 kHz
        var encoder = Concentus.OpusCodecFactory.CreateEncoder(16000, 1, Concentus.Enums.OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.Bitrate = Bitrate;
        var decoder = Concentus.OpusCodecFactory.CreateDecoder(16000, 1);
        var output = new short[samples.Length];
        var packet = new byte[1275];
        var input = new short[frame];
        for (int i = 0; i + frame <= samples.Length; i += frame)
        {
            Array.Copy(samples, i, input, 0, frame);
            int length = encoder.Encode(input, frame, packet, packet.Length);
            decoder.Decode(packet.AsSpan(0, length), output.AsSpan(i, frame), frame, false);
        }
        return output;
    }
}
