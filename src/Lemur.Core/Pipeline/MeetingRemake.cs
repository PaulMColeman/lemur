using Lemur.Backends;
using Lemur.Config;
using Lemur.Logging;
using Lemur.Speakers;
using Lemur.Transcripts;

namespace Lemur.Pipeline;

/// <param name="MeBefore">Seconds of the meeting labelled as the user before, and <paramref name="MeAfter"/> after.</param>
/// <param name="NamesKept">Names the user gave speakers, carried over.</param>
/// <param name="NamesLeft">Names the user gave the letter that held their own stray lines, left for the voices to decide again.</param>
/// <param name="PrintsRemoved">Voiceprints that naming saved from that letter, taken out of the person they were saved to.</param>
/// <param name="Backup">The folder with the files as they were.</param>
public sealed record RemakeResult(double MeBefore, double MeAfter, int Lines, int SplitLines, int NamesKept, int NamesLeft,
    int PrintsRemoved, double SummarySeconds, string Backup);

/// <summary>
/// Makes a finished meeting's notes again with the current Lemur, from the audio and transcription job it already has: the
/// transcript fetched again (with word times), labels, speaker names, summary, description and note (Paul, 2026-10-14:
/// "so we have a clean baseline" after "me" became word by word). What the user decided is kept: the title, the names they
/// gave speakers, the letters the notes used, and where the note is. One exception: a name given to the letter that held
/// the user's own stray lines. The old labelling put lines of theirs under their voice group's letter, and its clip, cut
/// from the call track, played whoever murmured over them, so that name is left for the voices to decide again, and a
/// voiceprint the naming saved from it is taken out of that person first (left in while the voices are matched, the letter
/// recognised itself: 0.93 against 0.29 on Paul's calls). The processing history (steps, completed time) stays,
/// so time estimates and the audio's age don't move, and nothing is learned into the people folder twice.
/// Only for a note Lemur wrote and nobody edited. Everything is written at the end, after a backup, or nothing is. Meetings
/// stay Done throughout, so the tray's queue leaves them alone while this runs.
/// </summary>
public sealed class MeetingRemake(AppSettings settings, Func<IMeetingBackend> backendFactory, SpeakerIdentifier identifier)
{
    /// <summary>A voiceprint this close to the meeting's own for that letter is the one naming saved from it.</summary>
    const double SamePrint = 0.999;

    public async Task<RemakeResult> RunAsync(RecordingFolder rec, CancellationToken ct)
    {
        var state = rec.LoadState();
        if (state.Stage != Stage.Done) throw new InvalidOperationException("its notes aren't made yet");
        if (state.JobId is null) throw new InvalidOperationException("there's no transcription job to fetch it from");
        if (!File.Exists(rec.MixWav) || !File.Exists(rec.MicWav) || !File.Exists(rec.OthersWav))
            throw new InvalidOperationException("its audio has been deleted");
        if (!Untouched(state)) throw new InvalidOperationException("its note was edited or moved, and making it again would lose that");
        var env = rec.Read<MeetingProcessor.Envelopes>(rec.EnvelopesFile) ?? throw new InvalidDataException("envelopes.json missing");
        var meta = rec.LoadMeta();
        var oldLabels = rec.Read<List<LabelledSegment>>(rec.LabelledFile) ?? [];
        var oldIdentified = rec.Read<IdentifiedResult>(rec.IdentifiedFile);
        var oldPrints = rec.Read<MeetingVoiceprints>(rec.VoiceprintsFile);
        var printsFile = File.Exists(rec.VoiceprintsFile) ? File.ReadAllBytes(rec.VoiceprintsFile) : null;
        var title = string.IsNullOrWhiteSpace(meta.Title) ? $"Meeting {meta.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm}" : meta.Title;

        using var backend = backendFactory();
        if (backend is not IResumableTranscription resumable || backend.Kind != state.Backend)
            throw new InvalidOperationException($"it was transcribed by {state.Backend}, and that isn't the backend set up now");
        // The finished job's transcript, now with word times. A server that lost the job transcribes the audio again.
        TranscriptionJob? newJob = null;
        var transcript = await resumable.TranscribeAsync(rec.MixWav, title, TimeSpan.FromSeconds(meta.DurationSeconds), state.JobId,
            job => newJob = job, ct);

        var letters = oldLabels.Where(s => !s.IsMe).GroupBy(s => s.EngineSpeaker).ToDictionary(g => g.Key, g => g.First().Speaker);
        var labels = SpeakerLabeler.Label(transcript.Segments, env.Mic, env.Others, settings.DisplayName, letters);
        var vocabulary = new Vocabulary(settings.Vocabulary);
        labels = labels with { Segments = vocabulary.Fix(labels.Segments, out _) };
        var meCluster = labels.MeEngineSpeaker;
        // Out before the voices are matched: left in, that letter recognises itself (0.93 on Paul's calls, 2026-10-14).
        var takenOut = TakeOutPrintsFromOwnLetter(rec, oldIdentified, oldPrints, meCluster);

        try
        {
            var identified = await identifier.IdentifyAsync(rec, meta, labels, env, ct); // writes voiceprints.json as it goes
            int kept = 0, left = 0;
            foreach (var old in oldIdentified?.Speakers ?? [])
            {
                if (old.IsMe || old.Status is not (SpeakerStatus.Confirmed or SpeakerStatus.NotAPerson or SpeakerStatus.Me)) continue;
                var now = identified.Speakers.FirstOrDefault(s => !s.IsMe && s.Key == old.Key);
                if (now is null) continue; // nobody is left under that letter
                if (old.Key == meCluster)
                {
                    left++;
                    continue;
                }
                now.Status = old.Status;
                now.Name = old.Name;
                now.PersonId = old.PersonId;
                kept++;
            }

            var named = Naming.Apply(labels.Segments, identified, settings.DisplayName);
            var started = DateTimeOffset.Now;
            var said = TranscriptFormat.ToPlainText(named);
            var reply = await backend.SummarizeAsync(transcript,
                SummaryPrompt.For(settings.DisplayName, settings.Summary.PromptFile, meta.StartedAt, vocabulary.Glossary(said)),
                said, ct);
            double summarySeconds = (DateTimeOffset.Now - started).TotalSeconds;
            var (summary, suggested, description) = MeetingAbout.Split(reply);
            summary = vocabulary.Fix(summary);
            suggested = suggested is null ? null : vocabulary.Fix(suggested);
            description = description is null ? null : vocabulary.Fix(description);
            if (string.IsNullOrWhiteSpace(summary))
                throw new BackendException("summary: only the title came back, no summary (cut off?); nothing was changed");

            // An untitled meeting takes its suggested title, as when it was first made (and its note's files with it).
            if (suggested is not null && MeetingAbout.NeedsTitle(rec.LoadMeta())) MeetingRename.Apply(rec, suggested, byUser: false);

            string backup;
            using (RecordingFolder.Lock())
            {
                meta = rec.LoadMeta(); // renamed meanwhile, in the Recordings window or just above
                state = rec.LoadState();
                if (!Untouched(state)) throw new InvalidOperationException("its note changed while it was being made again; nothing was changed");
                backup = Backup(rec, state.NotePath!);
                if (description is not null) meta.Description = description;
                if (suggested is not null) meta.SuggestedTitle = suggested;
                if (newJob is not null)
                {
                    state.JobId = newJob.Id;
                    state.WebUrl = newJob.WebUrl;
                }
                // The note first: if it can't be written (open in an editor that locks it), nothing else has changed.
                NoteWriter.Rewrite(state.NotePath!, meta, summary, named, state.Backend ?? "?", state.WebUrl, Naming.People(identified), rec.Name);
                rec.Write(rec.TranscriptFile, transcript);
                rec.Write(rec.LabelledFile, labels.Segments);
                rec.Write(rec.IdentifiedFile, identified);
                File.WriteAllText(rec.SummaryFile, summary);
                rec.SaveMeta(meta);
                state.NoteHash = NoteWriter.Hash(state.NotePath!);
                state.PageVersion = NoteHtml.PageVersion;
                rec.SaveState(state);
            }
            MeetingFile.TryWrite(rec, settings.DisplayName, vocabulary, transcript: settings.MeetingFile.IncludeTranscript); // items reworded by the new summary are new items
            printsFile = null; // the meeting is made: its new voiceprints stay, and the ones taken out stay out
            int removed = takenOut?.Prints.Count ?? 0;
            DeleteClips(takenOut);
            takenOut = null;

            var names = Naming.ForBackend(identified, settings.DisplayName, meCluster);
            if (names.Count > 0)
            {
                try
                {
                    await backend.NameSpeakersAsync(transcript, names, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warn($"{rec.Name}: could not rename speakers in backend (the note is made): {ex.Message}");
                }
            }

            static double Me(IEnumerable<LabelledSegment> s) => s.Where(x => x.IsMe).Sum(x => x.End - x.Start);
            var result = new RemakeResult(Me(oldLabels), Me(labels.Segments), labels.Segments.Count, labels.SplitLines, kept, left,
                removed, summarySeconds, backup);
            Log.Info($"{rec.Name}: made again — you {result.MeBefore / 60:0.0} → {result.MeAfter / 60:0.0} min, {result.SplitLines} lines split, " +
                     $"{kept} names kept, {left} left to the voices, {removed} voiceprints taken out");
            return result;
        }
        catch
        {
            // Identification rewrote this meeting's voiceprints; the meeting is as it was, so they are too.
            if (printsFile is not null) File.WriteAllBytes(rec.VoiceprintsFile, printsFile);
            PutBack(takenOut);
            throw;
        }
    }

    static bool Untouched(ProcessingState state) =>
        state.NotePath is { } note && File.Exists(note) && state.NoteHash is not null && NoteWriter.Hash(note) == state.NoteHash;

    /// <summary>The files a remake replaces, as they were, in a folder beside them.</summary>
    static string Backup(RecordingFolder rec, string notePath)
    {
        var folder = Path.Combine(rec.Path, $"before-remake-{DateTimeOffset.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(folder);
        foreach (var file in new[] { rec.TranscriptFile, rec.LabelledFile, rec.IdentifiedFile, rec.SummaryFile,
                     rec.MetaFile, rec.StateFile, notePath, NoteWriter.HtmlPath(notePath), MeetingFile.PathFor(notePath) })
            if (File.Exists(file)) File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
        return folder;
    }

    /// <summary>Voiceprints taken out of a person, to put back if the remake fails.</summary>
    sealed record TakenOut(PeopleStore Store, string PersonId, List<Voiceprint> Prints);

    /// <summary>
    /// A voiceprint saved by naming the letter that held the user's own stray lines came from audio cut around their speech:
    /// it goes. It is found by its source (this meeting) and by being this meeting's own voiceprint for that letter.
    /// </summary>
    TakenOut? TakeOutPrintsFromOwnLetter(RecordingFolder rec, IdentifiedResult? oldIdentified, MeetingVoiceprints? oldPrints, string? meCluster)
    {
        if (meCluster is null || oldPrints is null || !oldPrints.Speakers.TryGetValue(meCluster, out var saved)) return null;
        var named = oldIdentified?.Speakers.FirstOrDefault(s => !s.IsMe && s.Key == meCluster && s.PersonId is not null);
        if (named is null) return null;
        var store = new PeopleStore(settings.Speakers.ResolvedPeopleFolder);
        var person = store.People().FirstOrDefault(p => p.Id == named.PersonId);
        if (person is null) return null;
        var vector = Vectors.FromBase64(saved);
        var drop = person.Prints
            .Where(p => p.From?.Recording == rec.Name && p.Model == oldPrints.Model && Vectors.Cosine(p.Values(), vector) >= SamePrint)
            .ToList();
        if (drop.Count == 0) return null;
        foreach (var p in drop) person.Prints.Remove(p);
        store.Save(person);
        return new TakenOut(store, person.Id, drop);
    }

    void PutBack(TakenOut? taken)
    {
        if (taken is null) return;
        try
        {
            var person = taken.Store.People().FirstOrDefault(p => p.Id == taken.PersonId);
            if (person is null) return;
            person.Prints.AddRange(taken.Prints.Where(t => person.Prints.All(p => p.Id != t.Id)));
            taken.Store.Save(person);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Log.Warn($"a voiceprint taken out for a remake couldn't be put back: {ex.Message}");
        }
    }

    static void DeleteClips(TakenOut? taken)
    {
        foreach (var p in taken?.Prints ?? [])
        {
            try
            {
                if (taken!.Store.ClipPath(p) is { } clip && File.Exists(clip)) File.Delete(clip);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"a voiceprint's clip couldn't be deleted: {ex.Message}");
            }
        }
    }
}
