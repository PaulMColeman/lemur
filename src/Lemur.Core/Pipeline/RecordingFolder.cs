using System.Text.Json;
using System.Text.Json.Serialization;
using Lemur.Audio;
using Lemur.Config;

namespace Lemur.Pipeline;

public sealed class RecordingMeta
{
    /// <summary>The call's name from its window, the one its summary suggested if it had none, or what the user renamed it to.</summary>
    public string Title { get; set; } = "";

    /// <summary>
    /// One sentence on what the meeting was about, from the summary model. Null until it has been asked; empty when it
    /// answered without one (the Recordings window then quotes the summary instead).
    /// </summary>
    public string? Description { get; set; }

    /// <summary>The title the summary model suggested, offered when renaming.</summary>
    public string? SuggestedTitle { get; set; }

    /// <summary>The user chose <see cref="Title"/> (Rename…, llmmr rename): a suggested title never replaces it, even "Meeting".</summary>
    public bool TitleByUser { get; set; }

    /// <summary>
    /// <see cref="Title"/> is an imported file's name ("Budget review.m4a"), a stand-in until its summary suggests a title:
    /// a phone names its recordings "New Recording 12" or after the street it was made in.
    /// </summary>
    public bool TitleFromFile { get; set; }

    /// <summary>The file it was made from, for a recording imported from a phone or another recorder; null for calls.</summary>
    public ImportedFile? Imported { get; set; }

    /// <summary>
    /// The user ticked "Keep recording" (the Recordings window, llmmr keep): the clean-up after settings.keepAudioDays leaves
    /// this meeting's audio, and its note's MP3, alone (<see cref="AudioCleanUp"/>).
    /// </summary>
    public bool KeepRecording { get; set; }

    public string Source { get; set; } = "";
    public bool Manual { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
    public double DurationSeconds { get; set; }
    public string? MicDevice { get; set; }
    public string? OutputDevice { get; set; }
    public double MicPaddedSeconds { get; set; }
    public double OthersPaddedSeconds { get; set; }
    public int DeviceRestarts { get; set; }
}

public enum Stage
{
    /// <summary>Audio on disk, nothing done yet.</summary>
    Recorded,
    Mixed,
    Transcribed,
    /// <summary>Speakers named by voice and introductions (or skipped); identified.json written.</summary>
    Identified,
    Summarized,
    Done,
}

public sealed class ProcessingState
{
    public Stage Stage { get; set; } = Stage.Recorded;
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? Backend { get; set; }
    public string? JobId { get; set; }
    public string? WebUrl { get; set; }
    public string? NotePath { get; set; }

    /// <summary>SHA-256 of the .md as written; if the file differs, the user has edited it (design 3.8).</summary>
    public string? NoteHash { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public bool AudioDeleted { get; set; }

    /// <summary>The clean-up has dealt with the note's MP3 (settings.noteMp3.deleteWithAudio): deleted it, or found it gone.</summary>
    public bool Mp3Deleted { get; set; }

    /// <summary>The layout its note page was made with (<see cref="NoteHtml.PageVersion"/>); 0 for pages from before there was one.</summary>
    public int PageVersion { get; set; }

    /// <summary>When each step last ran, for the Recordings window and its estimates.</summary>
    public List<StepTime> Steps { get; set; } = [];

    /// <summary>
    /// The meeting file's action items by number (<see cref="MeetingFile"/>): each item's text, so it keeps its number when
    /// the summary is read again after a speaker is named or a word fixed. A new item takes <see cref="NextActionItem"/>;
    /// numbers are never reused, so nothing is taken out. A table that can't be read is empty, not the state unreadable.
    /// </summary>
    [JsonConverter(typeof(ActionItemNumbersConverter))]
    public List<ActionItemNumber> ActionItemNumbers { get; set; } = [];

    public int NextActionItem { get; set; } = 1;

    /// <summary>What the meeting file beside the note was made from (<see cref="MeetingFile.Stamp"/>); null until it is written.</summary>
    public string? MeetingFileStamp { get; set; }

    /// <summary>Writing the meeting file failed: the queue's pass tries it again after this.</summary>
    public DateTimeOffset? MeetingFileRetryAt { get; set; }

    public StepTime? TimeOf(Step step) => Steps.FirstOrDefault(t => t.Step == step);

    public void StepStarted(Step step, DateTimeOffset at)
    {
        var time = TimeOf(step);
        if (time is null) Steps.Add(time = new StepTime { Step = step });
        time.Tries = time.FinishedAt is null ? time.Tries + 1 : 1; // finished before = done again (llmmr process --from)
        time.StartedAt = at;
        time.FinishedAt = null;
    }

    public void StepFinished(Step step, DateTimeOffset at)
    {
        if (TimeOf(step) is { } time) time.FinishedAt = at;
    }
}

/// <summary>An action item's number in its meeting (<see cref="ProcessingState.ActionItemNumbers"/>).</summary>
public sealed class ActionItemNumber
{
    /// <summary>The item's text as the summary had it when it was numbered, in lower case with its spaces made one: names and
    /// "Speaker B" kept, since <see cref="MeetingFile"/> takes out the names the meeting has when it compares.</summary>
    public string Text { get; set; } = "";

    public int Number { get; set; }
}

/// <summary>
/// The table of action item numbers, or an empty one when it can't be read (a shape from before, an edit by hand): its
/// items are numbered again from <see cref="ProcessingState.NextActionItem"/>, and the rest of the state is still read.
/// </summary>
sealed class ActionItemNumbersConverter : JsonConverter<List<ActionItemNumber>>
{
    public override bool HandleNull => true;

    public override List<ActionItemNumber> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var value = JsonDocument.ParseValue(ref reader);
        try
        {
            return [.. (value.RootElement.Deserialize<List<ActionItemNumber?>>(options) ?? []).OfType<ActionItemNumber>().Where(n => !string.IsNullOrEmpty(n.Text))];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public override void Write(Utf8JsonWriter writer, List<ActionItemNumber> value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);
}

/// <summary>
/// One meeting on disk. Every stage writes its result here, so processing resumes where it stopped
/// after a crash, a reboot or an unreachable server — and never uploads the same meeting twice.
/// </summary>
public sealed class RecordingFolder
{
    public RecordingFolder(string path) => Path = path;

    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string MicWav => File(DualTrackRecorder.MicFile);
    public string OthersWav => File(DualTrackRecorder.OthersFile);
    public string MixWav => File("mix.wav");
    public string MetaFile => File("meta.json");
    public string StateFile => File("state.json");
    public string EnvelopesFile => File("envelopes.json");
    public string TranscriptFile => File("transcript.json");
    public string LabelledFile => File("labelled.json");
    public string SummaryFile => File("summary.md");
    public string IdentifiedFile => File("identified.json");

    /// <summary>Per-speaker voiceprints of this meeting: biometric, so deleted with the audio.</summary>
    public string VoiceprintsFile => File("voiceprints.json");

    string File(string name) => System.IO.Path.Combine(Path, name);

    public static RecordingFolder CreateNew(string root, DateTimeOffset startedAt)
    {
        var name = startedAt.ToLocalTime().ToString("yyyyMMdd-HHmmss");
        var path = System.IO.Path.Combine(root, name);
        for (int i = 2; Directory.Exists(path); i++) path = System.IO.Path.Combine(root, $"{name}-{i}");
        Directory.CreateDirectory(path);
        return new RecordingFolder(path);
    }

    /// <summary>
    /// A recording by its folder name ("20261014-093000"), as a link from a meeting's page names it. Only
    /// that exact shape is accepted, so a crafted link can't point outside the recordings folder.
    /// </summary>
    public static RecordingFolder? ByName(string root, string name)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^\d{8}-\d{6}(-\d{1,3})?$")) return null;
        var rec = new RecordingFolder(System.IO.Path.Combine(root, name));
        return System.IO.File.Exists(rec.MetaFile) ? rec : null;
    }

    public static IEnumerable<RecordingFolder> All(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateDirectories(root).Order().Select(p => new RecordingFolder(p)).Where(r => System.IO.File.Exists(r.MetaFile))
            : [];

    /// <summary>
    /// Recordings the app stopped making without finishing them (crash, killed, power cut): audio but no meta.json,
    /// so nothing processes or cleans them up. Folders written to in the last two minutes are left out — that is a
    /// recording still being made, or being stopped right now.
    /// </summary>
    public static IEnumerable<RecordingFolder> Interrupted(string root, string? exceptPath = null)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var path in Directory.EnumerateDirectories(root).Order())
        {
            var rec = new RecordingFolder(path);
            if (System.IO.File.Exists(rec.MetaFile) || !System.IO.File.Exists(rec.MicWav)) continue;
            if (exceptPath is not null && string.Equals(System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(exceptPath), StringComparison.OrdinalIgnoreCase)) continue;
            var lastWrite = new[] { rec.MicWav, rec.OthersWav }.Where(System.IO.File.Exists).Max(System.IO.File.GetLastWriteTimeUtc);
            if (DateTime.UtcNow - lastWrite < TimeSpan.FromMinutes(2)) continue;
            yield return rec;
        }
    }

    /// <summary>When an interrupted recording began, from its folder's name ("20261014-093000").</summary>
    public DateTimeOffset? StartedFromName =>
        DateTime.TryParseExact(Name.Length >= 15 ? Name[..15] : Name, "yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeLocal, out var t) ? new DateTimeOffset(t) : null;

    /// <summary>
    /// Makes an interrupted recording an ordinary one waiting for notes: fixes its WAV headers, then writes the
    /// meta.json and state.json that stopping it would have written. Its title is unknown, so the note gets a dated one.
    /// </summary>
    public RecordingMeta Recover()
    {
        if (!System.IO.File.Exists(MicWav) || !System.IO.File.Exists(OthersWav))
            throw new InvalidDataException("this recording is missing one of its two tracks");
        WavRepair.FixSizes(MicWav);
        WavRepair.FixSizes(OthersWav);
        double seconds;
        using (var mic = new NAudio.Wave.WaveFileReader(MicWav))
        using (var others = new NAudio.Wave.WaveFileReader(OthersWav))
            seconds = Math.Max(mic.TotalTime.TotalSeconds, others.TotalTime.TotalSeconds);
        var started = StartedFromName ?? new DateTimeOffset(Directory.GetCreationTime(Path));
        var meta = new RecordingMeta
        {
            Title = "",
            Source = "Recovered",
            StartedAt = started,
            EndedAt = started.AddSeconds(seconds),
            DurationSeconds = Math.Round(seconds, 1),
        };
        SaveMeta(meta);
        SaveState(new ProcessingState());
        return meta;
    }

    /// <summary>Removes the whole folder: audio, transcript and state. The note, if one was written, stays.</summary>
    public void Delete() => Directory.Delete(Path, recursive: true);

    public RecordingMeta LoadMeta() => Read<RecordingMeta>(MetaFile) ?? throw new InvalidDataException($"{MetaFile} missing");
    public void SaveMeta(RecordingMeta meta) => Write(MetaFile, meta);
    public ProcessingState LoadState() => Read<ProcessingState>(StateFile) ?? new ProcessingState();
    public void SaveState(ProcessingState state) => Write(StateFile, state);

    /// <summary>
    /// Held while a meeting's meta.json, state.json or note change outside its own processing step: renaming (the
    /// Recordings window, llmmr rename), titles and descriptions (the queue), naming speakers (the tray, a note page's
    /// naming window, llmmr), clean-up. A named mutex, so it holds across those processes as well as threads; the same
    /// thread may take it again inside.
    /// </summary>
    /// <exception cref="IOException">Another copy held it for too long.</exception>
    public static IDisposable Lock() => new NotesLock();

    sealed class NotesLock : IDisposable
    {
        readonly Mutex _mutex = new(false, @"Local\Lemur.Notes" + AppPaths.InstanceSuffix);

        public NotesLock()
        {
            try
            {
                if (_mutex.WaitOne(TimeSpan.FromSeconds(15))) return;
            }
            catch (AbandonedMutexException)
            {
                return; // a copy of Lemur died holding it: it is this one's now
            }
            _mutex.Dispose();
            throw new IOException("Another Lemur window is changing this meeting's notes. Try again in a moment.");
        }

        public void Dispose()
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }

    /// <summary>Reads, changes and saves meta.json under <see cref="Lock"/>.</summary>
    public RecordingMeta UpdateMeta(Action<RecordingMeta> change)
    {
        using (Lock())
        {
            var meta = LoadMeta();
            change(meta);
            SaveMeta(meta);
            return meta;
        }
    }

    /// <summary>Reads, changes and saves state.json under <see cref="Lock"/>.</summary>
    public ProcessingState UpdateState(Action<ProcessingState> change)
    {
        using (Lock())
        {
            var state = LoadState();
            change(state);
            SaveState(state);
            return state;
        }
    }

    /// <summary>Shares the file for delete too, so a save (a new file moved over this one) isn't refused because of a reader.</summary>
    public T? Read<T>(string path) where T : class
    {
        if (!System.IO.File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<T>(stream, Json.Options);
    }

    /// <summary>Written beside, then moved over the old file; a reader holding it for a moment (the Recordings window,
    /// another copy) is waited out briefly rather than failing the save.</summary>
    public void Write<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json.Options));
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                System.IO.File.Move(tmp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 10)
            {
                Thread.Sleep(30);
            }
        }
    }

    /// <summary>
    /// Removes the raw audio and this meeting's voiceprints, keeping the small text files (state, transcript,
    /// identified.json, note path). After this, speakers can still be named but their voices not remembered.
    /// </summary>
    public void DeleteAudio()
    {
        foreach (var f in new[] { MicWav, OthersWav, MixWav, VoiceprintsFile })
            if (System.IO.File.Exists(f)) System.IO.File.Delete(f);
    }
}
