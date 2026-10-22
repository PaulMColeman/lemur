namespace Lemur.Speakers;

public enum MatchStatus
{
    /// <summary>A clear match: above the accept line and ahead of the runner-up.</summary>
    Named,

    /// <summary>Close but not sure: shown as "Dana?", never stored as a confirmed match.</summary>
    Maybe,

    Unknown,

    /// <summary>Its clips match two different people: the engine merged two voices, so no name.</summary>
    Conflict,
}

/// <param name="Vector">The meeting's voiceprint for this speaker (length-weighted mean of its clips).</param>
public sealed record SpeakerVoice(string Key, VoiceKind Kind, float[] Vector, IReadOnlyList<float[]> ClipVectors);

public sealed record VoiceMatch(string Key, MatchStatus Status, string? PersonId, string? Name, double Score, double RunnerUp);

/// <summary>Who each voice in a meeting is, if anyone we know (design 3.3).</summary>
public static class Matcher
{
    /// <summary>
    /// The higher of: best similarity to this person's voiceprints of the same kind; best similarity to
    /// their other kind minus a penalty. Only voiceprints from <paramref name="model"/> count.
    /// Null when the person has none from this model.
    /// </summary>
    public static double? Score(float[] vector, VoiceKind kind, Person person, string model, double penalty)
    {
        double? best = null;
        foreach (var print in person.Prints)
        {
            if (print.Model != model) continue;
            double s = Vectors.Cosine(vector, print.Values()) - (print.Kind == kind ? 0 : penalty);
            if (best is null || s > best) best = s;
        }
        return best;
    }

    public static IReadOnlyList<VoiceMatch> Match(IReadOnlyList<SpeakerVoice> voices, IReadOnlyList<Person> candidates,
        string model, ThresholdSet t)
    {
        var people = candidates.Where(p => !p.IsDeleted && p.Prints.Any(x => x.Model == model)).ToList();
        var matches = voices.Select(v => MatchOne(v, people, model, t)).ToList();

        // A person can be "Maybe" for at most one voice, and not at all if some voice is clearly them.
        // Two voices that are both clearly the same person keep the name: the engine split one person.
        foreach (var group in matches.Where(m => m.PersonId is not null).GroupBy(m => m.PersonId))
        {
            bool someoneNamed = group.Any(m => m.Status == MatchStatus.Named);
            var bestMaybe = group.Where(m => m.Status == MatchStatus.Maybe).OrderByDescending(m => m.Score).FirstOrDefault();
            foreach (var m in group.Where(m => m.Status == MatchStatus.Maybe))
            {
                if (someoneNamed || m != bestMaybe)
                    matches[matches.IndexOf(m)] = m with { Status = MatchStatus.Unknown, PersonId = null, Name = null };
            }
        }
        return matches;
    }

    static VoiceMatch MatchOne(SpeakerVoice voice, List<Person> people, string model, ThresholdSet t)
    {
        var scored = people
            .Select(p => (Person: p, Score: Score(voice.Vector, voice.Kind, p, model, t.KindPenalty)))
            .Where(x => x.Score is not null)
            .Select(x => (x.Person, Score: x.Score!.Value))
            .OrderByDescending(x => x.Score)
            .ToList();
        if (scored.Count == 0) return new VoiceMatch(voice.Key, MatchStatus.Unknown, null, null, 0, 0);

        var (best, score) = scored[0];
        double runnerUp = scored.Count > 1 ? scored[1].Score : -1;
        var status = score >= t.Accept && score - runnerUp >= t.Margin ? MatchStatus.Named
            : score >= t.Maybe ? MatchStatus.Maybe
            : MatchStatus.Unknown;
        if (status == MatchStatus.Unknown) return new VoiceMatch(voice.Key, status, null, null, score, runnerUp);

        if (TwoPeopleInOne(voice, best, people, model, t))
            return new VoiceMatch(voice.Key, MatchStatus.Conflict, null, null, score, runnerUp);
        return new VoiceMatch(voice.Key, status, best.Id, best.Name, score, runnerUp);
    }

    /// <summary>
    /// Some other single person has at least as many clips clearly theirs as the best match has: the engine
    /// put two people under one label, and there's no telling whose it mostly is. A few clips of others is the
    /// engine's usual untidiness and doesn't cost the name — on AMI ES2004b a label that was 81% one person
    /// had clips from three (3–2–1); it is named after the 3.
    /// </summary>
    static bool TwoPeopleInOne(SpeakerVoice voice, Person best, List<Person> people, string model, ThresholdSet t)
    {
        if (voice.ClipVectors.Count < 2) return false;
        var elsewhere = new Dictionary<string, int>();
        int agreeing = 0;
        foreach (var clip in voice.ClipVectors)
        {
            var top = people
                .Select(p => (Person: p, Score: Score(clip, voice.Kind, p, model, t.KindPenalty) ?? -1))
                .OrderByDescending(x => x.Score)
                .First();
            double toBest = Score(clip, voice.Kind, best, model, t.KindPenalty) ?? -1;
            if (top.Person != best && top.Score >= t.Accept && top.Score - toBest >= t.Margin)
                elsewhere[top.Person.Id] = elsewhere.GetValueOrDefault(top.Person.Id) + 1;
            else
                agreeing++;
        }
        int rival = elsewhere.Count == 0 ? 0 : elsewhere.Values.Max();
        return rival >= 2 && rival >= agreeing;
    }
}
