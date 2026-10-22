using Lemur.Backends;
using Lemur.Config;
using Lemur.Pipeline;
using Lemur.Speakers;
using Lemur.Transcripts;

namespace Lemur.Cli;

/// <summary>Speaker names from the command line: everything the naming window and People tab do, for S2 and scripts.</summary>
static partial class Program
{
    /// <summary>A recording by folder path, or by its name under the recordings folder.</summary>
    static RecordingFolder Recording(AppSettings settings, string folderOrName)
    {
        // Without the separator tab completion leaves on the end, or the recording's name would be "".
        var path = Path.TrimEndingDirectorySeparator(Directory.Exists(folderOrName) ? folderOrName : Path.Combine(settings.ResolvedRecordingsFolder, folderOrName));
        if (!File.Exists(Path.Combine(path, "meta.json"))) throw new FileNotFoundException($"no recording '{folderOrName}'");
        return new RecordingFolder(path);
    }

    static async Task<int> Identify(AppSettings settings, string folderOrName, string? peopleFolder = null)
    {
        if (peopleFolder is not null) settings.Speakers.PeopleFolder = peopleFolder; // e.g. a test set of people
        var rec = Recording(settings, folderOrName);
        var transcript = rec.Read<TranscriptResult>(rec.TranscriptFile) ?? throw new InvalidOperationException("not transcribed yet");
        var env = rec.Read<MeetingProcessor.Envelopes>(rec.EnvelopesFile) ?? throw new InvalidOperationException("envelopes.json missing");
        if (!File.Exists(rec.MicWav)) Console.WriteLine("note: audio has been deleted, so only introductions can run");
        var labels = SpeakerLabeler.Label(transcript.Segments, env.Mic, env.Others, settings.DisplayName);
        var identifier = SpeakerIdentifier.FromSettings(settings, new WindowsCredentialStore());
        var result = await identifier.IdentifyAsync(rec, rec.LoadMeta(), labels, env, CancellationToken.None);
        rec.Write(rec.IdentifiedFile, result);

        Console.WriteLine($"voices:        {result.Voices}{(result.Model is null ? "" : $" (model {result.Model})")}");
        Console.WriteLine($"introductions: {result.Introductions}");
        foreach (var s in result.Speakers)
        {
            var score = s.Score is null ? "" : $" score {s.Score:0.000} (next {s.RunnerUp:0.000})";
            // The name and time only: what people said is transcript text, which the CLI never prints.
            var intro = s.Introduction is null ? "" : $" — introduced as {s.Introduction.Name} at {TranscriptFormat.Clock(s.Introduction.Time)}";
            Console.WriteLine($"  {s.Label,-12} {s.Kind,-5} {s.TalkSeconds,6:0}s talk, {s.Clips.Count} clips → {s.Status} {s.Name}{score}{intro}");
        }
        Console.WriteLine("(identified.json updated; notes unchanged — use `llmmr people name` or `llmmr process --from identified`)");
        return 0;
    }

    /// <summary>Finished meetings made again with this Lemur (word-by-word "me", 2026-10-14); counts only, never content.</summary>
    static async Task<int> Remake(AppSettings settings, string[] args)
    {
        var names = args.Skip(1).Where(a => !a.StartsWith("--")).ToList();
        if (names.Count == 0) return Fail("llmmr remake <recording>... (see llmmr --help)");
        var secrets = new WindowsCredentialStore();
        var remake = new MeetingRemake(settings, () => BackendFactory.Create(settings, secrets),
            SpeakerIdentifier.FromSettings(settings, secrets, learn: false));
        int failed = 0;
        foreach (var name in names)
        {
            var rec = Recording(settings, name);
            try
            {
                var r = await remake.RunAsync(rec, CancellationToken.None);
                Console.WriteLine($"{rec.Name}: you {r.MeBefore / 60:0.0} → {r.MeAfter / 60:0.0} min; {r.Lines} lines, {r.SplitLines} split " +
                                  $"between you and someone; names kept {r.NamesKept}, left to the voices {r.NamesLeft}; " +
                                  $"voiceprints taken out {r.PrintsRemoved}; summary {r.SummarySeconds:0}s");
                Console.WriteLine($"  backup: {r.Backup}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                Console.WriteLine($"{rec.Name}: not made again: {ex.Message}");
            }
        }
        return failed == 0 ? 0 : 1;
    }

    static async Task<int> People(AppSettings settings, string[] args)
    {
        var store = new PeopleStore(settings.Speakers.ResolvedPeopleFolder);
        switch (Arg(args, 1))
        {
            case "list":
            {
                Console.WriteLine($"people folder: {store.Folder}");
                foreach (var p in store.People().OrderByDescending(p => p.IsMe).ThenBy(p => p.Name))
                {
                    var kinds = string.Join(", ", p.Prints.GroupBy(x => x.Kind).Select(g => $"{g.Count()} {g.Key.ToString().ToLowerInvariant()}"));
                    var heard = p.Prints.Select(x => x.From?.At).Where(x => x is not null).DefaultIfEmpty().Max();
                    Console.WriteLine($"  {p.Name}{(p.IsMe ? " (you)" : "")}  [{p.Id}]  voiceprints: {(kinds.Length == 0 ? "none" : kinds)}" +
                                      $"{(heard is null ? "" : $"  last heard {heard:yyyy-MM-dd}")}" +
                                      $"{(p.Consent is null ? "" : $"  consent {p.Consent.At:yyyy-MM-dd}")}" +
                                      $"{(p.Aliases.Count == 0 ? "" : $"  also: {string.Join(", ", p.Aliases)}")}");
                }
                return 0;
            }
            case "name":
            {
                var rec = Recording(settings, Arg(args, 2));
                var change = new NameChange(Arg(args, 3), args.Contains("--not-a-person") ? null : Arg(args, 4),
                    Remember: args.Contains("--remember"), Consent: Opt(args, "--consent") is not null,
                    ConsentNote: Opt(args, "--consent"), NotAPerson: args.Contains("--not-a-person"));
                var secrets = new WindowsCredentialStore();
                var namer = new SpeakerNamer(settings, () => BackendFactory.Create(settings, secrets));
                var outcome = await namer.ApplyAsync(rec, [change], CancellationToken.None);
                foreach (var m in outcome.Messages) Console.WriteLine(m);
                if (outcome.NotePath is not null) Console.WriteLine($"note: {outcome.NotePath}");
                return 0;
            }
            case "rename":
            {
                var p = store.Find(Arg(args, 2)) ?? throw new ArgumentException($"nobody called '{Arg(args, 2)}'");
                var old = p.Name;
                p.Name = Arg(args, 3).Trim();
                if (!p.Answers(old)) p.Aliases.Add(old);
                store.Save(p);
                Console.WriteLine($"{old} → {p.Name} (old name kept as an alias; existing notes are unchanged)");
                return 0;
            }
            case "merge":
            {
                var from = store.Find(Arg(args, 2)) ?? throw new ArgumentException($"nobody called '{Arg(args, 2)}'");
                var into = store.Find(Arg(args, 3)) ?? throw new ArgumentException($"nobody called '{Arg(args, 3)}'");
                if (from.Id == into.Id) throw new ArgumentException("that is the same person");
                store.MergeInto(from, into);
                Console.WriteLine($"{from.Name} merged into {into.Name}");
                return 0;
            }
            case "delete":
            {
                var p = store.Find(Arg(args, 2)) ?? throw new ArgumentException($"nobody called '{Arg(args, 2)}'");
                if (!args.Contains("--yes")) return Fail($"this deletes {p.Name}'s voiceprints and clips; add --yes to confirm");
                store.Delete(p);
                Console.WriteLine($"{p.Name} deleted (voiceprints and clips removed)." +
                                  (store.Folder.Contains("OneDrive", StringComparison.OrdinalIgnoreCase)
                                      ? " OneDrive keeps deleted files in its recycle bin and version history for a while; empty them to remove every copy."
                                      : ""));
                return 0;
            }
            case "export":
                Console.WriteLine($"exported {PeopleTransfer.Export(store, Arg(args, 2))} people to {Arg(args, 2)}");
                return 0;
            case "import":
            {
                var (added, merged) = PeopleTransfer.Import(store, Arg(args, 2));
                Console.WriteLine($"imported: {added} new, {merged} merged with people you already had");
                return 0;
            }
            default:
                return Fail("llmmr people list|name|rename|merge|delete|export|import (see llmmr --help)");
        }
    }

    static async Task<int> Voices(AppSettings settings, string[] args)
    {
        switch (Arg(args, 1))
        {
            case "calibrate":
            {
                using var client = VoiceprintClient.Create(settings, new WindowsCredentialStore(), out var whyNot)
                                   ?? throw new InvalidOperationException(whyNot);
                var v = (await client.EmbedAsync([Calibration.SignalWav()], CancellationToken.None))[0];
                var store = new PeopleStore(settings.Speakers.ResolvedPeopleFolder);
                var id = store.Models.Resolve(v, settings.Speakers.Model, DateTimeOffset.Now);
                var numbers = BuiltInThresholds.For(v, settings.Speakers.Level, settings.Speakers.Thresholds);
                Console.WriteLine($"model:        {settings.Speakers.Model} at {settings.Speakers.ResolvedBaseUrl(settings)}");
                Console.WriteLine($"fingerprint:  {id} ({v.Length} dimensions)");
                Console.WriteLine(numbers is null
                    ? "numbers:      none for this model — run `llmmr voices benchmark` and put its output in speakers.thresholds"
                    : $"numbers:      accept {numbers.Accept:0.000}, maybe {numbers.Maybe:0.000}, margin {numbers.Margin:0.000}, kind penalty {numbers.KindPenalty:0.000} (level {settings.Speakers.Level})");
                return 0;
            }
            case "benchmark":
                return await VoiceBenchmark.Run(settings, args);
            default:
                return Fail("llmmr voices calibrate|benchmark (see llmmr --help)");
        }
    }
}
