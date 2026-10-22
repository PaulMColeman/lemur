using Lemur.Transcripts;

namespace Lemur.Pipeline;

public enum RowKind
{
    /// <summary>Being recorded right now.</summary>
    Recording,
    /// <summary>Being turned into notes right now.</summary>
    Working,
    /// <summary>Queued for notes.</summary>
    Waiting,
    /// <summary>Failed; will be tried again at NextAttemptAt.</summary>
    Retrying,
    Done,
    /// <summary>The app stopped while recording it: audio but no meta.json (<see cref="RecordingFolder.Interrupted"/>).</summary>
    Interrupted,
}

public enum StepState { Done, Working, Failed, Next, Recording }

/// <summary>
/// One line of a recording's steps: the recording itself, then each step to notes. <see cref="Time"/> is how long it
/// took (done), has taken so far (working) or should take ("~2 min"); <see cref="Note"/> is the small print beside it.
/// </summary>
public sealed record StepLine(string Name, StepState State, string Time, string? Note = null);

/// <summary>One line of the Recordings window. <see cref="Detail"/> is the longer text shown when it is selected.</summary>
public sealed record RecordingRow(
    string Name, string Path, DateTimeOffset? StartedAt, string Title, double Seconds, RowKind Kind,
    string Status, string Detail, string? NotePath, string? WebUrl)
{
    public bool CanRetry => Kind is RowKind.Retrying or RowKind.Waiting;
    public bool HasNote => NotePath is not null && File.Exists(NotePath);

    public IReadOnlyList<StepLine> Steps { get; init; } = [];

    /// <summary>Estimated processing still to do (zero when done; for queued ones, not counting the wait).</summary>
    public TimeSpan Remaining { get; init; }

    /// <summary>When the notes should be ready, or how long they took: the line under the steps.</summary>
    public string? Expected { get; init; }

    /// <summary>One sentence on what the meeting was about: the summary model's, or the start of its summary.</summary>
    public string? Description { get; init; }

    /// <summary>Everyone named for sure in it.</summary>
    public IReadOnlyList<string> People { get; init; } = [];

    /// <summary>Where it was recorded: "Teams (Edge)", "Google Meet (Edge)", "Manual".</summary>
    public string Source { get; init; } = "";

    /// <summary>It has no real title yet: <see cref="Title"/> is a stand-in until its summary suggests one.</summary>
    public bool Untitled { get; init; }

    /// <summary>Marked "Keep recording": the clean-up after settings.keepAudioDays leaves its audio and its note's MP3 alone.</summary>
    public bool Keep { get; init; }

    /// <summary>Its working audio (the two tracks and the mix) hasn't been deleted.</summary>
    public bool HasAudio { get; init; }

    /// <summary>Its note's MP3 is beside the note.</summary>
    public bool HasMp3 { get; init; }

    /// <summary>When its notes were made: the clean-up's days count from then.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>"Keep recording" has something to keep. Not for an interrupted recording: it has no meta.json to keep it in.</summary>
    public bool CanKeep => Kind != RowKind.Interrupted && (HasAudio || HasMp3);
}

/// <summary>The recording being made now, as the tray app knows it.</summary>
/// <param name="Keep">"Keep recording" has been ticked for it.</param>
public sealed record LiveRecording(string Path, string Title, string Source, TimeSpan Elapsed, bool Keep = false);

/// <summary>
/// Every recording and what is happening to it, for the Recordings window. Reads each folder's meta.json and
/// state.json, and re-reads only those that changed since the last refresh (a year of meetings is a few
/// thousand small files).
/// </summary>
public sealed class RecordingList
{
    /// <summary>A recording's files as last read; <see cref="Stamp"/> is the newest of their write times.</summary>
    sealed record Entry(DateTime Stamp, RecordingMeta? Meta, ProcessingState State, string? Description, IReadOnlyList<string> People);

    readonly Dictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The estimates the last <see cref="Build"/> used.</summary>
    public StepEstimates Estimates { get; private set; } = StepEstimates.Measured();

    public List<RecordingRow> Build(string root, LiveRecording? live, (string Recording, Stage Stage)? working, DateTimeOffset now)
    {
        var entries = new List<(RecordingFolder Rec, Entry Entry)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rec in RecordingFolder.All(root))
        {
            seen.Add(rec.Path);
            if (Read(rec) is { } entry) entries.Add((rec, entry));
        }
        foreach (var path in _cache.Keys.Where(k => !seen.Contains(k)).ToList()) _cache.Remove(path);
        var estimates = Estimates = StepEstimates.From(entries.Select(e => (e.Entry.Meta?.DurationSeconds ?? 0, e.Entry.State)));

        var rows = new List<RecordingRow>();
        if (live is not null) rows.Add(LiveRow(live, estimates));
        foreach (var (rec, e) in entries)
            rows.Add(Row(rec, e.Meta, e.State, working is { } w && w.Recording == rec.Name ? w.Stage : null, now, estimates) with
            {
                Description = e.Description,
                People = e.People,
            });

        foreach (var rec in RecordingFolder.Interrupted(root, live?.Path))
        {
            double seconds = 0;
            try { seconds = new FileInfo(rec.MicWav).Length / (double)(Audio.AnchoredTrackWriter.SampleRate * 2); } catch (IOException) { }
            rows.Add(new RecordingRow(rec.Name, rec.Path, rec.StartedFromName, "Unfinished recording", seconds, RowKind.Interrupted,
                "Interrupted — the app closed while recording",
                "Lemur stopped while this was being recorded (it crashed, was closed, or the PC lost power), so it was " +
                "never finished or turned into notes. \"Make notes anyway\" uses what was recorded; \"Delete\" removes it.", null, null)
            {
                Untitled = true,
                Description = "Never finished: \"Make notes anyway\" turns what was recorded into notes.",
            });
        }

        Queue(rows, now);
        return [.. rows.OrderByDescending(r => r.Kind == RowKind.Recording).ThenByDescending(r => r.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Notes are made one meeting at a time, oldest first (<see cref="ProcessingQueue"/>): a waiting meeting's notes
    /// come after the one being worked on and every meeting waiting before it.
    /// </summary>
    static void Queue(List<RecordingRow> rows, DateTimeOffset now)
    {
        var ahead = rows.Where(r => r.Kind == RowKind.Working).Aggregate(TimeSpan.Zero, (sum, r) => sum + r.Remaining);
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Kind == RowKind.Working)
                rows[i] = rows[i] with { Expected = $"Notes expected around {Clock(now + rows[i].Remaining)} ({Roughly(rows[i].Remaining)} more)." };
        }
        foreach (int i in Enumerable.Range(0, rows.Count).Where(i => rows[i].Kind == RowKind.Waiting).OrderBy(i => rows[i].Name, StringComparer.Ordinal))
        {
            var r = rows[i];
            var wait = ahead > TimeSpan.Zero ? $"Starts in {Roughly(ahead)}, after the meetings ahead of it; notes" : "Notes";
            rows[i] = r with { Expected = $"{wait} expected around {Clock(now + ahead + r.Remaining)} ({Roughly(r.Remaining)} of work)." };
            ahead += r.Remaining;
        }
    }

    Entry? Read(RecordingFolder rec)
    {
        try
        {
            var stamp = new[] { rec.MetaFile, rec.StateFile, rec.SummaryFile, rec.IdentifiedFile }
                .Where(File.Exists).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty().Max();
            if (_cache.TryGetValue(rec.Path, out var cached) && cached.Stamp == stamp) return cached;
            var meta = rec.Read<RecordingMeta>(rec.MetaFile);
            var state = rec.LoadState();
            // The model's sentence; for meetings from before it wrote one, the start of the summary.
            var description = !string.IsNullOrWhiteSpace(meta?.Description) ? meta.Description
                : File.Exists(rec.SummaryFile) ? MeetingAbout.FromSummary(File.ReadAllText(rec.SummaryFile)) : null;
            var people = Speakers.Naming.People(rec.Read<Speakers.IdentifiedResult>(rec.IdentifiedFile));
            var entry = new Entry(stamp, meta, state, description, people);
            _cache[rec.Path] = entry;
            return entry;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null; // mid-write or locked: the next refresh gets it
        }
    }

    static RecordingRow LiveRow(LiveRecording live, StepEstimates estimates)
    {
        var rec = new RecordingFolder(live.Path);
        double seconds = live.Elapsed.TotalSeconds;
        var steps = new List<StepLine> { new("Recording", StepState.Recording, Clock(live.Elapsed), "recording now") };
        var total = TimeSpan.Zero;
        foreach (var step in PipelineSteps.All)
        {
            var estimate = estimates.For(step, seconds);
            total += estimate;
            steps.Add(new StepLine(PipelineSteps.Name(step), StepState.Next, About(estimate)));
        }
        return new RecordingRow(rec.Name, rec.Path, rec.StartedFromName, TitleOf(live.Title, live.Source), seconds, RowKind.Recording,
            $"Recording · {Clock(live.Elapsed)}", $"Recording now ({live.Source}). Notes are made when the call ends.", null, null)
        {
            Steps = steps,
            Remaining = total,
            Expected = $"If the call ended now, its notes would take {Roughly(total)}.",
            Source = live.Source,
            Untitled = MeetingAbout.IsUntitled(live.Title),
            Keep = live.Keep,
            HasAudio = true,
        };
    }

    public static RecordingRow Row(RecordingFolder rec, RecordingMeta? meta, ProcessingState state, Stage? working, DateTimeOffset now,
        StepEstimates? estimates = null)
    {
        estimates ??= StepEstimates.Measured();
        var title = TitleOf(meta?.Title, meta?.Source);
        double seconds = meta?.DurationSeconds ?? 0;
        var started = meta?.StartedAt ?? rec.StartedFromName;
        string? link = state.WebUrl is not null ? $"\n\nTranscript job: {state.WebUrl}" : null;
        string? lastTry = state.LastError is null ? null : $"\n\nLast try ({state.Attempts}) failed at {Doing(state.Stage)}: {state.LastError}";
        var (steps, remaining) = StepLines(meta, state, working, now, estimates);
        var origin = meta?.Imported is { } imported ? $"\n\n{Origin(imported)}" : "";
        bool kept = meta?.KeepRecording == true;
        bool mp3 = state.Stage == Stage.Done && !state.Mp3Deleted && state.NotePath is { } notePath && File.Exists(NoteWriter.AudioPath(notePath));

        RecordingRow Make(RowKind kind, string status, string detail, string? note = null, string? expected = null) =>
            new(rec.Name, rec.Path, started, title, seconds, kind, status, detail + origin, note, state.WebUrl)
            {
                Steps = steps,
                Remaining = remaining,
                Expected = expected,
                Source = meta?.Source ?? "",
                Untitled = meta is null || MeetingAbout.NeedsTitle(meta),
                Keep = kept,
                HasAudio = !state.AudioDeleted,
                HasMp3 = mp3,
                CompletedAt = state.CompletedAt,
            };

        if (working is { } stage)
        {
            int number = PipelineSteps.From(stage) is { } step ? Array.IndexOf(PipelineSteps.All, step) + 1 : PipelineSteps.All.Length;
            return Make(RowKind.Working, $"{Capital(Doing(stage))}… (step {number} of {PipelineSteps.All.Length})",
                $"Working on this meeting now: {Doing(stage)}.{lastTry}{link}");
        }

        if (state.Stage == Stage.Done)
        {
            bool noteThere = state.NotePath is not null && File.Exists(state.NotePath);
            var audio = !state.AudioDeleted ? (kept ? " · recording kept" : "")
                : state.Mp3Deleted ? " · audio and MP3 deleted"
                : kept && mp3 ? " · audio deleted · MP3 kept"
                : " · audio deleted";
            var status = (noteThere ? "Done" : "Done · note moved or deleted") + audio;
            var detail = (state.NotePath is null ? "Done." : $"Note: {state.NotePath}") +
                         (!state.AudioDeleted ? ""
                             : state.Mp3Deleted ? "\n\nThe working audio and the note's MP3 have been deleted. The note and its transcript stay."
                             : mp3 ? "\n\nThe working audio has been deleted (the note keeps its MP3)."
                             : "\n\nThe working audio has been deleted.") + link;
            return Make(RowKind.Done, status, detail, state.NotePath, Took(meta, state));
        }

        if (state.LastError is not null && state.NextAttemptAt is { } next && next > now)
            return Make(RowKind.Retrying, $"Retrying at {next.ToLocalTime():HH:mm} — {Short(state.LastError, 90)}",
                $"Couldn't finish {Doing(state.Stage)} (try {state.Attempts}). It will be tried again at {next.ToLocalTime():HH:mm}, " +
                $"or press Retry now.\n\n{state.LastError}{link}",
                expected: $"Next try at {next.ToLocalTime():HH:mm}; then {Roughly(remaining)} to finish.");

        return Make(RowKind.Waiting, state.LastError is null ? "Waiting" : "Waiting to try again",
            "Queued: notes are made one meeting at a time." + lastTry + link);
    }

    /// <summary>The recording, then each step: done (and how long it took), working, failed, or to come (and roughly how long).</summary>
    static (List<StepLine> Lines, TimeSpan Remaining) StepLines(RecordingMeta? meta, ProcessingState state, Stage? working, DateTimeOffset now,
        StepEstimates estimates)
    {
        double seconds = meta?.DurationSeconds ?? 0;
        var lines = new List<StepLine>();
        if (meta is not null)
        {
            var span = meta.EndedAt > meta.StartedAt ? $"{meta.StartedAt.ToLocalTime():HH:mm}–{meta.EndedAt.ToLocalTime():HH:mm}" : null;
            lines.Add(new StepLine("Recording", StepState.Done, Clock(TimeSpan.FromSeconds(Math.Round(seconds))), span));
        }

        var remaining = TimeSpan.Zero;
        foreach (var step in PipelineSteps.All)
        {
            var name = PipelineSteps.Name(step);
            var time = state.TimeOf(step);
            var estimate = estimates.For(step, seconds);
            var before = PipelineSteps.Before(step);
            if (state.Stage > before)
            {
                var tries = time is { Tries: > 1 } ? $"{time.Tries} tries" : null;
                lines.Add(new StepLine(name, StepState.Done, time?.Took is { } took ? Took(took) : "", tries));
            }
            else if (state.Stage == before && working is not null)
            {
                var elapsed = time is { FinishedAt: null } ? now - time.StartedAt : TimeSpan.Zero;
                if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
                var left = estimate - elapsed;
                remaining += left > TimeSpan.FromSeconds(15) ? left : TimeSpan.FromSeconds(15); // running long: "any moment now"
                lines.Add(new StepLine(name, StepState.Working, Took(elapsed), $"usually {About(estimate)}"));
            }
            else if (state.Stage == before && state.LastError is not null)
            {
                remaining += estimate;
                var retry = state.NextAttemptAt is { } next && next > now ? $"retry at {next.ToLocalTime():HH:mm}" : "will retry";
                lines.Add(new StepLine(name, StepState.Failed, About(estimate), $"failed (try {state.Attempts}) · {retry}"));
            }
            else
            {
                remaining += estimate;
                lines.Add(new StepLine(name, StepState.Next, About(estimate)));
            }
        }
        return (lines, remaining);
    }

    /// <summary>For a finished meeting: how long its notes took after the call ended.</summary>
    static string? Took(RecordingMeta? meta, ProcessingState state)
    {
        var steps = PipelineSteps.All.Select(s => state.TimeOf(s)?.Took).ToList();
        string? work = steps.All(t => t is not null) ? $"{Took(steps.Aggregate(TimeSpan.Zero, (sum, t) => sum + t!.Value))} of processing" : null;
        if (meta is null || state.CompletedAt is not { } done || meta.EndedAt == default || done < meta.EndedAt) return work is null ? null : $"Notes took {work}.";
        var after = $"Notes were ready {Took(done - meta.EndedAt)} after the call ended";
        return work is null ? after + "." : $"{after} ({work}).";
    }

    /// <summary>For an imported recording: the file it came from, and how sure its start time is.</summary>
    static string Origin(ImportedFile file) => $"Imported from {file.Name}" + file.StartedFrom switch
    {
        StartSource.FileName => ". Its start time comes from the file's name.",
        StartSource.Modified => ". The file didn't say when it was recorded, so its start time is when the file was last saved, " +
                                "less its length; that may not be when the meeting was.",
        _ => ".",
    };

    /// <summary>What happens to a meeting at each stage, named by the stage it is leaving.</summary>
    public static string Doing(Stage stage) => stage switch
    {
        Stage.Recorded => "mixing the two tracks",
        Stage.Mixed => "transcribing",
        Stage.Transcribed => "naming speakers",
        Stage.Identified => "summarizing",
        Stage.Summarized => "writing the note",
        _ => "finishing",
    };

    /// <summary>The meeting's title; one the call app left blank is a stand-in until its summary suggests a title.</summary>
    static string TitleOf(string? title, string? source) => string.IsNullOrWhiteSpace(title) ? MeetingAbout.Placeholder(source) : title.Trim();
    static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    static string Short(string s, int max) => s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";
    static string Clock(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm");

    public static string Clock(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    /// <summary>A measured time: "8 s", "3 min 28 s", "1 h 05 min".</summary>
    public static string Took(TimeSpan t) =>
        t.TotalSeconds < 1 ? "under 1 s"
        : t.TotalSeconds < 60 ? $"{t.TotalSeconds:0} s"
        : t.TotalHours < 1 ? $"{(int)t.TotalMinutes} min {t.Seconds:00} s"
        : $"{(int)t.TotalHours} h {t.Minutes:00} min";

    /// <summary>An estimate in a sentence: "a few seconds", "about 40 s", "about 3 min".</summary>
    public static string Roughly(TimeSpan t) => About(t) is var s && s.StartsWith('~') ? "about " + s[1..] : s;

    /// <summary>An estimate, as rough as it is: "a few seconds", "~40 s", "~3 min".</summary>
    public static string About(TimeSpan t) =>
        t.TotalSeconds < 10 ? "a few seconds"
        : t.TotalSeconds < 55 ? $"~{Math.Round(t.TotalSeconds / 5) * 5:0} s"
        : t.TotalHours < 1 ? $"~{Math.Max(1, Math.Round(t.TotalMinutes)):0} min"
        : $"~{(int)t.TotalHours} h {t.Minutes:00} min";
}
