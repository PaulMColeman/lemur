using Lemur.Audio;
using Lemur.Config;
using Lemur.Pipeline;
using Lemur.Speakers;
using Lemur.Transcripts;

// Writes a made-up Lemur world for the manual's screenshots: settings, people, nine meetings in every state, and
// real notes (made by NoteWriter) for the finished ones. Nothing here is real: names, meetings and audio are invented.
//   demo <home> screens   settings pointing at example.lan (for the settings, naming and about screenshots)
//   demo <home> working   settings pointing at the local stub server (a meeting is summarized "forever")
if (args.Length < 2) { Console.Error.WriteLine("demo <LEMUR_HOME> screens|working"); return 2; }
string home = Path.GetFullPath(args[0]);
bool working = args[1] == "working";
if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
string config = Path.Combine(home, "config"), data = Path.Combine(home, "data"), notes = Path.Combine(home, "Meeting Notes");
string recordings = Path.Combine(data, "recordings"), peopleFolder = Path.Combine(config, "people");
Directory.CreateDirectory(recordings);
Directory.CreateDirectory(notes);

const string Me = "Alex Rivera";
var settings = new AppSettings
{
    DisplayName = Me,
    NotesFolder = notes,
};
settings.Backend.Type = BackendTypes.Scriberr;
settings.Backend.Scriberr.BaseUrl = working ? "http://127.0.0.1:18080" : "https://scriberr.example.lan";
settings.Backend.Scriberr.SummaryModel = "meeting-summary";
settings.Summary.BaseUrl = working ? "http://127.0.0.1:18080/v1" : "https://llm.example.lan/v1";
settings.Summary.Model = "meeting-summary";
settings.Speakers.Enabled = true;
settings.Speakers.Level = 2;
// Words the transcription gets wrong (Settings → Vocabulary). "Cooper Netties" in the meetings below isn't listed yet,
// so Fix a word… finds it.
settings.Vocabulary =
[
    new VocabularyEntry { Term = "Northwind Traders", HeardAs = ["North Wind Traders", "Northwind Traitors"] },
    new VocabularyEntry { Term = "Priya Raman", HeardAs = ["Pria Raman"] },
    new VocabularyEntry { Term = "SSO" },
    new VocabularyEntry { Term = "OKRs", HeardAs = ["okay ours"] },
];
SettingsStore.Save(settings, Path.Combine(config, "settings.json"));

// People: you, and four colleagues who agreed to be recognised.
var people = new PeopleStore(peopleFolder);
var rng = new Random(7);
var now = DateTimeOffset.Now;
Person Add(string name, bool isMe, int prints, string? consent)
{
    var p = people.Create(name, isMe);
    if (consent is not null) p.Consent = new Consent { At = now.AddDays(-20), Note = consent };
    for (int i = 0; i < prints; i++)
    {
        var v = new float[256];
        for (int j = 0; j < v.Length; j++) v[j] = (float)(rng.NextDouble() - 0.5);
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        for (int j = 0; j < v.Length; j++) v[j] /= norm;
        p.Prints.Add(new Voiceprint
        {
            Id = Guid.NewGuid().ToString("N")[..12], Kind = isMe ? VoiceKind.Local : VoiceKind.Call, Model = "demo",
            Vector = Vectors.ToBase64(v), Seconds = 60 + 30 * i, Confirmed = true, Added = now.AddDays(-10 + i),
            From = new PrintSource { Recording = "demo", Title = "Weekly product sync", At = now.AddDays(-10 + i) },
        });
    }
    people.Save(p);
    return p;
}
Add(Me, true, 3, null);
Add("Dana Whitfield", false, 3, "Agreed in the design channel");
Add("Priya Raman", false, 1, "Agreed at the research sync");
Add("Sam Okafor", false, 2, "Agreed by email");
Add("Jordan Lee", false, 1, "Agreed on the call");

// The design review: a real-looking note with a transcript and a player.
var review = new (double T, string Who, string Text)[]
{
    (4, "me", "Okay, I think everyone's here. Thanks for joining. Today is the onboarding flow review."),
    (11, "B", "Thanks, Alex. I'll share my screen. This is the new three-step version."),
    (19, "C", "Hi everyone, Priya here from research. I'll jump in with what we saw in the interviews."),
    (24, "F", "Sorry, joining late."),
    (27, "B", "Step one asks for your name and work email, step two connects your calendar, and step three invites your team."),
    (41, "D", "Is the calendar step required? Last time that's where people dropped off."),
    (48, "C", "It was. In the interviews, five of eight people stopped at the calendar permissions screen."),
    (58, "me", "Could we make it skippable and remind them later from the dashboard?"),
    (66, "B", "Yes. I mocked that up: there's a \"Skip for now\" link under the connect button."),
    (75, "E", "From the support side, most tickets in the first week are about calendar permissions."),
    (84, "D", "Then let's also explain why we need the permission, right on that screen."),
    (92, "C", "A one-line reason helped a lot in the prototype test. People were fine once they knew it was read-only."),
    (103, "me", "Great. So the calendar becomes optional, with a short explanation of what we read."),
    (112, "B", "For the invite step I'd like to test two versions: a list of suggested teammates, or a share link."),
    (123, "D", "Suggested teammates needs the directory sync, and that isn't ready until November."),
    (131, "me", "Then let's ship the share link first and keep the suggestions for later."),
    (138, "B", "Agreed. I'll update the mockups by Thursday."),
    (145, "C", "I can run five quick tests on the new version next week, if someone sets up the prototype."),
    (153, "D", "I'll put the prototype on the staging site by Monday."),
    (161, "me", "Perfect. Last thing: the welcome email. Dana, does it still link to the old help pages?"),
    (169, "B", "It does. I'll need new copy from the docs team."),
    (177, "E", "I'll ask them today."),
    (183, "me", "Thanks. Open question: do we want an onboarding checklist on the dashboard at all?"),
    (192, "C", "People liked seeing their progress, but some found it naggy after the first day."),
    (200, "D", "We could hide it once three of the five items are done."),
    (208, "me", "Let's leave that open until Priya's tests are back. Anything else? Okay, thanks everyone."),
};
const string ReviewSummary = """
    ## Summary
    The team reviewed the new three-step onboarding flow. Research found that most people dropped off at the calendar
    step, so the calendar connection becomes optional, with a one-line explanation of what is read. The invite step ships
    with a share link first; suggested teammates wait for the directory sync in November.

    ## Action items
    - [ ] Dana Whitfield - Update the onboarding mockups with "Skip for now" and the permission explanation - Thursday
    - [ ] Sam Okafor - Put the prototype on the staging site - Monday
    - [ ] Priya Raman - Run five quick tests on the new flow - next week
    - [ ] Speaker E - Ask the docs team for new welcome-email copy - today

    ## Decisions
    - The calendar step becomes optional, because five of eight interviewees stopped there.
    - Explain on the calendar screen that access is read-only; it helped in the prototype test.
    - Ship the invite step with a share link; suggested teammates need the directory sync (November).

    ## Open questions
    - Should the dashboard show an onboarding checklist at all? Waiting for Priya's tests.

    ## Concepts and terms
    - **Directory sync** - importing the company's staff list, needed to suggest teammates.
    - **Share link** - a link a new user can send to colleagues to join their team.
    """;

string Label(string who) => who == "me" ? Me : $"Speaker {who}";
List<LabelledSegment> Segments((double T, string Who, string Text)[] lines, double gap = 1.2) =>
    [.. lines.Select((l, i) =>
    {
        double end = i + 1 < lines.Length ? lines[i + 1].T - gap * 0.3 : l.T + 6;
        return new LabelledSegment(l.T, end, Label(l.Who), l.Text, l.Who == "me", l.Who == "me" ? "S0" : "S" + l.Who);
    })];

IdentifiedSpeaker Speaker(string key, string label, SpeakerStatus status, string? name, double talk, double? score = null,
    Introduction? intro = null, Track track = Track.Others) => new()
{
    Key = key, Label = label, EngineSpeaker = key == "me" ? "S0" : "S" + key, IsMe = key == "me", Kind = VoiceKind.Call,
    Track = track, TalkSeconds = talk, Clips = [new ClipSpan(10, 16), new ClipSpan(40, 46)], Status = status, Name = name, Score = score,
    Introduction = intro,
};

static IdentifiedSpeaker NoClips(IdentifiedSpeaker s)
{
    s.Clips = [];
    return s;
}

var reviewIdentified = new IdentifiedResult
{
    Voices = "done", Introductions = "done", Model = "demo",
    Speakers =
    [
        Speaker("me", Me, SpeakerStatus.Me, Me, 412, track: Track.Mic),
        Speaker("B", "Speaker B", SpeakerStatus.Named, "Dana Whitfield", 538, 0.74),
        Speaker("C", "Speaker C", SpeakerStatus.Introduced, "Priya Raman", 301, intro: new Introduction("Speaker C", "Priya Raman", "Hi everyone, Priya here from research.", 19)),
        NoClips(Speaker("F", "Speaker F", SpeakerStatus.TooLittle, null, 2)),
        Speaker("D", "Speaker D", SpeakerStatus.Maybe, "Sam Okafor", 266, 0.52),
        Speaker("E", "Speaker E", SpeakerStatus.Unknown, null, 74),
    ],
};

// Nine meetings: six done (one titled from its summary, Google Meet having given it no name; the design review marked
// Keep recording; the oldest without its audio or its note's MP3), one waiting to retry, one being summarized, one waiting.
var today = now.Date;
Make("Security review: SSO rollout", today.AddDays(-36).AddHours(10), 41 * 60, Stage.Done, timed: true, audioDeleted: true, mp3Deleted: true,
    description: "SSO goes to staff first in November; contractors wait for the new identity provider, and Jordan writes the comms plan.");
Make("Hiring sync: support team", today.AddDays(-8).AddHours(15), 22 * 60, Stage.Done, timed: true, audioDeleted: true,
    description: "Two support roles open next week; Sam rewrites the job ad and Priya joins the second interviews.");
Make("Vendor contract check-in", today.AddDays(-3).AddHours(11), 18 * 60, Stage.Done, timed: true, source: "Google Meet (Edge)",
    suggested: "Vendor contract check-in",
    description: "Legal still hasn't signed the vendor contract; Dana chases it this afternoon and the launch date holds for now.");
Make("Weekly product sync", today.AddDays(-2).AddHours(9.5), 46 * 60, Stage.Done, timed: true,
    description: "Release 4.2 slips a week for the billing fix; Dana owns the changelog and Sam the release notes.");
var reviewRec = Make("Design review: onboarding flow", today.AddDays(-1).AddHours(14), 32 * 60, Stage.Done, timed: true,
    lines: review, summary: ReviewSummary, identified: reviewIdentified, audioSeconds: 215, keep: true,
    suggested: "Onboarding flow: optional calendar step",
    description: "The calendar step becomes optional, with a line on why it's needed; the invite step ships with a share link first.");
Make("Customer call: Northwind Traders", today.AddDays(-1).AddHours(16.5), 28 * 60, Stage.Done, timed: true, audioDeleted: true,
    description: "Northwind wants single sign-on before renewing in March; Sam sends enterprise pricing by Friday.");
Make("1:1 with Priya", today.AddDays(-1).AddHours(17.25), 24 * 60, Stage.Identified, timed: true,
    error: "Scriberr summarize: no summary came back (Scriberr gives the model 5 minutes; it will be retried)", attempts: 2);
Make("Quarterly planning (Sydney)", now.AddMinutes(-112), 58 * 60, Stage.Identified, timed: true);
Make("Standup (Sydney)", now.AddMinutes(-44), 14 * 60, Stage.Recorded, timed: false);
Console.WriteLine($"demo written to {home}");
return 0;

RecordingFolder Make(string title, DateTimeOffset started, double seconds, Stage stage, bool timed,
    (double T, string Who, string Text)[]? lines = null, string? summary = null, IdentifiedResult? identified = null,
    double audioSeconds = 0, bool audioDeleted = false, bool mp3Deleted = false, bool keep = false, string? error = null, int attempts = 0,
    string? description = null, string? suggested = null, string source = "Teams (Edge)")
{
    var rec = RecordingFolder.CreateNew(recordings, started);
    var ended = started.AddSeconds(seconds);
    var meta = new RecordingMeta
    {
        Title = title, Source = source, StartedAt = started, EndedAt = ended, DurationSeconds = seconds,
        MicDevice = "Microphone (Headset)", OutputDevice = "Headphones (Headset)",
        Description = description, SuggestedTitle = suggested, KeepRecording = keep,
    };
    rec.SaveMeta(meta);

    lines ??=
    [
        (5, "me", "Thanks for making time. Let's start with where we are."),
        (14, "B", "Sure. The short version is that we're on track, with one risk."),
        (26, "C", "The risk is the vendor contract; legal hasn't signed it yet."),
        (33, "C", "And the staging site moves to Cooper Netties next week."),
        (38, "me", "Okay. Who's following up with legal?"),
        (44, "B", "I will, this afternoon."),
    ];
    var labelled = Segments(lines);
    rec.Write(rec.TranscriptFile, new TranscriptResult([.. labelled.Select(l => new TranscriptSegment(l.Start, l.End, l.EngineSpeaker, l.Text))],
        "demo-" + rec.Name, "https://scriberr.example.lan/audio/demo-" + rec.Name));
    rec.Write(rec.LabelledFile, labelled);
    rec.Write(rec.IdentifiedFile, identified ?? new IdentifiedResult
    {
        Voices = "done", Introductions = "done", Model = "demo",
        Speakers = [Speaker("me", Me, SpeakerStatus.Me, Me, seconds * 0.35, track: Track.Mic), Speaker("B", "Speaker B", SpeakerStatus.Named, "Dana Whitfield", seconds * 0.4, 0.71), Speaker("C", "Speaker C", SpeakerStatus.Named, "Sam Okafor", seconds * 0.25, 0.68)],
    });

    // Step times like the measured ones (a 48-minute call: transcription 208 s, names 63 s, summary 114 s).
    var state = new ProcessingState { Stage = stage, Backend = "scriberr", JobId = "demo-" + rec.Name, WebUrl = "https://scriberr.example.lan/audio/demo-" + rec.Name };
    double minutes = seconds / 60;
    var t = ended.AddSeconds(2);
    (Step Step, double Seconds)[] plan =
    [
        (Step.Mix, 0.6), (Step.Transcribe, 30 + 3.6 * minutes), (Step.NameSpeakers, 20 + 1.0 * minutes),
        (Step.Summarize, 22 + 2.1 * minutes), (Step.WriteNote, 1 + 0.1 * minutes),
    ];
    if (timed)
    {
        foreach (var (step, secs) in plan)
        {
            if (PipelineSteps.Before(step) >= stage) break;
            state.Steps.Add(new StepTime { Step = step, StartedAt = t, FinishedAt = t.AddSeconds(secs * (0.9 + 0.2 * rng.NextDouble())), Tries = 1 });
            t = state.Steps[^1].FinishedAt!.Value.AddSeconds(0.2);
        }
    }
    if (error is not null)
    {
        state.Attempts = attempts;
        state.LastError = error;
        state.NextAttemptAt = now.AddMinutes(23);
        state.Steps.Add(new StepTime { Step = Step.Summarize, StartedAt = t, Tries = attempts });
    }

    if (stage == Stage.Done)
    {
        File.WriteAllText(rec.SummaryFile, summary ?? """
            ## Summary
            A regular check-in. The project is on track; the open risk is the vendor contract, which legal hasn't signed.

            ## Action items
            - [ ] Dana Whitfield - Follow up with legal on the vendor contract - this afternoon

            ## Decisions
            None.

            ## Open questions
            - When will legal sign the vendor contract?

            ## Concepts and terms
            None.
            """);
        string? wav = null;
        if (audioSeconds > 0)
        {
            // Quiet, softly varying noise: enough for the MP3 and the player; there is no real audio in the demo.
            wav = rec.MixWav;
            int rate = AnchoredTrackWriter.SampleRate;
            var samples = new float[(int)(audioSeconds * rate)];
            double phase = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                phase += 2 * Math.PI * 3 / rate;
                samples[i] = (float)((rng.NextDouble() - 0.5) * 0.02 * (1.2 + Math.Sin(phase)));
            }
            using (var sink = new WavSampleSink(wav)) sink.Write(samples);
            File.Copy(wav, rec.MicWav);
            File.Copy(wav, rec.OthersWav);
            rec.Write(rec.VoiceprintsFile, new MeetingVoiceprints { Model = "demo" });
        }
        var named = Naming.Apply(rec.Read<List<LabelledSegment>>(rec.LabelledFile)!, rec.Read<IdentifiedResult>(rec.IdentifiedFile), Me);
        state.NotePath = NoteWriter.Write(notes, meta, File.ReadAllText(rec.SummaryFile), [.. named], "scriberr", state.WebUrl,
            Naming.People(rec.Read<IdentifiedResult>(rec.IdentifiedFile)), rec.Name, wav);
        state.NoteHash = NoteWriter.Hash(state.NotePath);
        state.CompletedAt = t;
        state.AudioDeleted = audioDeleted;
        state.Mp3Deleted = mp3Deleted;
        if (audioDeleted) rec.DeleteAudio();
    }
    else if (stage == Stage.Recorded)
    {
        // Waiting: the two tracks exist (a second of quiet each; it never gets that far in the demo).
        using (var a = new WavSampleSink(rec.MicWav)) a.Write(new float[AnchoredTrackWriter.SampleRate]);
        using (var b = new WavSampleSink(rec.OthersWav)) b.Write(new float[AnchoredTrackWriter.SampleRate]);
    }
    rec.SaveState(state);
    return rec;
}
