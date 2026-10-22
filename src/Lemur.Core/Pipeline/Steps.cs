using System.Text.Json.Serialization;

namespace Lemur.Pipeline;

/// <summary>The work that turns a recording into notes: one step per stage it moves the meeting on from.</summary>
public enum Step
{
    Mix,
    Transcribe,
    NameSpeakers,
    Summarize,
    WriteNote,
}

public static class PipelineSteps
{
    public static readonly Step[] All = [Step.Mix, Step.Transcribe, Step.NameSpeakers, Step.Summarize, Step.WriteNote];

    /// <summary>The step that moves a meeting on from this stage; null once it is done.</summary>
    public static Step? From(Stage stage) => stage switch
    {
        Stage.Recorded => Step.Mix,
        Stage.Mixed => Step.Transcribe,
        Stage.Transcribed => Step.NameSpeakers,
        Stage.Identified => Step.Summarize,
        Stage.Summarized => Step.WriteNote,
        _ => null,
    };

    /// <summary>The stage a meeting is at while this step is still to do.</summary>
    public static Stage Before(Step step) => step switch
    {
        Step.Mix => Stage.Recorded,
        Step.Transcribe => Stage.Mixed,
        Step.NameSpeakers => Stage.Transcribed,
        Step.Summarize => Stage.Identified,
        _ => Stage.Summarized,
    };

    public static string Name(Step step) => step switch
    {
        Step.Mix => "Mixing the two tracks",
        Step.Transcribe => "Transcribing",
        Step.NameSpeakers => "Naming speakers",
        Step.Summarize => "Summarizing",
        _ => "Writing the note",
    };
}

/// <summary>When a step last ran: its latest try's start and end, and how many tries it has taken.</summary>
public sealed class StepTime
{
    public Step Step { get; set; }
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>Null while it runs, or when its latest try failed.</summary>
    public DateTimeOffset? FinishedAt { get; set; }

    public int Tries { get; set; }

    [JsonIgnore]
    public TimeSpan? Took => FinishedAt is { } end && end >= StartedAt ? end - StartedAt : null;
}

/// <summary>
/// Roughly how long each step takes, for the Recordings window. Starts from times measured on the first meetings
/// (2026-10-13/14: Scriberr — Parakeet + pyannote — on an RTX 3090, and a local Qwen3.8 summary model behind
/// LiteLLM), then scales each step to this PC's own finished meetings (the median of actual ÷ measured), so another
/// server's speed shows after one meeting. Only steps that went through on their first try count.
/// </summary>
public sealed class StepEstimates
{
    readonly Dictionary<Step, double> _scale;

    StepEstimates(Dictionary<Step, double> scale, int meetings)
    {
        _scale = scale;
        Meetings = meetings;
    }

    /// <summary>How many of this PC's meetings the estimates were scaled by (0 = the built-in measurements alone).</summary>
    public int Meetings { get; }

    /// <summary>seconds = fixed + per minute of meeting.</summary>
    static double Measured(Step step, double minutes)
    {
        var (fixedSeconds, perMinute) = step switch
        {
            Step.Mix => (0.5, 0.02),          // 48 min: under a second
            Step.Transcribe => (30, 3.7),     // 2 min: 39 s · 17 min: 82 s · 39 min: 184 s · 48 min: 208 s
            Step.NameSpeakers => (20, 1.5),   // voiceprints take seconds; the rest is the introductions check
            Step.Summarize => (20, 2.5),      // 2 min: 25 s · 17 min: 60–120 s · 43 min: 134 s
            _ => (1, 0.12),                   // mostly the MP3: 4 s for 40 min
        };
        return fixedSeconds + perMinute * minutes;
    }

    public static StepEstimates Measured() => new([], 0);

    /// <param name="meetings">Each meeting's length in seconds and its processing state.</param>
    public static StepEstimates From(IEnumerable<(double Seconds, ProcessingState State)> meetings)
    {
        var ratios = new Dictionary<Step, List<double>>();
        int counted = 0;
        foreach (var (seconds, state) in meetings)
        {
            if (seconds <= 0) continue;
            bool any = false;
            foreach (var time in state.Steps)
            {
                if (time.Tries != 1 || time.Took is not { } took) continue;
                if (!ratios.TryGetValue(time.Step, out var list)) ratios[time.Step] = list = [];
                list.Add(took.TotalSeconds / Measured(time.Step, seconds / 60));
                any = true;
            }
            if (any) counted++;
        }
        var scale = ratios.ToDictionary(r => r.Key, r => Math.Clamp(Median(r.Value), 0.2, 5));
        return new StepEstimates(scale, counted);
    }

    static double Median(List<double> values)
    {
        values.Sort();
        int mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }

    /// <summary>Estimates from every recording in the folder (unreadable ones are skipped).</summary>
    public static StepEstimates FromFolder(string root) =>
        From(RecordingFolder.All(root).Select(rec =>
        {
            try { return (rec.LoadMeta().DurationSeconds, rec.LoadState()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            { return (0d, new ProcessingState()); }
        }).ToList());

    /// <summary>All the steps of a meeting this long.</summary>
    public TimeSpan Total(double meetingSeconds) => PipelineSteps.All.Aggregate(TimeSpan.Zero, (sum, step) => sum + For(step, meetingSeconds));

    public TimeSpan For(Step step, double meetingSeconds) =>
        TimeSpan.FromSeconds(Measured(step, Math.Max(0, meetingSeconds) / 60) * _scale.GetValueOrDefault(step, 1));
}
