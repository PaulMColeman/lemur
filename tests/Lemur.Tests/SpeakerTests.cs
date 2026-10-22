using System.Net;
using System.Text;
using System.Text.Json;
using Lemur.Audio;
using Lemur.Backends;
using Lemur.Config;
using Lemur.Pipeline;
using Lemur.Speakers;
using Lemur.Transcripts;

namespace Lemur.Tests;

static class V
{
    /// <summary>A unit vector pointing mostly along axis <paramref name="axis"/>, nudged along the next one.</summary>
    public static float[] Of(int axis, double nudge = 0, int dim = 8)
    {
        var v = new float[dim];
        v[axis % dim] = 1;
        v[(axis + 1) % dim] = (float)nudge;
        return Vectors.Normalize(v);
    }

    public static Voiceprint Print(float[] v, VoiceKind kind = VoiceKind.Call, string model = "fp-test", bool confirmed = true) => new()
    {
        Id = "pr-" + Guid.NewGuid().ToString("N")[..8],
        Kind = kind,
        Model = model,
        Vector = Vectors.ToBase64(v),
        Confirmed = confirmed,
    };

    public static Person Person(string name, params Voiceprint[] prints) =>
        new() { Id = Guid.NewGuid().ToString("N")[..6], Name = name, Prints = [.. prints] };

    public static Envelope Flat(double seconds, float db) =>
        new(0.05, Enumerable.Repeat(db, (int)(seconds / 0.05)).ToArray());

    public static LabelledSegment Seg(double start, double end, string engine, bool me = false, string? label = null, string text = "words") =>
        new(start, end, label ?? (me ? "Paul" : $"Speaker {engine}"), text, me, engine);
}

public class ClipPlannerTests
{
    [Fact]
    public void In_person_clips_come_from_the_mic_and_skip_overlaps()
    {
        var segs = new[] { V.Seg(0, 10, "A"), V.Seg(9, 20, "B") };
        var plans = ClipPlanner.Plan(segs, V.Flat(20, -20), V.Flat(20, -100), callAudio: false);
        var a = plans.Single(p => p.Key == "A");
        Assert.Equal(Track.Mic, a.Track);
        Assert.Equal(VoiceKind.Local, a.Kind);
        Assert.All(a.Clips, c => Assert.True(c.End <= 9 - ClipPlanner.OverlapGuard));       // stops before B starts talking
        Assert.All(plans.SelectMany(p => p.Clips), c => Assert.InRange(c.Seconds, ClipPlanner.MinRun, ClipPlanner.MaxPiece));
        var b = plans.Single(p => p.Key == "B");
        Assert.All(b.Clips, c => Assert.True(c.Start >= 10 + ClipPlanner.OverlapGuard));
    }

    [Fact]
    public void On_a_call_remote_voices_come_from_others_and_you_from_the_mic()
    {
        var segs = new[] { V.Seg(0, 8, "S1", me: true), V.Seg(10, 30, "S2") };
        var mic = new Envelope(0.05, Enumerable.Range(0, 600).Select(i => i < 170 ? -20f : -70f).ToArray());
        var others = new Envelope(0.05, Enumerable.Range(0, 600).Select(i => i >= 200 ? -22f : -90f).ToArray());
        var plans = ClipPlanner.Plan(segs, mic, others, callAudio: true);
        var me = plans.Single(p => p.IsMe);
        Assert.Equal(("me", Track.Mic, VoiceKind.Local, "S1"), (me.Key, me.Track, me.Kind, me.EngineSpeaker));
        var them = plans.Single(p => p.Key == "S2");
        Assert.Equal((Track.Others, VoiceKind.Call), (them.Track, them.Kind));
        Assert.True(them.Clips.Count <= ClipPlanner.MaxClips);
        Assert.False(them.TooLittle);
    }

    [Fact]
    public void Crosstalk_from_you_is_cut_out_of_their_clips()
    {
        var segs = new[] { V.Seg(0, 20, "S2") };
        // You talk over them from 5 s to 15 s: mic loud, louder than the call audio.
        var mic = new Envelope(0.05, Enumerable.Range(0, 400).Select(i => i is >= 100 and < 300 ? -15f : -70f).ToArray());
        var others = V.Flat(20, -25);
        var plan = ClipPlanner.Plan(segs, mic, others, callAudio: true).Single();
        Assert.All(plan.Clips, c => Assert.True(c.End <= 5 || c.Start >= 15));
    }

    [Fact]
    public void Clips_are_never_cut_from_a_silent_track()
    {
        // A muted mic is digital silence: a voiceprint of it matches every other one (Paul's call of 2026-10-21).
        var plan = ClipPlanner.Plan([V.Seg(0, 20, "A")], V.Flat(20, -100), V.Flat(20, -100), callAudio: false).Single();
        Assert.Empty(plan.Clips);
        Assert.True(plan.TooLittle);

        // The call's track silent for the first half of their line: clips only from the half you can hear.
        var others = new Envelope(0.05, Enumerable.Range(0, 400).Select(i => i < 200 ? -100f : -22f).ToArray());
        plan = ClipPlanner.Plan([V.Seg(0, 20, "S2")], V.Flat(20, -100), others, callAudio: true).Single();
        Assert.NotEmpty(plan.Clips);
        Assert.All(plan.Clips, c => Assert.True(c.Start >= 9.99));
    }

    [Fact]
    public void A_speaker_with_little_clean_audio_is_too_little_to_guess()
    {
        var segs = new[] { V.Seg(0, 1.5, "A"), V.Seg(5, 6.0, "A"), V.Seg(10, 40, "B") };
        var plans = ClipPlanner.Plan(segs, V.Flat(40, -20), V.Flat(40, -100), callAudio: false);
        Assert.True(plans.Single(p => p.Key == "A").TooLittle);
        Assert.False(plans.Single(p => p.Key == "B").TooLittle);
    }

    [Fact]
    public void Interval_subtraction_handles_edges()
    {
        var from = new List<ClipSpan> { new(0, 10), new(20, 30) };
        var cut = new List<ClipSpan> { new(-1, 2), new(5, 6), new(9, 21), new(29, 40) };
        var left = ClipPlanner.Subtract(from, cut);
        Assert.Equal([new ClipSpan(2, 5), new ClipSpan(6, 9), new ClipSpan(21, 29)], left);
    }
}

public class MatcherTests
{
    static readonly ThresholdSet T = new(Accept: 0.9, Maybe: 0.75, Margin: 0.05, KindPenalty: 0.1);

    static SpeakerVoice Voice(string key, float[] v, VoiceKind kind = VoiceKind.Call, params float[][] clips) =>
        new(key, kind, v, clips.Length > 0 ? clips : [v]);

    [Fact]
    public void Clear_close_and_far_voices()
    {
        var dana = V.Person("Dana", V.Print(V.Of(0)));
        var sam = V.Person("Sam", V.Print(V.Of(3)));
        var m = Matcher.Match([Voice("B", V.Of(0, 0.1)), Voice("C", V.Of(3, 0.6)), Voice("D", V.Of(6))], [dana, sam], "fp-test", T);
        Assert.Equal((MatchStatus.Named, "Dana"), (m[0].Status, m[0].Name));
        Assert.Equal((MatchStatus.Maybe, "Sam"), (m[1].Status, m[1].Name));
        Assert.Equal((MatchStatus.Unknown, null), (m[2].Status, m[2].Name));
    }

    [Fact]
    public void Too_close_to_the_runner_up_is_not_clear()
    {
        var a = V.Person("A", V.Print(V.Of(0, 0.2)));
        var b = V.Person("B", V.Print(V.Of(0, 0.25)));
        var m = Matcher.Match([Voice("X", V.Of(0, 0.22))], [a, b], "fp-test", T).Single();
        Assert.NotEqual(MatchStatus.Named, m.Status);
    }

    [Fact]
    public void Other_kinds_count_with_a_penalty_and_other_models_not_at_all()
    {
        var local = V.Person("Dana", V.Print(V.Of(0, 0.2), VoiceKind.Local));
        var m = Matcher.Match([Voice("B", V.Of(0))], [local], "fp-test", T).Single();
        Assert.Equal(MatchStatus.Maybe, m.Status); // 0.98 − 0.1 penalty: below accept
        var sameKind = V.Person("Dana", V.Print(V.Of(0, 0.2), VoiceKind.Call));
        Assert.Equal(MatchStatus.Named, Matcher.Match([Voice("B", V.Of(0))], [sameKind], "fp-test", T).Single().Status);
        var otherModel = V.Person("Dana", V.Print(V.Of(0), model: "fp-other"));
        Assert.Equal(MatchStatus.Unknown, Matcher.Match([Voice("B", V.Of(0))], [otherModel], "fp-test", T).Single().Status);
    }

    [Fact]
    public void Two_voices_clearly_one_person_both_keep_the_name_but_a_maybe_is_dropped()
    {
        var dana = V.Person("Dana", V.Print(V.Of(0)));
        var m = Matcher.Match([Voice("B", V.Of(0, 0.05)), Voice("C", V.Of(0, 0.1)), Voice("D", V.Of(0, 0.7))], [dana], "fp-test", T);
        Assert.Equal(MatchStatus.Named, m[0].Status);
        Assert.Equal(MatchStatus.Named, m[1].Status);
        Assert.Equal(MatchStatus.Unknown, m[2].Status);
    }

    [Fact]
    public void Only_the_best_maybe_survives()
    {
        var dana = V.Person("Dana", V.Print(V.Of(0)));
        var m = Matcher.Match([Voice("B", V.Of(0, 0.6)), Voice("C", V.Of(0, 0.7))], [dana], "fp-test", T);
        Assert.Equal(MatchStatus.Maybe, m[0].Status);
        Assert.Equal(MatchStatus.Unknown, m[1].Status);
    }

    [Fact]
    public void A_label_holding_two_people_gets_no_name()
    {
        var dana = V.Person("Dana", V.Print(V.Of(0)));
        var sam = V.Person("Sam", V.Print(V.Of(3)));
        var mean = V.Of(0, 0.1);
        var m = Matcher.Match([Voice("B", mean, VoiceKind.Call, V.Of(0), V.Of(0), V.Of(3), V.Of(3))], [dana, sam], "fp-test", T).Single();
        Assert.Equal(MatchStatus.Conflict, m.Status);
    }

    [Fact]
    public void A_little_of_someone_else_under_a_label_still_names_the_main_voice()
    {
        var dana = V.Person("Dana", V.Print(V.Of(0)));
        var sam = V.Person("Sam", V.Print(V.Of(3)));
        var ali = V.Person("Ali", V.Print(V.Of(5)));
        // Clips from three people, 3–2–1 (AMI ES2004b, speaker G): the engine's untidiness; named after the 3.
        var m = Matcher.Match([Voice("G", V.Of(0, 0.2), VoiceKind.Call, V.Of(0), V.Of(0), V.Of(0), V.Of(3), V.Of(3), V.Of(5))],
            [dana, sam, ali], "fp-test", T).Single();
        Assert.Equal((MatchStatus.Named, "Dana"), (m.Status, m.Name));
    }
}

public class PeopleStoreTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("llmmr-people").FullName;
    static readonly DateTimeOffset T0 = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Saves_finds_by_name_or_alias_and_deletes_to_a_stub()
    {
        var store = new PeopleStore(_root);
        var dana = store.Create("Dana Smith");
        dana.Aliases.Add("Dayna");
        dana.Prints.Add(V.Print(V.Of(0)));
        dana.Prints[0].Clip = store.SaveClip(dana.Id, dana.Prints[0].Id, Wav.Encode(new short[16000]));
        store.Save(dana);

        Assert.Equal(dana.Id, store.Find("dayna")!.Id);
        Assert.True(File.Exists(store.ClipPath(store.Find("Dana Smith")!.Prints[0])));

        store.Delete(dana, T0);
        Assert.Null(store.Find("Dana Smith"));
        Assert.False(Directory.Exists(Path.Combine(_root, "clips", dana.Id)));
        var stub = store.LoadAll(T0).Single();
        Assert.True(stub.IsDeleted);
        Assert.Equal("", stub.Name);                                        // no name kept
        Assert.Empty(store.LoadAll(T0 + PeopleStore.StubLifetime + TimeSpan.FromDays(1)));
    }

    [Fact]
    public void OneDrive_conflict_copies_merge_into_one_file()
    {
        var store = new PeopleStore(_root);
        var pc1 = V.Person("Dana", V.Print(V.Of(0)));
        pc1.Updated = T0;
        var pc2 = new Person { Id = pc1.Id, Name = "Dana Smith", Updated = T0.AddMinutes(5), Prints = [V.Print(V.Of(4))] };
        Directory.CreateDirectory(Path.Combine(_root, "people"));
        File.WriteAllText(Path.Combine(_root, "people", pc1.Id + ".json"), JsonSerializer.Serialize(pc1, Json.Options));
        File.WriteAllText(Path.Combine(_root, "people", pc1.Id + "-PAULS-LAPTOP.json"), JsonSerializer.Serialize(pc2, Json.Options));

        var merged = store.LoadAll(T0.AddHours(1)).Single();
        Assert.Equal("Dana Smith", merged.Name);                            // newest copy's name
        Assert.Equal(2, merged.Prints.Count);                               // both PCs' voiceprints
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "people")));   // the copy is gone
    }

    [Fact]
    public void A_newer_deletion_beats_an_offline_copy_but_not_a_newer_edit()
    {
        var living = V.Person("Dana", V.Print(V.Of(0)));
        living.Updated = T0;
        var stub = new Person { Id = living.Id, Deleted = T0.AddMinutes(1), Updated = T0.AddMinutes(1) };
        Assert.True(PeopleStore.Merge([living, stub]).IsDeleted);
        living.Updated = T0.AddMinutes(2);
        Assert.False(PeopleStore.Merge([living, stub]).IsDeleted);
    }

    [Fact]
    public void Voiceprints_must_add_something_and_stay_three_per_kind()
    {
        var dana = V.Person("Dana");
        Assert.True(PeopleStore.AddPrint(dana, V.Print(V.Of(0))));
        Assert.False(PeopleStore.AddPrint(dana, V.Print(V.Of(0, 0.05))));   // same as one kept
        Assert.True(PeopleStore.AddPrint(dana, V.Print(V.Of(0, 0.8))));
        Assert.True(PeopleStore.AddPrint(dana, V.Print(V.Of(0, 2.0))));
        PeopleStore.AddPrint(dana, V.Print(V.Of(0, 4.0)));
        Assert.Equal(PeopleStore.MaxPerKind, dana.Prints.Count(p => p.Kind == VoiceKind.Call));
        Assert.True(PeopleStore.AddPrint(dana, V.Print(V.Of(0), VoiceKind.Local)));  // other kinds have their own three
        Assert.True(PeopleStore.AddPrint(dana, V.Print(V.Of(0), model: "fp-new")));  // and other models
    }

    [Fact]
    public void Merging_two_people_moves_voiceprints_and_names()
    {
        var store = new PeopleStore(_root);
        var a = store.Create("Dayna");
        a.Prints.Add(V.Print(V.Of(0)));
        store.Save(a);
        var b = store.Create("Dana Smith");
        store.MergeInto(store.Find("Dayna")!, store.Find("Dana Smith")!);
        var merged = store.People().Single();
        Assert.Equal("Dana Smith", merged.Name);
        Assert.Contains("Dayna", merged.Aliases);
        Assert.Single(merged.Prints);
    }

    public void Dispose() => Directory.Delete(_root, true);
}

public class CalibrationTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("llmmr-cal").FullName;

    [Fact]
    public void The_signal_is_the_same_every_time()
    {
        Assert.Equal(Calibration.Signal(), Calibration.Signal());
        Assert.Equal(48000, Calibration.Signal().Length);
        Assert.Contains(Calibration.Signal(), s => Math.Abs((int)s) > 3000); // not silence
    }

    [Fact]
    public void The_same_model_resolves_to_the_same_id_and_a_new_one_gets_its_own()
    {
        var registry = new ModelRegistry(_root);
        var now = DateTimeOffset.Now;
        var id = registry.Resolve(V.Of(0, 0.1), "speaker-embed-local", now);
        Assert.StartsWith("fp-", id);
        Assert.Equal(id, registry.Resolve(V.Of(0, 0.1001), "renamed-by-proxy", now)); // tiny rounding: same model
        Assert.NotEqual(id, registry.Resolve(V.Of(2), "speaker-embed-local", now));
        Assert.Equal(2, registry.All().Count);
    }

    [Fact]
    public void Built_in_numbers_apply_only_to_the_model_they_were_fitted_on()
    {
        var fitted = new ModelThresholds { Calibration = Vectors.ToBase64(V.Of(0)), Levels = [new(0.9, 0.8, 0.05, 0.1), new(0.8, 0.7, 0.04, 0.1)] };
        Assert.Equal(0.8, BuiltInThresholds.For(V.Of(0), 2, fitted)!.Accept);
        Assert.Equal(0.8, BuiltInThresholds.For(V.Of(0), 9, fitted)!.Accept); // clamped to the last level
        Assert.Null(BuiltInThresholds.For(V.Of(3), 1, fitted));
    }

    public void Dispose() => Directory.Delete(_root, true);
}

public class IntroductionTests
{
    static readonly LabelledSegment[] Meeting =
    [
        V.Seg(0, 4, "S1", me: true, text: "Morning everyone, let's start."),
        V.Seg(5, 9, "S2", text: "Hi, it's Dana from finance. I'm good, thanks."),
        V.Seg(10, 14, "S3", text: "Sam here, sorry I'm late."),
        V.Seg(15, 18, "S2", text: "Thanks, Sam."),
    ];

    static IReadOnlyList<NameClaim> Claims(params object[] names) =>
        Introductions.Parse("```json\n" + JsonSerializer.Serialize(new { names }) + "\n```");

    [Fact]
    public void Self_introductions_backed_by_the_transcript_are_accepted()
    {
        var (accepted, hints) = Introductions.Verify(Claims(
            new { speaker = "Speaker S2", name = "Dana", quote = "it's Dana from finance", time = "00:00:05", kind = "self" },
            new { speaker = "Speaker S3", name = "Sam", quote = "Sam here", time = "00:00:10", kind = "self" },
            new { speaker = "Speaker S2", name = "Sam", quote = "Thanks, Sam.", time = "00:00:15", kind = "other" }), Meeting);
        Assert.Equal(["Speaker S2:Dana", "Speaker S3:Sam"], accepted.Select(a => $"{a.Speaker}:{a.Name}").Order());
        var hint = Assert.Single(hints);
        Assert.Equal(("Speaker S3", "Sam"), (hint.ForSpeaker, hint.Name)); // the person who spoke just before
    }

    [Theory]
    [InlineData("Speaker S2", "Priya", "it's Priya from finance")] // invented quote
    [InlineData("Speaker S2", "Good", "I'm good, thanks")]          // not a name
    [InlineData("Speaker S2", "Sam", "it's Dana from finance")]     // name not in the quote
    [InlineData("Paul", "Paul", "Morning everyone")]                // the user
    public void Claims_the_transcript_does_not_back_are_rejected(string speaker, string name, string quote)
    {
        var (accepted, _) = Introductions.Verify(Claims(new { speaker, name, quote, kind = "self" }), Meeting);
        Assert.Empty(accepted);
    }

    [Fact]
    public void Two_names_for_one_speaker_or_one_name_for_two_speakers_is_no_name()
    {
        var segs = new[] { V.Seg(0, 3, "S2", text: "I'm Dana. Well, Dana Smith."), V.Seg(4, 6, "S3", text: "And I'm Dana too.") };
        var (accepted, _) = Introductions.Verify(Claims(
            new { speaker = "Speaker S2", name = "Dana", quote = "I'm Dana", kind = "self" },
            new { speaker = "Speaker S2", name = "Dana Smith", quote = "Dana Smith", kind = "self" }), segs);
        Assert.Empty(accepted);
        (accepted, _) = Introductions.Verify(Claims(
            new { speaker = "Speaker S2", name = "Dana", quote = "I'm Dana", kind = "self" },
            new { speaker = "Speaker S3", name = "Dana", quote = "I'm Dana too", kind = "self" }), segs);
        Assert.Empty(accepted);
    }

    [Fact]
    public void Without_capitals_in_the_transcript_the_capital_check_is_dropped()
    {
        var segs = new[] { V.Seg(0, 3, "S2", text: "hi it's dana from finance") };
        var (accepted, _) = Introductions.Verify(Claims(new { speaker = "Speaker S2", name = "dana", quote = "it's dana", kind = "self" }), segs);
        Assert.Single(accepted);
    }

    [Fact]
    public void Garbage_replies_parse_to_nothing() =>
        Assert.Empty(Introductions.Parse("I could not find any names, sorry."));
}

public class VoiceprintClientTests
{
    sealed class Handler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<int> InputsPerRequest { get; } = [];
        public string? Auth;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Auth = request.Headers.Authorization?.ToString();
            var body = await request.Content!.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            InputsPerRequest.Add(doc.RootElement.GetProperty("input").GetArrayLength());
            return respond(request, body);
        }
    }

    static HttpResponseMessage Reply(string body)
    {
        using var doc = JsonDocument.Parse(body);
        int n = doc.RootElement.GetProperty("input").GetArrayLength();
        // Answer out of order, one as base64, to prove both are handled.
        var data = Enumerable.Range(0, n).Reverse().Select(i => i == 0
            ? (object)new { index = i, embedding = Vectors.ToBase64([2f, 0f]) }
            : new { index = i, embedding = new[] { 0f, (float)i } });
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { data }), Encoding.UTF8, "application/json") };
    }

    [Fact]
    public async Task Batches_under_a_megabyte_and_returns_unit_vectors_in_order()
    {
        var handler = new Handler((_, body) => Reply(body));
        using var client = new VoiceprintClient("https://gw.example/v1/", "speaker-embed-local", "k", handler);
        var clip = Wav.Encode(new short[6 * 16000]);                          // ~256 KB as base64
        var vectors = await client.EmbedAsync(Enumerable.Repeat(clip, 7).ToList(), CancellationToken.None);
        Assert.Equal(7, vectors.Count);
        Assert.True(handler.InputsPerRequest.Count >= 2);
        Assert.All(handler.InputsPerRequest, n => Assert.True(n * 256_000 < VoiceprintClient.MaxRequestChars + 256_000));
        Assert.Equal([1f, 0f], vectors[0]);                                   // base64 answer, normalised
        Assert.Equal([0f, 1f], vectors[1]);
        Assert.Equal("Bearer k", handler.Auth);
    }

    [Fact]
    public void The_summary_key_is_reused_only_on_the_same_server()
    {
        Assert.True(VoiceprintClient.SameServer("https://litellm.example.com/v1", "https://LITELLM.example.com:443/v1"));
        Assert.False(VoiceprintClient.SameServer("https://litellm.example.com/v1", "http://litellm.example.com/v1"));
        Assert.False(VoiceprintClient.SameServer("https://a.example.com/v1", "https://b.example.com/v1"));
    }
}

/// <summary>
/// A stand-in voiceprint model: the voice's pitch (from zero crossings) as an angle, so the same
/// synthetic voice always gets the same vector and different pitches different ones.
/// </summary>
sealed class PitchVoiceprints : IVoiceprintClient
{
    public int Calls;

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<byte[]> wavClips, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<float[]>>(wavClips.Select(Embed).ToList());
    }

    static float[] Embed(byte[] wav)
    {
        int crossings = 0, n = (wav.Length - 44) / 2;
        short prev = 0;
        for (int i = 0; i < n; i++)
        {
            short s = (short)(wav[44 + 2 * i] | (wav[45 + 2 * i] << 8));
            if ((prev < 0) != (s < 0)) crossings++;
            prev = s;
        }
        double hz = crossings / 2.0 / (n / 16000.0);
        double angle = hz / 1000 * Math.PI;
        return Vectors.Normalize([(float)Math.Cos(angle), (float)Math.Sin(angle), 0.1f]);
    }

    public void Dispose() { }
}

sealed class FakeChat(string reply) : IChatModel
{
    public string? LastTranscript;

    public Task<string> AskAsync(string system, string user, CancellationToken ct)
    {
        LastTranscript = user;
        return Task.FromResult(reply);
    }

    public void Dispose() { }
}

/// <summary>A 40-second call: you (300 Hz) for 12 s, then a remote voice (700 Hz) for 25 s.</summary>
sealed class CallBackend : IMeetingBackend
{
    public Dictionary<string, string>? Named;
    public string? LastTranscriptText;
    public string Kind => "fake";

    public Task<TranscriptResult> TranscribeAsync(string audioPath, string title, TimeSpan duration, CancellationToken ct) =>
        Task.FromResult(new TranscriptResult(
        [
            new(0.5, 12, "S1", "Morning, I'll write it up."),
            new(14, 39, "S2", "Hi, it's Dana from finance, and I'll send the numbers."),
        ], "j1", "https://fake/j1"));

    public Task NameSpeakersAsync(TranscriptResult t, IReadOnlyDictionary<string, string> names, CancellationToken ct)
    {
        Named = new(names);
        return Task.CompletedTask;
    }

    public Task<string> SummarizeAsync(TranscriptResult t, string instructions, string labelled, CancellationToken ct)
    {
        LastTranscriptText = labelled;
        return Task.FromResult("## Summary\nTest.\n\n## Action items\n- [ ] Speaker A - send the numbers");
    }

    public void Dispose() { }
}

public class IdentificationPipelineTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("llmmr-ident").FullName;
    static readonly ThresholdSet Numbers = new(Accept: 0.95, Maybe: 0.85, Margin: 0.02, KindPenalty: 0.05);

    AppSettings Settings(bool voices = true, bool intros = false) => new()
    {
        DisplayName = "Paul",
        RecordingsFolder = Path.Combine(_root, "rec"),
        NotesFolder = Path.Combine(_root, "notes"),
        Backend = { Type = BackendTypes.Scriberr, Scriberr = { BaseUrl = "https://x" } },
        Speakers = new SpeakerSettings
        {
            Enabled = voices,
            BaseUrl = "https://gw.example/v1",
            PeopleFolder = Path.Combine(_root, "people"),
            NameFromIntroductions = intros,
            AskConsent = true,
            Thresholds = new ModelThresholds
            {
                Calibration = Vectors.ToBase64(new PitchVoiceprints().EmbedAsync([Calibration.SignalWav()], default).Result[0]),
                Levels = [Numbers],
            },
        },
    };

    static float[] Tone(int seconds, double hz, double from, double to, float amp = 0.3f)
    {
        var x = new float[seconds * 16000];
        for (int i = (int)(from * 16000); i < Math.Min(x.Length, (int)(to * 16000)); i++) x[i] = amp * MathF.Sin((float)(2 * Math.PI * hz * i / 16000));
        return x;
    }

    RecordingFolder MakeCall(AppSettings s)
    {
        var rec = RecordingFolder.CreateNew(s.ResolvedRecordingsFolder, new DateTimeOffset(2026, 10, 14, 9, 30, 0, TimeSpan.FromHours(-5)));
        var mic = Tone(40, 300, 0.5, 12);
        var others = Tone(40, 700, 14, 39);
        for (int i = 0; i < mic.Length; i++) mic[i] += 0.0005f * MathF.Sin(i * 1.7f);
        using (var a = new WavSampleSink(rec.MicWav)) a.Write(mic);
        using (var b = new WavSampleSink(rec.OthersWav)) b.Write(others);
        rec.SaveMeta(new RecordingMeta { Title = "Budget", Source = "Teams (Edge)", StartedAt = new DateTimeOffset(2026, 10, 14, 9, 30, 0, TimeSpan.FromHours(-5)), DurationSeconds = 40 });
        rec.SaveState(new ProcessingState());
        return rec;
    }

    /// <summary>Dana's voiceprint as this fake model hears a 700 Hz voice through the call.</summary>
    static float[] DanaVector() =>
        new PitchVoiceprints().EmbedAsync([Wav.Encode(Tone(5, 700, 0, 5).Select(x => (short)(x * 32767)).ToArray())], default).Result[0];

    [Fact]
    public async Task A_known_voice_is_named_everywhere_and_your_voice_is_learned()
    {
        var s = Settings();
        var store = new PeopleStore(s.Speakers.ResolvedPeopleFolder);
        var model = store.Models.Resolve(Vectors.FromBase64(s.Speakers.Thresholds!.Calibration), "speaker-embed-local", DateTimeOffset.Now);
        var dana = store.Create("Dana Smith");
        dana.Prints.Add(V.Print(DanaVector(), VoiceKind.Call, model));
        store.Save(dana);

        var rec = MakeCall(s);
        var backend = new CallBackend();
        var voiceprints = new PitchVoiceprints();
        var identifier = new SpeakerIdentifier(s, () => voiceprints, () => null);
        var state = await new MeetingProcessor(s, () => backend, identifier).ProcessAsync(rec, CancellationToken.None);

        Assert.Equal(Stage.Done, state.Stage);
        var identified = rec.Read<IdentifiedResult>(rec.IdentifiedFile)!;
        Assert.Equal("done", identified.Voices);
        var remote = identified.Speakers.Single(x => !x.IsMe);
        Assert.Equal((SpeakerStatus.Named, "Dana Smith"), (remote.Status, remote.Name));

        Assert.Contains("Dana Smith: Hi, it's Dana", backend.LastTranscriptText);         // the summary model saw the name
        Assert.Equal("Dana Smith", backend.Named!["S2"]);                                   // and so does Scriberr
        var note = File.ReadAllText(state.NotePath!);
        Assert.Contains("people: [\"Dana Smith\"]", note);
        Assert.Contains("**[00:00:14] Dana Smith:**", note);
        Assert.Contains("lemur://name/" + rec.Name, File.ReadAllText(NoteWriter.HtmlPath(state.NotePath!)));

        var me = store.Me()!;                                                               // learned from the call
        Assert.Equal("Paul", me.Name);
        Assert.Single(me.Prints, p => p.Kind == VoiceKind.Local && p.Clip is not null);
        Assert.True(File.Exists(rec.VoiceprintsFile));
        rec.DeleteAudio();
        Assert.False(File.Exists(rec.VoiceprintsFile));                                      // biometric: goes with the audio
        Assert.True(File.Exists(rec.IdentifiedFile));
    }

    [Fact]
    public async Task A_call_you_sat_through_muted_takes_every_voice_from_the_call_audio()
    {
        var s = Settings();
        var store = new PeopleStore(s.Speakers.ResolvedPeopleFolder);
        var model = store.Models.Resolve(Vectors.FromBase64(s.Speakers.Thresholds!.Calibration), "speaker-embed-local", DateTimeOffset.Now);
        var dana = store.Create("Dana Smith");
        dana.Prints.Add(V.Print(DanaVector(), VoiceKind.Call, model));
        store.Save(dana);

        // Muted all along: the mic is digital silence, and both voices are on the call's track.
        var rec = MakeCall(s);
        using (var a = new WavSampleSink(rec.MicWav)) a.Write(new float[40 * 16000]);
        var others = Tone(40, 300, 0.5, 12);
        var second = Tone(40, 700, 14, 39);
        for (int i = 0; i < others.Length; i++) others[i] += second[i];
        using (var b = new WavSampleSink(rec.OthersWav)) b.Write(others);

        var identifier = new SpeakerIdentifier(s, () => new PitchVoiceprints(), () => null);
        await new MeetingProcessor(s, () => new CallBackend(), identifier).ProcessAsync(rec, CancellationToken.None);

        var identified = rec.Read<IdentifiedResult>(rec.IdentifiedFile)!;
        Assert.DoesNotContain(identified.Speakers, x => x.IsMe);
        Assert.All(identified.Speakers, x => Assert.Equal((Track.Others, VoiceKind.Call), (x.Track, x.Kind)));  // ▶ plays the call
        var remote = identified.Speakers.Single(x => x.EngineSpeaker == "S2");
        Assert.Equal((SpeakerStatus.Named, "Dana Smith"), (remote.Status, remote.Name));
        var prints = rec.Read<MeetingVoiceprints>(rec.VoiceprintsFile)!.Speakers;
        Assert.NotEqual(prints["S1"], prints["S2"]);                                                           // two voices, not one silence
    }

    [Fact]
    public async Task An_introduction_names_someone_new_without_any_voiceprints()
    {
        var s = Settings(voices: false, intros: true);
        var rec = MakeCall(s);
        var backend = new CallBackend();
        var chat = new FakeChat("""{"names":[{"speaker":"Speaker A","name":"Dana","quote":"it's Dana from finance","time":"00:00:14","kind":"self"}]}""");
        var identifier = new SpeakerIdentifier(s, () => null, () => chat);
        var state = await new MeetingProcessor(s, () => backend, identifier).ProcessAsync(rec, CancellationToken.None);

        var remote = rec.Read<IdentifiedResult>(rec.IdentifiedFile)!.Speakers.Single(x => !x.IsMe);
        Assert.Equal((SpeakerStatus.Introduced, "Dana"), (remote.Status, remote.Name));
        Assert.Contains("Speaker A:", chat.LastTranscript);                                  // asked with the original labels
        Assert.Contains("**[00:00:14] Dana:**", File.ReadAllText(state.NotePath!));
        Assert.False(Directory.Exists(s.Speakers.ResolvedPeopleFolder));                    // nobody remembered by itself
    }

    [Fact]
    public async Task A_failing_voiceprint_service_never_holds_up_the_notes()
    {
        var s = Settings();
        var rec = MakeCall(s);
        var identifier = new SpeakerIdentifier(s, () => throw new BackendException("voiceprints: HTTP 502"), () => null);
        var state = await new MeetingProcessor(s, () => new CallBackend(), identifier).ProcessAsync(rec, CancellationToken.None);
        Assert.Equal(Stage.Done, state.Stage);
        Assert.StartsWith("skipped: voiceprints: HTTP 502", rec.Read<IdentifiedResult>(rec.IdentifiedFile)!.Voices);
        Assert.Contains("Speaker A:", File.ReadAllText(state.NotePath!));
    }

    [Fact]
    public async Task Naming_afterwards_remembers_with_consent_and_keeps_your_edits()
    {
        var s = Settings();
        var rec = MakeCall(s);
        var backend = new CallBackend();
        var identifier = new SpeakerIdentifier(s, () => new PitchVoiceprints(), () => null);
        var state = await new MeetingProcessor(s, () => backend, identifier).ProcessAsync(rec, CancellationToken.None);
        var namer = new SpeakerNamer(s, () => backend);

        // Without their agreement: named in this meeting only.
        var outcome = await namer.ApplyAsync(rec, [new NameChange("Speaker A", "Dana Smith", Remember: true)], CancellationToken.None);
        Assert.Contains(outcome.Messages, m => m.Contains("only"));
        Assert.Null(namer.People.Find("Dana Smith"));
        var note = File.ReadAllText(state.NotePath!);
        Assert.Contains("**[00:00:14] Dana Smith:**", note);                                 // untouched note: rewritten
        Assert.Contains("- [ ] Dana Smith - send the numbers", note);

        // With it: remembered, with a reference clip.
        await namer.ApplyAsync(rec, [new NameChange("Dana Smith", "Dana Smith", Remember: true, Consent: true, ConsentNote: "asked")], CancellationToken.None);
        var dana = namer.People.Find("Dana Smith")!;
        Assert.Equal("asked", dana.Consent!.Note);
        Assert.Single(dana.Prints, p => p.Confirmed && p.Clip is not null);

        // You edit the note, then correct the name: your edit survives, transcript lines change.
        File.AppendAllText(state.NotePath!, "\nMy own notes about Dana Smith.\n");
        await namer.ApplyAsync(rec, [new NameChange("Dana Smith", "Dana Smythe")], CancellationToken.None);
        note = File.ReadAllText(state.NotePath!);
        Assert.Contains("My own notes about Dana Smith.", note);
        Assert.Contains("**[00:00:14] Dana Smythe:**", note);
        Assert.Contains("people: [\"Dana Smythe\"]", note);
        Assert.Equal("Dana Smythe", backend.Named!["S2"]);
        Assert.Contains("Dana Smythe", File.ReadAllText(NoteWriter.HtmlPath(state.NotePath!)));
    }

    /// <summary>A minute's call: you (300 Hz) between two remote voices, the first (700 Hz) heard twice.</summary>
    sealed class TwoVoicesBackend : IMeetingBackend
    {
        public string Kind => "fake";
        public Task<TranscriptResult> TranscribeAsync(string audioPath, string title, TimeSpan duration, CancellationToken ct) =>
            Task.FromResult(new TranscriptResult(
            [
                new(0.5, 6, "S1", "Morning."),
                new(8, 18, "S2", "Hi, I'll send the numbers."),
                new(19, 23, "S1", "Great."),
                new(25, 37, "S3", "And I'll check them."),
                new(38, 41, "S1", "Thanks."),
                new(43, 55, "S2", "Bye for now."),
            ], "j1", "https://fake/j1"));
        public Task NameSpeakersAsync(TranscriptResult t, IReadOnlyDictionary<string, string> names, CancellationToken ct) => Task.CompletedTask;
        public Task<string> SummarizeAsync(TranscriptResult t, string instructions, string labelled, CancellationToken ct) =>
            Task.FromResult("## Summary\nSpeaker A sends the numbers and Speaker B checks them.");
        public void Dispose() { }
    }

    RecordingFolder MakeTwoVoices(AppSettings s)
    {
        var at = new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.FromHours(-5));
        var rec = RecordingFolder.CreateNew(s.ResolvedRecordingsFolder, at);
        static float[] Sum(params float[][] parts)
        {
            var x = new float[parts[0].Length];
            foreach (var p in parts) for (int i = 0; i < x.Length; i++) x[i] += p[i];
            return x;
        }
        var mic = Sum(Tone(60, 300, 0.5, 6), Tone(60, 300, 19, 23), Tone(60, 300, 38, 41));
        for (int i = 0; i < mic.Length; i++) mic[i] += 0.0005f * MathF.Sin(i * 1.7f);
        var others = Sum(Tone(60, 700, 8, 18), Tone(60, 900, 25, 37), Tone(60, 700, 43, 55));
        using (var a = new WavSampleSink(rec.MicWav)) a.Write(mic);
        using (var b = new WavSampleSink(rec.OthersWav)) b.Write(others);
        rec.SaveMeta(new RecordingMeta { Title = "Numbers", Source = "Teams (Edge)", StartedAt = at, DurationSeconds = 60 });
        rec.SaveState(new ProcessingState());
        return rec;
    }

    [Fact]
    public async Task One_person_heard_as_two_voices_gets_one_name_and_the_voices_can_be_told_apart_again()
    {
        var s = Settings();
        var rec = MakeTwoVoices(s);
        var identifier = new SpeakerIdentifier(s, () => new PitchVoiceprints(), () => null);
        var state = await new MeetingProcessor(s, () => new TwoVoicesBackend(), identifier).ProcessAsync(rec, CancellationToken.None);
        Assert.Equal(["Speaker A", "Speaker B"], rec.Read<IdentifiedResult>(rec.IdentifiedFile)!.Speakers.Where(x => !x.IsMe).Select(x => x.Label));
        var namer = new SpeakerNamer(s);

        // Both named Dana, "they agreed" ticked once: one person, with the voice of each.
        await namer.ApplyAsync(rec,
        [
            new NameChange("Speaker A", "Dana", Remember: true, Key: "S2"),
            new NameChange("Speaker B", "Dana", Remember: true, Consent: true, ConsentNote: "asked", Key: "S3"),
        ], CancellationToken.None);
        var dana = Assert.Single(namer.People.People(), p => !p.IsMe);
        Assert.Equal(("Dana", "asked", 2), (dana.Name, dana.Consent?.Note, dana.Prints.Count));
        var note = File.ReadAllText(state.NotePath!);
        Assert.Contains("**[00:00:08] Dana:**", note);
        Assert.Contains("**[00:00:25] Dana:**", note);
        Assert.Contains("Dana sends the numbers and Dana checks them.", note);

        // The label they share no longer says which voice: refused, not guessed.
        var outcome = await namer.ApplyAsync(rec, [new NameChange("Dana", "Sam")], CancellationToken.None);
        Assert.Contains(outcome.Messages, m => m.Contains("(Speaker A, Speaker B)"));
        Assert.DoesNotContain("Sam", File.ReadAllText(state.NotePath!));

        // You edit the note, then hear that the second voice was Sam: only its lines change.
        File.AppendAllText(state.NotePath!, "\nMy own notes about Dana.\n");
        await namer.ApplyAsync(rec, [new NameChange("Dana", "Sam", Key: "S3")], CancellationToken.None);
        note = File.ReadAllText(state.NotePath!);
        Assert.Contains("**[00:00:08] Dana:**", note);
        Assert.Contains("**[00:00:25] Sam:**", note);
        Assert.Contains("**[00:00:43] Dana:**", note);
        Assert.Contains("My own notes about Dana.", note);
        Assert.Contains("Dana sends the numbers and Dana checks them.", note); // which Dana the summary meant isn't known
        Assert.Contains("people: [\"Dana\", \"Sam\"]", note);

        // And that the first was you all along: its lines say your name; your own lines were never anyone else's.
        await namer.ApplyAsync(rec, [new NameChange("Dana", "Paul", Key: "S2")], CancellationToken.None);
        note = File.ReadAllText(state.NotePath!);
        Assert.Contains("**[00:00:08] Paul:**", note);
        Assert.Contains("**[00:00:43] Paul:**", note);
        Assert.Contains("**[00:00:19] Paul:** Great.", note);
        Assert.Contains("**[00:00:25] Sam:**", note);
    }

    public void Dispose() => Directory.Delete(_root, true);
}

public class NoteEditTests
{
    [Fact]
    public void Two_names_swapped_with_each_other_stay_two()
    {
        var text = "Dana and Sam met. **[00:00:01] Dana:** hi **[00:00:02] Sam:** yo";
        Assert.Equal("Sam and Dana met. **[00:00:01] Sam:** hi **[00:00:02] Dana:** yo",
            SpeakerNamer.SwapLabels(text, [("Dana", "Sam"), ("Sam", "Dana")], transcriptOnly: false));
        Assert.Equal("Dana and Sam met. **[00:00:01] Sam:** hi **[00:00:02] Dana:** yo",
            SpeakerNamer.SwapLabels(text, [("Dana", "Sam"), ("Sam", "Dana")], transcriptOnly: true));
    }

    [Fact]
    public void Labels_swap_as_whole_words_only()
    {
        var text = "Speaker B said hi. Speaker BB too. **[00:00:01] Speaker B:** yes";
        var swapped = SpeakerNamer.SwapLabels(text, [("Speaker B", "Dana")], transcriptOnly: false);
        Assert.Equal("Dana said hi. Speaker BB too. **[00:00:01] Dana:** yes", swapped);
    }

    [Fact]
    public void The_people_line_is_set_added_or_removed()
    {
        var note = "---\ntitle: \"x\"\ntags: [meeting]\n---\n\n# x\n";
        var added = SpeakerNamer.SetPeople(note, ["Dana"]);
        Assert.Contains("people: [\"Dana\"]\ntags: [meeting]", added);
        var changed = SpeakerNamer.SetPeople(added, ["Dana", "Sam"]);
        Assert.Contains("people: [\"Dana\", \"Sam\"]", changed);
        Assert.Equal(note, SpeakerNamer.SetPeople(changed, []));
    }
}

public class SetupFileTests
{
    static AppSettings Mine() => new()
    {
        DisplayName = "Paul",
        NotesFolder = @"C:\Users\me\Documents\Meeting Notes",
        Backend = { Type = BackendTypes.OpenAi, OpenAi = { TranscriptionBaseUrl = "https://wlk.example.com/v1", TranscriptionModel = "whisper-1" } },
        Summary = { BaseUrl = "https://litellm.example.com/v1", Model = "qwen-local", ApiKeySecret = "summary-api-key" },
        Speakers = { Enabled = true, Model = "speaker-embed-local" },
        Audio = { MicDeviceContains = "Jabra" },
    };

    [Fact]
    public void Carries_servers_and_models_but_no_keys_devices_folders_or_name()
    {
        var json = SetupFile.From(Mine(), "Example Org", "Get a key in the LiteLLM UI").ToJson();
        Assert.Contains("https://litellm.example.com/v1", json);
        Assert.Contains("speaker-embed-local", json);
        Assert.DoesNotContain("Jabra", json);
        Assert.DoesNotContain("Meeting Notes", json);
        Assert.DoesNotContain("summary-api-key", json);
        Assert.DoesNotContain("Paul", json);
    }

    [Fact]
    public void Importing_shows_the_changes_and_leaves_personal_settings_alone()
    {
        var file = SetupFile.Parse(SetupFile.From(Mine(), "WA", null).ToJson());
        var fresh = new AppSettings { DisplayName = "Dana", NotesFolder = @"D:\Notes", Backend = { Type = BackendTypes.Scriberr } };
        var preview = file.Preview(fresh);
        Assert.Contains(preview, l => l.StartsWith("backend: scriberr → openai"));
        Assert.Contains(preview, l => l.StartsWith("summary model: (none) → qwen-local"));
        file.ApplyTo(fresh);
        Assert.Equal(("Dana", @"D:\Notes", "qwen-local", false), (fresh.DisplayName, fresh.NotesFolder, fresh.Summary.Model, fresh.Speakers.Enabled));
        Assert.Empty(file.Preview(fresh));
    }

    [Fact]
    public void Rejects_newer_formats_and_odd_urls()
    {
        Assert.Throws<InvalidDataException>(() => SetupFile.Parse("""{"format": 99}"""));
        Assert.Throws<InvalidDataException>(() => SetupFile.Parse("""{"format": 1, "summary": {"baseUrl": "file:///c:/x"}}"""));
    }
}

public class ConnectionTestTests
{
    sealed class Secrets(Dictionary<string, string> values) : ISecretStore
    {
        public string? Get(string name) => values.GetValueOrDefault(name);
        public void Set(string name, string value) => values[name] = value;
        public bool Delete(string name) => values.Remove(name);
        public IEnumerable<string> Names() => [.. values.Keys];
    }

    sealed class Server : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var path = r.RequestUri!.AbsolutePath;
            string json = path.EndsWith("/models")
                ? r.Headers.Authorization?.Parameter == "good" ? """{"data":[{"id":"qwen-local"},{"id":"whisper-1"}]}""" : null!
                : """{"data":[{"index":0,"embedding":[1,0,0]}]}""";
            return Task.FromResult(json is null
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task Checks_each_server_and_skips_what_is_off()
    {
        var s = new AppSettings
        {
            Backend = { Type = BackendTypes.OpenAi, OpenAi = { TranscriptionBaseUrl = "https://wlk.example.com/v1" } },
            Summary = { BaseUrl = "https://litellm.example.com/v1", Model = "qwen-local" },
        };
        var secrets = new Secrets(new() { ["transcription-api-key"] = "bad", ["summary-api-key"] = "good" });
        var results = await ConnectionTest.RunAsync(s, secrets, CancellationToken.None, new Server());
        Assert.Equal(["Transcription", "Summary model"], results.Select(r => r.What));
        Assert.False(results[0].Ok);
        Assert.Contains("refused", results[0].Detail);
        Assert.True(results[1].Ok);

        s.Speakers.Enabled = true;
        results = await ConnectionTest.RunAsync(s, secrets, CancellationToken.None, new Server());
        var voice = results.Single(r => r.What == "Voiceprint model");
        Assert.True(voice.Ok);
        Assert.Contains("no matching numbers", voice.Detail);
    }
}

public class SampleImporterTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("llmmr-sample").FullName;

    /// <summary>A stereo 44.1 kHz WAV: <paramref name="hz"/> tones in 3-second bursts with 1-second pauses.</summary>
    string MakeFile(int seconds, params double[] hz)
    {
        var path = Path.Combine(_root, $"sample-{hz.Length}-{seconds}.wav");
        var format = new NAudio.Wave.WaveFormat(44100, 16, 2);
        using var w = new NAudio.Wave.WaveFileWriter(path, format);
        for (int i = 0; i < seconds * 44100; i++)
        {
            double t = i / 44100.0;
            int burst = (int)(t / 4);
            bool on = t % 4 < 3;
            double f = hz[burst % hz.Length];
            short v = on ? (short)(8000 * Math.Sin(2 * Math.PI * f * t)) : (short)0;
            w.WriteSample(v / 32768f);
            w.WriteSample(v / 32768f);
        }
        return path;
    }

    [Fact]
    public async Task A_voice_memo_becomes_a_confirmed_voiceprint_with_a_reference_clip()
    {
        var path = MakeFile(24, 300);
        Assert.Equal(24 * 16000, SampleImporter.Load(path).Length, tolerance: 400);   // resampled and downmixed
        var store = new PeopleStore(Path.Combine(_root, "people"));
        var check = await SampleImporter.CheckAsync(path, new PitchVoiceprints(), store, "speaker-embed-local", CancellationToken.None);
        Assert.InRange(check.SpeechSeconds, 15, 19);
        Assert.False(check.MaybeTwoVoices);

        var dana = store.Create("Dana");
        Assert.True(SampleImporter.Add(store, dana, check, path, VoiceKind.Local, DateTimeOffset.Now));
        var print = store.Find("Dana")!.Prints.Single();
        Assert.True(print.Confirmed);
        Assert.True(File.Exists(store.ClipPath(print)));
    }

    [Fact]
    public async Task Too_short_or_two_voices_is_caught()
    {
        var store = new PeopleStore(Path.Combine(_root, "people"));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SampleImporter.CheckAsync(MakeFile(8, 300), new PitchVoiceprints(), store, "m", CancellationToken.None));
        var two = await SampleImporter.CheckAsync(MakeFile(24, 250, 950), new PitchVoiceprints(), store, "m", CancellationToken.None);
        Assert.True(two.MaybeTwoVoices);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
