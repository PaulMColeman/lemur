using Lemur.Audio;
using Lemur.Backends;
using Lemur.Config;
using Lemur.Logging;
using Lemur.Pipeline;
using Lemur.Transcripts;

namespace Lemur.Speakers;

/// <summary>
/// The Identified stage (design 3.1): who each speaker is, by voice and by introduction. Each half runs
/// on its own and is recorded as done or skipped; nothing here throws except cancellation, because
/// names must never hold up the notes.
/// </summary>
public sealed class SpeakerIdentifier
{
    /// <summary>How far above "accept" a match must be before it teaches that person's voice by itself.</summary>
    public const double LearnAbove = 0.05;

    /// <summary>Length of the reference clip kept per person and kind (design D2).</summary>
    public const double ReferenceSeconds = 10;

    readonly AppSettings _settings;
    readonly Func<IVoiceprintClient?> _voiceprints;
    readonly Func<IChatModel?> _chat;
    readonly Func<DateTimeOffset> _clock;

    public SpeakerIdentifier(AppSettings settings, Func<IVoiceprintClient?> voiceprints, Func<IChatModel?> chat,
        Func<DateTimeOffset>? clock = null)
    {
        _settings = settings;
        _voiceprints = voiceprints;
        _chat = chat;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>The real thing: voiceprint service and summary model from settings, keys from the store.</summary>
    /// <param name="learn">See <see cref="Learn"/>.</param>
    public static SpeakerIdentifier FromSettings(AppSettings settings, ISecretStore secrets, bool learn = true) =>
        new(settings,
            () => VoiceprintClient.Create(settings, secrets, out _),
            () => ChatModel.Create(settings, secrets, out _))
        { Learn = learn };

    public PeopleStore People => new(_settings.Speakers.ResolvedPeopleFolder);

    /// <summary>Whether a meeting teaches the people folder (the user's voice, clear matches). Off when a meeting is made
    /// again: it taught what it could the first time.</summary>
    public bool Learn { get; init; } = true;

    public async Task<IdentifiedResult> IdentifyAsync(RecordingFolder rec, RecordingMeta meta, LabelResult labels,
        MeetingProcessor.Envelopes env, CancellationToken ct)
    {
        // Remote voices are on the call's track whenever it has sound — also on a call you sat through muted, where the
        // tracks can't be compared and nobody is you (Paul's Teams call of 2026-10-21: taken for in-person, every clip was
        // cut from the silent mic, so ▶ played nothing and all five voices got one voiceprint, of silence).
        bool callAudio = labels.UsedTrackComparison || env.Others.Percentile(95) > Mixer.SilentTrackDb;
        var plans = ClipPlanner.Plan(labels.Segments, env.Mic, env.Others, callAudio);
        var result = new IdentifiedResult
        {
            Speakers = plans.Select(p => new IdentifiedSpeaker
            {
                Key = p.Key,
                Label = p.Label,
                EngineSpeaker = p.EngineSpeaker,
                IsMe = p.IsMe,
                Kind = p.Kind,
                Track = p.Track,
                TalkSeconds = Math.Round(p.TalkSeconds, 1),
                Clips = [.. p.Clips],
                Status = p.IsMe ? SpeakerStatus.Me : p.TooLittle ? SpeakerStatus.TooLittle : SpeakerStatus.Unknown,
            }).ToList(),
        };

        var vectors = new Dictionary<string, (float[] Mean, IReadOnlyList<float[]> Clips)>();
        if (_settings.Speakers.Enabled)
        {
            try
            {
                result.Voices = await VoicesAsync(rec, meta, plans, result, vectors, callAudio, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                result.Voices = "skipped: " + Reason(ex);
            }
        }

        if (_settings.Speakers.NameFromIntroductions)
        {
            try
            {
                result.Introductions = await IntroductionsAsync(labels.Segments, result, vectors, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                result.Introductions = "skipped: " + Reason(ex);
            }
        }
        return result;
    }

    async Task<string> VoicesAsync(RecordingFolder rec, RecordingMeta meta, IReadOnlyList<SpeakerPlan> plans,
        IdentifiedResult result, Dictionary<string, (float[] Mean, IReadOnlyList<float[]> Clips)> vectors, bool callAudio,
        CancellationToken ct)
    {
        using var client = _voiceprints();
        if (client is null) return "skipped: speakers.baseUrl is not set";

        var calibration = (await client.EmbedAsync([Calibration.SignalWav()], ct))[0];
        var store = People;
        var now = _clock();
        var model = store.Models.Resolve(calibration, _settings.Speakers.Model, now);
        result.Model = model;

        // Every speaker with enough clean audio — including the user on calls, to learn their voice.
        var wanted = plans.Where(p => !p.TooLittle && (!p.IsMe || _settings.Speakers.LearnMyVoice)).ToList();
        var clips = new List<(SpeakerPlan Plan, ClipSpan Span, byte[] Wav)>();
        foreach (var p in wanted)
        {
            var path = p.Track == Track.Others ? rec.OthersWav : rec.MicWav;
            foreach (var span in p.Clips) clips.Add((p, span, Wav.Encode(Wav.Read(path, span.Start, span.End))));
        }
        var embedded = clips.Count == 0 ? [] : await client.EmbedAsync(clips.Select(c => c.Wav).ToList(), ct);
        foreach (var group in clips.Select((c, i) => (c.Plan, c.Span, Vector: embedded[i])).GroupBy(x => x.Plan.Key))
        {
            var clipVectors = group.Select(x => x.Vector).ToList();
            var mean = Vectors.WeightedMean(group.Select(x => (x.Vector, x.Span.Seconds)).ToList());
            vectors[group.Key] = (mean, clipVectors);
        }
        rec.Write(rec.VoiceprintsFile, new MeetingVoiceprints
        {
            Model = model,
            Speakers = vectors.ToDictionary(kv => kv.Key, kv => Vectors.ToBase64(kv.Value.Mean)),
        });

        var people = store.People(now);
        var thresholds = BuiltInThresholds.For(calibration, _settings.Speakers.Level, _settings.Speakers.Thresholds);
        string status = "done";
        if (thresholds is null)
        {
            status = "skipped: no matching numbers for this model yet (run llmmr voices benchmark)";
        }
        else
        {
            // Teams never plays you back to yourself, so on a call nobody on "others" is the user.
            var candidates = callAudio ? people.Where(p => !p.IsMe).ToList() : people.ToList();
            var voices = result.Speakers
                .Where(s => !s.IsMe && vectors.ContainsKey(s.Key))
                .Select(s => new SpeakerVoice(s.Key, s.Kind, vectors[s.Key].Mean, vectors[s.Key].Clips))
                .ToList();
            foreach (var m in Matcher.Match(voices, candidates, model, thresholds))
            {
                var s = result.Speakers.First(x => x.Key == m.Key);
                s.Score = Math.Round(m.Score, 3);
                s.RunnerUp = Math.Round(m.RunnerUp, 3);
                var person = m.PersonId is null ? null : people.First(p => p.Id == m.PersonId);
                s.Status = m.Status switch
                {
                    MatchStatus.Named when person!.IsMe => SpeakerStatus.Me,
                    MatchStatus.Named => SpeakerStatus.Named,
                    MatchStatus.Maybe => SpeakerStatus.Maybe,
                    MatchStatus.Conflict => SpeakerStatus.Conflict,
                    _ => SpeakerStatus.Unknown,
                };
                s.PersonId = m.PersonId;
                s.Name = person?.Name;

                // A clear match teaches that person a little more about how they sound (design 3.7: "their next meetings add more").
                if (Learn && m.Status == MatchStatus.Named && m.Score >= thresholds.Accept + LearnAbove && person is not null)
                {
                    if (PeopleStore.AddPrint(person, NewPrint(rec, meta, s, vectors[s.Key].Mean, model, confirmed: false, now)))
                        store.Save(person, now);
                }
            }
        }

        if (Learn && callAudio && _settings.Speakers.LearnMyVoice && vectors.TryGetValue("me", out var mine))
            LearnMe(store, rec, meta, result.Speakers.First(s => s.Key == "me"), mine.Mean, model, now);

        return status;
    }

    /// <summary>On calls, the track comparison knows which segments are the user: free, reliable samples (design 3.5).</summary>
    void LearnMe(PeopleStore store, RecordingFolder rec, RecordingMeta meta, IdentifiedSpeaker me, float[] vector, string model, DateTimeOffset now)
    {
        var person = store.Me() ?? store.Create(_settings.DisplayName, isMe: true);
        var print = NewPrint(rec, meta, me, vector, model, confirmed: true, now);
        if (!person.Prints.Any(p => p.Kind == me.Kind && p.Model == model && p.Clip is not null))
            print.Clip = store.SaveClip(person.Id, print.Id, ReferenceClip(rec, me));
        if (PeopleStore.AddPrint(person, print))
        {
            store.Save(person, now);
            Log.Info($"{rec.Name}: learned your voice ({person.Prints.Count(p => p.Model == model)} voiceprints)");
        }
        else if (print.Clip is not null)
        {
            var clip = store.ClipPath(print);
            if (clip is not null && File.Exists(clip)) File.Delete(clip); // not kept, so neither is its clip
        }
    }

    async Task<string> IntroductionsAsync(IReadOnlyList<LabelledSegment> segments, IdentifiedResult result,
        Dictionary<string, (float[] Mean, IReadOnlyList<float[]> Clips)> vectors, CancellationToken ct)
    {
        using var chat = _chat();
        if (chat is null) return "skipped: summary.baseUrl/summary.model not set";

        var reply = await chat.AskAsync(Introductions.Prompt, "Transcript:\n" + TranscriptFormat.ToPlainText(segments), ct);
        var (accepted, hints) = Introductions.Verify(Introductions.Parse(reply), segments);

        var people = Directory.Exists(_settings.Speakers.ResolvedPeopleFolder) ? People.People() : [];
        var thresholds = result.Model is null ? null : ThresholdsFor(result.Model);
        foreach (var intro in accepted)
        {
            var s = result.Speakers.FirstOrDefault(x => x.Label == intro.Speaker && !x.IsMe);
            if (s is null) continue;
            s.Introduction = intro;
            if (s.Status is SpeakerStatus.Me or SpeakerStatus.Conflict) continue;

            var known = people.FirstOrDefault(p => p.Answers(intro.Name));
            // Their voice clearly says they are someone else: believe the voice (design 3.9).
            if (s.Status == SpeakerStatus.Named && (known is null || known.Id != s.PersonId)) continue;

            double? score = known is not null && thresholds is not null && result.Model is not null && vectors.TryGetValue(s.Key, out var v)
                ? Matcher.Score(v.Mean, s.Kind, known, result.Model, thresholds.KindPenalty)
                : null;
            if (known is not null && score is not null)
            {
                // A known name and comparable voiceprints: agree → sure; clearly disagree → "Dana?" (maybe two Danas).
                s.Status = score >= thresholds!.Maybe ? SpeakerStatus.Named : SpeakerStatus.Maybe;
                s.PersonId = known.Id;
                s.Name = known.Name;
            }
            else
            {
                s.Status = SpeakerStatus.Introduced;
                s.PersonId = known?.Id;
                s.Name = known?.Name ?? intro.Name;
            }
        }
        foreach (var hint in hints)
            result.Speakers.FirstOrDefault(x => x.Label == hint.ForSpeaker && !x.IsMe)?.Hints.Add(hint);
        return "done";
    }

    ThresholdSet? ThresholdsFor(string model)
    {
        var record = People.Models.All().FirstOrDefault(m => m.Id == model);
        return record is null ? null : BuiltInThresholds.For(Vectors.FromBase64(record.Calibration), _settings.Speakers.Level, _settings.Speakers.Thresholds);
    }

    public static Voiceprint NewPrint(RecordingFolder rec, RecordingMeta meta, IdentifiedSpeaker s, float[] vector, string model,
        bool confirmed, DateTimeOffset now) => new()
    {
        Id = "pr-" + Guid.NewGuid().ToString("N")[..8],
        Kind = s.Kind,
        Model = model,
        Vector = Vectors.ToBase64(vector),
        Seconds = Math.Round(s.Clips.Sum(c => c.Seconds), 1),
        From = new PrintSource { Recording = rec.Name, Title = string.IsNullOrWhiteSpace(meta.Title) ? null : meta.Title, At = meta.StartedAt },
        Confirmed = confirmed,
        Added = now,
    };

    /// <summary>The speaker's longest clips joined, up to <see cref="ReferenceSeconds"/>.</summary>
    public static byte[] ReferenceClip(RecordingFolder rec, IdentifiedSpeaker s)
    {
        var path = s.Track == Track.Others ? rec.OthersWav : rec.MicWav;
        var samples = new List<short>();
        foreach (var c in s.Clips.OrderByDescending(c => c.Seconds))
        {
            if (samples.Count >= ReferenceSeconds * Wav.SampleRate) break;
            samples.AddRange(Wav.Read(path, c.Start, c.End));
        }
        return Wav.Encode(samples.Take((int)(ReferenceSeconds * Wav.SampleRate)).ToArray());
    }

    static string Reason(Exception ex) => ex is BackendException ? ex.Message : $"{ex.GetType().Name}: {Backends.Http.SafeSnippet(ex.Message)}";
}
