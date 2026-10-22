using Lemur.Transcripts;

namespace Lemur.Speakers;

public enum SpeakerStatus
{
    /// <summary>The user, by track comparison (calls) or by voice (in person).</summary>
    Me,
    /// <summary>Recognised by voice, clearly.</summary>
    Named,
    /// <summary>Recognised by voice, not sure: "Dana?".</summary>
    Maybe,
    /// <summary>Said their own name.</summary>
    Introduced,
    /// <summary>Named by the user (naming window or `llmmr people name`).</summary>
    Confirmed,
    /// <summary>The user said this isn't a person (a video, the TV).</summary>
    NotAPerson,
    Unknown,
    TooLittle,
    Conflict,
}

/// <summary>One speaker the engine found, and who we think they are. Saved in identified.json.</summary>
public sealed class IdentifiedSpeaker
{
    /// <summary>"me", or the engine's speaker label.</summary>
    public string Key { get; set; } = "";

    /// <summary>The label the transcript gave them before naming: "Speaker B", or the user's name.</summary>
    public string Label { get; set; } = "";

    public string EngineSpeaker { get; set; } = "";
    public bool IsMe { get; set; }
    public VoiceKind Kind { get; set; }
    public Track Track { get; set; }
    public double TalkSeconds { get; set; }
    public List<ClipSpan> Clips { get; set; } = [];

    public SpeakerStatus Status { get; set; } = SpeakerStatus.Unknown;
    public string? PersonId { get; set; }
    public string? Name { get; set; }
    public double? Score { get; set; }
    public double? RunnerUp { get; set; }
    public Introduction? Introduction { get; set; }
    public List<NameHint> Hints { get; set; } = [];
}

/// <summary>identified.json — kept after the audio is deleted (it holds no audio and no voiceprints).</summary>
public sealed class IdentifiedResult
{
    /// <summary>"done" or "skipped: why".</summary>
    public string Voices { get; set; } = "skipped: off";
    public string Introductions { get; set; } = "skipped: off";

    /// <summary>Fingerprint of the voiceprint model used, when voices ran.</summary>
    public string? Model { get; set; }

    public List<IdentifiedSpeaker> Speakers { get; set; } = [];
}

/// <summary>
/// voiceprints.json — this meeting's voiceprint per speaker, so naming someone later can remember their
/// voice without asking the server again. Biometric, so it is deleted with the audio after 14 days.
/// </summary>
public sealed class MeetingVoiceprints
{
    public string Model { get; set; } = "";
    public Dictionary<string, string> Speakers { get; set; } = new();
}

/// <summary>Turning identification into what the notes say.</summary>
public static class Naming
{
    public const string NotAPersonLabel = "Other audio";

    /// <summary>What the notes call a speaker.</summary>
    public static string DisplayLabel(IdentifiedSpeaker s) => s.Status switch
    {
        SpeakerStatus.Named or SpeakerStatus.Introduced or SpeakerStatus.Confirmed when !string.IsNullOrWhiteSpace(s.Name) => s.Name!,
        SpeakerStatus.Maybe when !string.IsNullOrWhiteSpace(s.Name) => s.Name + "?",
        SpeakerStatus.NotAPerson => NotAPersonLabel,
        _ => s.Label,
    };

    /// <summary>The labelled transcript with names in place of "Speaker B". Without identification, unchanged.</summary>
    public static IReadOnlyList<LabelledSegment> Apply(IReadOnlyList<LabelledSegment> labelled, IdentifiedResult? identified, string displayName)
    {
        if (identified is null) return labelled;
        var byLabel = identified.Speakers
            .Where(s => !s.IsMe)
            .GroupBy(s => s.Label)
            .ToDictionary(g => g.Key, g => g.First());
        return labelled.Select(seg =>
        {
            if (seg.IsMe || !byLabel.TryGetValue(seg.Speaker, out var s)) return seg;
            bool me = s.Status == SpeakerStatus.Me;
            return seg with { Speaker = me ? displayName : DisplayLabel(s), IsMe = me };
        }).ToList();
    }

    /// <summary>Everyone named for sure, for the note's front matter (not the user, not "Dana?").</summary>
    public static IReadOnlyList<string> People(IdentifiedResult? identified) =>
        identified is null ? [] : identified.Speakers
            .Where(s => s.Status is SpeakerStatus.Named or SpeakerStatus.Introduced or SpeakerStatus.Confirmed && !s.IsMe)
            .Select(s => s.Name!)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Engine label → name, for showing names in the backend's own UI (Scriberr).</summary>
    public static Dictionary<string, string> ForBackend(IdentifiedResult? identified, string displayName, string? meEngineSpeaker)
    {
        var map = new Dictionary<string, string>();
        if (meEngineSpeaker is not null) map[meEngineSpeaker] = displayName;
        if (identified is null) return map;
        foreach (var s in identified.Speakers.Where(s => !s.IsMe))
        {
            if (s.Status == SpeakerStatus.Me) map.TryAdd(s.EngineSpeaker, displayName);
            else if (s.Status is SpeakerStatus.Named or SpeakerStatus.Introduced or SpeakerStatus.Confirmed && s.Name is not null)
                map.TryAdd(s.EngineSpeaker, s.Name);
        }
        return map;
    }
}
