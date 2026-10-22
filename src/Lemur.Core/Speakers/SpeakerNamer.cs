using System.Text;
using System.Text.RegularExpressions;
using Lemur.Backends;
using Lemur.Config;
using Lemur.Logging;
using Lemur.Pipeline;
using Lemur.Transcripts;

namespace Lemur.Speakers;

/// <param name="Label">How the speaker is shown now: "Speaker B", "Dana?", "Dana Smith".</param>
/// <param name="Name">Who they are; null with <paramref name="NotAPerson"/> for a video or the TV.</param>
/// <param name="Remember">Keep their voice so later meetings recognise them.</param>
/// <param name="Consent">They agreed to that (design D3); required to remember someone new when the settings ask for it.
/// One tick per person: it counts for every voice given their name in the same save.</param>
/// <param name="Key">The speaker's key in identified.json, when the caller has it (the naming window). One person heard as
/// several voices, all given their name, share a label; only the key (or the letter) tells those voices apart.</param>
public sealed record NameChange(string Label, string? Name, bool Remember = false, bool Consent = false,
    string? ConsentNote = null, bool NotAPerson = false, string? Key = null);

public sealed record NameOutcome(IReadOnlyList<string> Messages, string? NotePath);

/// <summary>
/// Names speakers after the fact (design 3.8, 7.1): the naming window and `llmmr people name`. Updates
/// identified.json, the people folder, the note (rewritten if untouched, labels swapped in place if
/// the user edited it), the meeting file (whose action items may now be the user's) and the backend's copy.
/// </summary>
public sealed partial class SpeakerNamer(AppSettings settings, Func<IMeetingBackend>? backend = null)
{
    public PeopleStore People => new(settings.Speakers.ResolvedPeopleFolder);

    /// <summary>identified.json, or for meetings from before speaker names, one entry per speaker in labelled.json.</summary>
    public static IdentifiedResult Load(RecordingFolder rec)
    {
        if (rec.Read<IdentifiedResult>(rec.IdentifiedFile) is { } existing) return existing;
        var labelled = rec.Read<List<LabelledSegment>>(rec.LabelledFile) ?? throw new InvalidDataException("labelled.json missing");
        return new IdentifiedResult
        {
            Voices = "skipped: meeting is older than speaker names",
            Introductions = "skipped: meeting is older than speaker names",
            Speakers = labelled.Where(s => !s.IsMe).GroupBy(s => s.Speaker).Select(g => new IdentifiedSpeaker
            {
                Key = g.First().EngineSpeaker,
                Label = g.Key,
                EngineSpeaker = g.First().EngineSpeaker,
                TalkSeconds = Math.Round(g.Sum(s => s.End - s.Start), 1),
            }).ToList(),
        };
    }

    public async Task<NameOutcome> ApplyAsync(RecordingFolder rec, IReadOnlyList<NameChange> changes, CancellationToken ct,
        DateTimeOffset? clock = null)
    {
        var now = clock ?? DateTimeOffset.Now;
        var messages = new List<string>();
        var identified = Load(rec);
        var meta = rec.LoadMeta();
        var before = identified.Speakers.ToDictionary(s => s, Shown);
        var prints = rec.Read<MeetingVoiceprints>(rec.VoiceprintsFile);
        var store = People;

        foreach (var change in changes)
        {
            var s = Find(identified, change, messages);
            if (s is null) continue;
            if (change.NotAPerson || string.IsNullOrWhiteSpace(change.Name))
            {
                s.Status = SpeakerStatus.NotAPerson;
                s.PersonId = null;
                s.Name = null;
                continue;
            }

            var name = change.Name.Trim();
            bool isMe = Same(name, settings.DisplayName);
            var person = isMe ? store.Me() : store.Find(name);
            s.Status = isMe ? SpeakerStatus.Me : SpeakerStatus.Confirmed;
            s.Name = person?.Name ?? name;
            s.PersonId = person?.Id;
            if (!change.Remember) continue;

            // A tick per person (design D3): ticked for any voice given this name now, or on record from before.
            var tick = change.Consent ? change : changes.FirstOrDefault(c => c.Consent && c.Name is not null && Same(c.Name, name));
            if (settings.Speakers.AskConsent && !isMe && tick is null && person?.Consent is null)
            {
                messages.Add($"{name}: named in this meeting only — their voice isn't remembered without their agreement.");
                continue;
            }
            if (prints is null || !prints.Speakers.TryGetValue(s.Key, out var vector))
            {
                messages.Add(s.Status == SpeakerStatus.TooLittle || (prints is not null && s.Clips.Count == 0)
                    ? $"{name}: named, but there was too little clear audio of them to remember their voice."
                    : $"{name}: named, but there is no voiceprint for this meeting (audio deleted, or speaker names were off).");
                continue;
            }

            person ??= store.Create(name, isMe);
            s.PersonId = person.Id;
            s.Name = person.Name;
            if (tick is not null && person.Consent is null) person.Consent = new Consent { At = now, Note = tick.ConsentNote };
            // The transcription's spelling becomes an alias, so the next introduction matches (design 3.9).
            if (s.Introduction is { } intro && !person.Answers(intro.Name)) person.Aliases.Add(intro.Name);

            var print = SpeakerIdentifier.NewPrint(rec, meta, s, Vectors.FromBase64(vector), prints.Model, confirmed: true, now);
            bool hasClip = person.Prints.Any(p => p.Kind == s.Kind && p.Model == prints.Model && p.Clip is not null);
            if (!hasClip && File.Exists(s.Track == Track.Others ? rec.OthersWav : rec.MicWav) && s.Clips.Count > 0)
                print.Clip = store.SaveClip(person.Id, print.Id, SpeakerIdentifier.ReferenceClip(rec, s));
            bool kept = PeopleStore.AddPrint(person, print);
            store.Save(person, now);
            messages.Add(kept ? $"{person.Name}: voice remembered." : $"{person.Name}: already known; nothing new to remember from this meeting.");
        }

        rec.Write(rec.IdentifiedFile, identified);
        var notePath = UpdateNote(rec, identified, Changed(identified, before), messages);
        MeetingFile.TryWrite(rec, settings.DisplayName, new Vocabulary(settings.Vocabulary), transcript: settings.MeetingFile.IncludeTranscript);
        await RenameInBackendAsync(rec, identified, messages, ct);
        return new NameOutcome(messages, notePath);
    }

    /// <summary>What the note calls a speaker: the user's name for a voice marked as them.</summary>
    string Shown(IdentifiedSpeaker s) => s.Status == SpeakerStatus.Me ? settings.DisplayName : Naming.DisplayLabel(s);

    /// <summary>By key when the caller has it; else by label, which voices given the same name share: then only their
    /// letters tell them apart, and guessing would rename the wrong one.</summary>
    static IdentifiedSpeaker? Find(IdentifiedResult identified, NameChange change, List<string> messages)
    {
        var voices = identified.Speakers.Where(x => !x.IsMe).ToList();
        if (change.Key is not null && voices.FirstOrDefault(x => x.Key == change.Key) is { } byKey) return byKey;
        var shown = voices.Where(x => Same(Naming.DisplayLabel(x), change.Label)).ToList();
        if (shown.Count > 1)
        {
            messages.Add($"{shown.Count} voices are called '{change.Label}' in this meeting; name each by its letter ({string.Join(", ", shown.Select(x => x.Label))}).");
            return null;
        }
        var s = shown.FirstOrDefault() ?? voices.FirstOrDefault(x => Same(x.Label, change.Label));
        if (s is null) messages.Add($"No speaker called '{change.Label}' in this meeting.");
        return s;
    }

    /// <summary>A voice whose label changed: its letter (which transcript lines are its), and what the note called it.</summary>
    internal sealed record Rename(string Letter, string Old, string New);

    /// <summary>
    /// The labels that changed. <see cref="Whole"/> ones can be swapped as text: the old label was one voice's, or every
    /// voice that had it changed the same way. <see cref="ByLine"/> ones only in the lines the voice starts: other lines
    /// keep the old label (another voice given the same name, or the user's own lines).
    /// </summary>
    internal sealed record Renames(List<(string Old, string New)> Whole, List<Rename> ByLine, Dictionary<string, string> ShownBefore)
    {
        public int Count => Whole.Count + ByLine.Count;
    }

    Renames Changed(IdentifiedResult identified, Dictionary<IdentifiedSpeaker, string> before)
    {
        var voices = identified.Speakers.Where(s => !s.IsMe && before.ContainsKey(s)).ToList();
        var whole = new List<(string Old, string New)>();
        var byLine = new List<Rename>();
        foreach (var had in voices.GroupBy(s => before[s]))
        {
            var changed = had.Where(s => Shown(s) != had.Key).ToList();
            if (changed.Count == 0) continue;
            var now = had.Select(Shown).Distinct().ToList();
            if (now.Count == 1 && !Same(had.Key, settings.DisplayName)) whole.Add((had.Key, now[0]));
            else byLine.AddRange(changed.Select(s => new Rename(s.Label, had.Key, Shown(s))));
        }
        var shownBefore = voices.GroupBy(s => s.Label).ToDictionary(g => g.Key, g => before[g.First()]);
        return new Renames(whole, byLine, shownBefore);
    }

    /// <summary>Under <see cref="RecordingFolder.Lock"/>: the meeting may be renamed meanwhile (the Recordings window, the queue),
    /// which moves its note and changes its title.</summary>
    string? UpdateNote(RecordingFolder rec, IdentifiedResult identified, Renames renames, List<string> messages)
    {
        using var held = RecordingFolder.Lock();
        if (renames.Count == 0) return rec.LoadState().NotePath;

        // A label other lines keep can't be swapped in the summary: which mentions were this voice is unknown.
        if (File.Exists(rec.SummaryFile))
            File.WriteAllText(rec.SummaryFile, SwapLabels(File.ReadAllText(rec.SummaryFile), renames.Whole, transcriptOnly: false));
        SwapInAbout(rec, renames.Whole);
        var meta = rec.LoadMeta();
        var state = rec.LoadState();
        if (state.NotePath is null || !File.Exists(state.NotePath)) return state.NotePath;
        var summary = File.Exists(rec.SummaryFile) ? File.ReadAllText(rec.SummaryFile) : "";
        var labelled = rec.Read<List<LabelledSegment>>(rec.LabelledFile) ?? [];
        var named = Naming.Apply(labelled, identified, settings.DisplayName);
        var people = Naming.People(identified);

        if (state.NoteHash is not null && NoteWriter.Hash(state.NotePath) == state.NoteHash)
        {
            NoteWriter.Rewrite(state.NotePath, meta, summary, named, state.Backend ?? "?", state.WebUrl, people, rec.Name);
            state.NoteHash = NoteWriter.Hash(state.NotePath);
            state.PageVersion = NoteHtml.PageVersion;
            rec.SaveState(state);
            messages.Add("Note updated.");
        }
        else
        {
            // Edited by the user: swap labels in place so their edits survive. Real names being corrected are
            // only changed in transcript lines; elsewhere they may be the person being talked about.
            var text = File.ReadAllText(state.NotePath);
            text = SwapLabels(text, renames.Whole, transcriptOnly: false, onlySynthetic: true);
            text = SwapLabels(text, renames.Whole.Where(r => !IsSynthetic(r.Old)).ToList(), transcriptOnly: true);
            text = SwapTurns(text, labelled, renames.ShownBefore, renames.ByLine);
            text = SetPeople(text, people);
            File.WriteAllText(state.NotePath, text, new UTF8Encoding(false));
            NoteWriter.RewriteHtml(state.NotePath, meta, summary, named, state.Backend ?? "?", state.WebUrl, people, rec.Name);
            messages.Add(renames.Whole.Any(r => !IsSynthetic(r.Old)) || renames.ByLine.Count > 0
                ? "Note updated in place (you had edited it). Transcript lines are corrected; the summary may still mention the old name."
                : "Note updated in place (you had edited it).");
        }
        return state.NotePath;
    }

    /// <summary>
    /// What the summary model wrote about the meeting from the summary, so with its labels: the one-line description (the
    /// Recordings window, the note's front matter), the title it suggested, and the title if Lemur chose it (the note's
    /// files are renamed to match; a title the user typed is theirs). Swapped as the summary is.
    /// </summary>
    void SwapInAbout(RecordingFolder rec, IReadOnlyList<(string Old, string New)> renamed)
    {
        if (renamed.Count == 0) return;
        var meta = rec.LoadMeta();
        string? Swapped(string? text) => text is null ? null : SwapLabels(text, renamed, transcriptOnly: false);
        var (description, suggested) = (Swapped(meta.Description), Swapped(meta.SuggestedTitle));
        if (description != meta.Description || suggested != meta.SuggestedTitle)
            rec.UpdateMeta(m => (m.Description, m.SuggestedTitle) = (description, suggested));
        if (meta.TitleByUser || Swapped(meta.Title) is not { } title || title == meta.Title) return;
        try
        {
            MeetingRename.Apply(rec, title, byUser: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // All or nothing, so the meeting keeps its title; renaming it in the Recordings window tries again.
            Log.Warn($"{rec.Name}: the title keeps the old name; renaming its note failed ({ex.GetType().Name})");
        }
    }

    /// <summary>
    /// Meetings named before naming reached their description and title (Lemur 1.1 and earlier): a "Speaker X" still in
    /// them whose voice has a name now is swapped, and their note and meeting file follow, as naming does now. The queue
    /// runs it once at start; a meeting without a label in its meta.json costs one small read.
    /// </summary>
    /// <returns>How many meetings were brought up to date.</returns>
    public int CatchUp(string root)
    {
        int done = 0;
        foreach (var rec in RecordingFolder.All(root))
        {
            try
            {
                var meta = rec.LoadMeta();
                var labels = new[] { meta.Description, meta.SuggestedTitle, meta.TitleByUser ? null : meta.Title }
                    .SelectMany(t => LabelInText().Matches(t ?? "").Select(m => m.Value)).Distinct().ToList();
                if (labels.Count == 0 || rec.LoadState().Stage != Stage.Done || rec.Read<IdentifiedResult>(rec.IdentifiedFile) is not { } identified)
                    continue;
                // Only a label every voice that had it is shown by the same way now, as naming swaps it.
                var renamed = labels
                    .Select(label => (Old: label, Now: identified.Speakers.Where(s => s.Label == label).Select(Shown).Distinct().ToList()))
                    .Where(x => x.Now.Count == 1 && x.Now[0] != x.Old)
                    .Select(x => (x.Old, x.Now[0])).ToList();
                if (renamed.Count == 0) continue;
                UpdateNote(rec, identified, new Renames(renamed, [], []), []);
                MeetingFile.TryWrite(rec, settings.DisplayName, new Vocabulary(settings.Vocabulary), transcript: settings.MeetingFile.IncludeTranscript);
                Log.Info($"{rec.Name}: names put into its description and title ({renamed.Count} {(renamed.Count == 1 ? "label" : "labels")})");
                done++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
            {
                Log.Warn($"{rec.Name}: couldn't put names into its description ({ex.GetType().Name})");
            }
        }
        return done;
    }

    async Task RenameInBackendAsync(RecordingFolder rec, IdentifiedResult identified, List<string> messages, CancellationToken ct)
    {
        if (backend is null) return;
        var transcript = rec.Read<TranscriptResult>(rec.TranscriptFile);
        if (transcript?.JobId is null) return;
        var labelled = rec.Read<List<LabelledSegment>>(rec.LabelledFile) ?? [];
        var me = labelled.Where(s => s.IsMe).GroupBy(s => s.EngineSpeaker).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        try
        {
            using var b = backend();
            await b.NameSpeakersAsync(transcript, Naming.ForBackend(identified, settings.DisplayName, me), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"{rec.Name}: could not rename speakers in backend: {ex.Message}");
            messages.Add("Couldn't update the names in the backend's copy (the note is updated).");
        }
    }

    /// <summary>"Speaker B" and "Dana?" are labels we made up, safe to replace anywhere.</summary>
    static bool IsSynthetic(string label) => SyntheticLabel().IsMatch(label) || label.EndsWith('?') || label == Naming.NotAPersonLabel;

    /// <summary>In one pass, so two names swapped with each other (Dana and Sam) don't both end up as one.</summary>
    internal static string SwapLabels(string text, IReadOnlyList<(string Old, string New)> renamed, bool transcriptOnly, bool onlySynthetic = false)
    {
        var map = new Dictionary<string, string>();
        foreach (var (old, @new) in renamed)
            if (!onlySynthetic || IsSynthetic(old)) map.TryAdd(old, @new);
        if (map.Count == 0) return text;
        var any = string.Join("|", map.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape));
        return transcriptOnly
            ? Regex.Replace(text, $@"\] ({any}):\*\*", m => $"] {map[m.Groups[1].Value]}:**")
            : Regex.Replace(text, $@"(?<![\p{{L}}\p{{N}}])({any})(?![\p{{L}}\p{{N}}?])", m => map[m.Groups[1].Value]);
    }

    /// <summary>
    /// Changes a voice's label only in the transcript lines it starts, found by their time in the note as it was written
    /// (<see cref="TranscriptFormat.MergeTurns"/>: a line starts where the name shown changes). Where it carried on a line
    /// another voice started (both had one name), that line keeps the old name; the page is made again from the meeting.
    /// </summary>
    internal static string SwapTurns(string text, IReadOnlyList<LabelledSegment> labelled, IReadOnlyDictionary<string, string> shownBefore,
        IReadOnlyList<Rename> renames)
    {
        if (renames.Count == 0) return text;
        var byLetter = renames.GroupBy(r => r.Letter).ToDictionary(g => g.Key, g => g.First());
        string? previous = null;
        foreach (var seg in labelled)
        {
            if (string.IsNullOrWhiteSpace(seg.Text)) continue;
            var shown = seg.IsMe ? seg.Speaker : shownBefore.GetValueOrDefault(seg.Speaker, seg.Speaker);
            if (shown != previous && !seg.IsMe && byLetter.TryGetValue(seg.Speaker, out var r))
            {
                var clock = TranscriptFormat.Clock(seg.Start);
                text = text.Replace($"**[{clock}] {r.Old}:**", $"**[{clock}] {r.New}:**");
            }
            previous = shown;
        }
        return text;
    }

    /// <summary>Sets the front matter's people: line (adds it before tags: if missing).</summary>
    internal static string SetPeople(string text, IReadOnlyList<string> people) =>
        FrontMatter.Set(text, "people", people.Count == 0 ? null : $"[{string.Join(", ", people.Select(FrontMatter.Quote))}]");

    static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^Speaker [A-Z]{1,2}$")]
    private static partial Regex SyntheticLabel();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])Speaker [A-Z]{1,2}(?![\p{L}\p{N}])")]
    private static partial Regex LabelInText();
}
