using System.Text.Json;
using Lemur.Audio;
using Lemur.Config;
using Lemur.Pipeline;
using Lemur.Speakers;

namespace Lemur.Tests;

/// <summary>Naming a speaker afterwards reaches what was written about the meeting: its description and a title Lemur chose.</summary>
public class NamingAboutTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("llmmr-about").FullName;

    static readonly DateTimeOffset Start = new(new DateTime(2026, 10, 14, 9, 30, 0, DateTimeKind.Local));

    const string Reply = """
        Title: Planning with Speaker A
        About: Paul and Speaker A agreed on the plan.

        ## Summary
        Paul and Speaker A agreed on the plan.

        ## Action items
        - [ ] Speaker A - send the deck [00:00:01]
        """;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    AppSettings Settings() => new()
    {
        DisplayName = "Paul",
        RecordingsFolder = Path.Combine(_root, "rec"),
        NotesFolder = Path.Combine(_root, "notes"),
        Backend = { Type = BackendTypes.Scriberr, Scriberr = { BaseUrl = "https://x" } },
        Speakers = { PeopleFolder = Path.Combine(_root, "people") },
    };

    /// <summary>Two seconds: you on the mic, then someone on the call (Speaker A). Untitled unless given one.</summary>
    async Task<(RecordingFolder Rec, string Note)> Finished(AppSettings s, string title = "", bool byUser = false)
    {
        var rec = RecordingFolder.CreateNew(s.ResolvedRecordingsFolder, Start);
        int rate = AnchoredTrackWriter.SampleRate;
        var mic = new float[2 * rate];
        var others = new float[2 * rate];
        for (int i = 0; i < rate; i++) mic[i] = 0.2f * MathF.Sin(i * 0.2f);
        for (int i = rate; i < 2 * rate; i++) others[i] = 0.2f * MathF.Sin(i * 0.3f);
        for (int i = 0; i < 2 * rate; i++) mic[i] += 0.0005f * MathF.Sin(i * 1.7f);
        using (var a = new WavSampleSink(rec.MicWav)) a.Write(mic);
        using (var b = new WavSampleSink(rec.OthersWav)) b.Write(others);
        rec.SaveMeta(new RecordingMeta
        {
            Title = title, TitleByUser = byUser, Source = "Teams (Edge)", StartedAt = Start, EndedAt = Start.AddSeconds(2), DurationSeconds = 2,
        });
        rec.SaveState(new ProcessingState());
        var state = await new MeetingProcessor(s, () => new FakeBackend { Summary = Reply }).ProcessAsync(rec, CancellationToken.None);
        Assert.Equal(Stage.Done, state.Stage);
        return (rec, state.NotePath!);
    }

    static string? FileDescription(string note) =>
        JsonSerializer.Deserialize<MeetingFile.Content>(File.ReadAllText(MeetingFile.PathFor(note)), Json.Options)!.Description;

    [Fact]
    public async Task Naming_a_speaker_puts_their_name_into_the_description_and_a_title_lemur_chose()
    {
        var s = Settings();
        var (rec, note) = await Finished(s);
        Assert.Equal("Paul and Speaker A agreed on the plan.", rec.LoadMeta().Description);
        Assert.EndsWith("Planning with Speaker A.md", note);

        await new SpeakerNamer(s).ApplyAsync(rec, [new NameChange("Speaker A", "Dana Whitfield")], CancellationToken.None);

        var meta = rec.LoadMeta();
        Assert.Equal("Paul and Dana Whitfield agreed on the plan.", meta.Description); // what the Recordings window shows
        Assert.Equal(("Planning with Dana Whitfield", "Planning with Dana Whitfield", false), (meta.Title, meta.SuggestedTitle, meta.TitleByUser));
        var renamed = rec.LoadState().NotePath!;
        Assert.EndsWith("Planning with Dana Whitfield.md", renamed);
        Assert.False(File.Exists(note));
        var md = File.ReadAllText(renamed);
        Assert.DoesNotContain("Speaker A", md);
        Assert.Contains("description: \"Paul and Dana Whitfield agreed on the plan.\"", md);
        Assert.Equal("Paul and Dana Whitfield agreed on the plan.", FileDescription(renamed));
    }

    [Fact]
    public async Task A_title_you_typed_is_yours_and_only_the_description_changes()
    {
        var s = Settings();
        var (rec, note) = await Finished(s, "Planning with Speaker A", byUser: true);

        await new SpeakerNamer(s).ApplyAsync(rec, [new NameChange("Speaker A", "Dana Whitfield")], CancellationToken.None);

        var meta = rec.LoadMeta();
        Assert.Equal(("Planning with Speaker A", "Paul and Dana Whitfield agreed on the plan."), (meta.Title, meta.Description));
        Assert.Equal(note, rec.LoadState().NotePath);
    }

    [Fact]
    public async Task Meetings_named_before_this_are_caught_up_once_and_your_edits_stay()
    {
        var s = Settings();
        var (rec, _) = await Finished(s);
        var namer = new SpeakerNamer(s);
        await namer.ApplyAsync(rec, [new NameChange("Speaker A", "Dana Whitfield")], CancellationToken.None);
        var note = rec.LoadState().NotePath!;

        // As Lemur 1.1 left it: the summary named, the description not; and a note the user has edited since.
        rec.UpdateMeta(m => m.Description = "Paul, Speaker A and Speaker B agreed on the plan.");
        File.WriteAllText(note, File.ReadAllText(note).Replace("description: \"Paul and Dana Whitfield", "description: \"Paul, Speaker A and Speaker B") +
                                "\nMy own line.\n");

        Assert.Equal(1, namer.CatchUp(s.ResolvedRecordingsFolder));
        Assert.Equal("Paul, Dana Whitfield and Speaker B agreed on the plan.", rec.LoadMeta().Description); // nobody is Speaker B
        var md = File.ReadAllText(note);
        Assert.Contains("description: \"Paul, Dana Whitfield and Speaker B agreed on the plan.\"", md);
        Assert.Contains("My own line.", md);
        Assert.Equal("Paul, Dana Whitfield and Speaker B agreed on the plan.", FileDescription(note));

        Assert.Equal(0, namer.CatchUp(s.ResolvedRecordingsFolder));
    }
}
