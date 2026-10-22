using System.Collections.Concurrent;
using Lemur.Config;
using Lemur.Logging;
using Lemur.Speakers;
using Lemur.Transcripts;

namespace Lemur.Pipeline;

/// <summary>
/// Works through unfinished recordings one at a time (the GPU is shared; there is no hurry),
/// retrying failures on their schedule, and deletes old audio once meetings are done (<see cref="AudioCleanUp"/>). In between, it gives
/// finished meetings without a description one, and a title if they had none (<see cref="DescribeOneAsync"/>), and
/// brings their note pages and meeting files up to date.
/// </summary>
public sealed class ProcessingQueue
{
    readonly Func<AppSettings> _settings;
    readonly Func<MeetingProcessor> _processor;
    readonly Func<IChatModel?>? _chat;
    readonly SemaphoreSlim _wake = new(0);
    readonly ConcurrentDictionary<string, bool> _retryNow = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (int Tries, DateTimeOffset Next)> _describeTries = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _unreadable = new(StringComparer.OrdinalIgnoreCase); // recordings whose state.json couldn't be read, said once
    readonly Func<DateTimeOffset> _clock;
    DateTimeOffset _describePausedUntil;
    bool _namesCaughtUp;

    /// <param name="chat">The summary model, for describing meetings that have no description; null leaves them be.</param>
    /// <param name="clock">Now, for <see cref="DescribeOneAsync"/>'s waits (tests).</param>
    public ProcessingQueue(Func<AppSettings> settings, Func<MeetingProcessor> processor, Func<IChatModel?>? chat = null,
        Func<DateTimeOffset>? clock = null)
    {
        _settings = settings;
        _processor = processor;
        _chat = chat;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>Raised on the worker thread just before an attempt at a meeting, with its state so far.</summary>
    public event Action<RecordingFolder, ProcessingState>? Starting;

    /// <summary>Raised on the worker thread when a meeting finishes or fails an attempt.</summary>
    public event Action<RecordingFolder, ProcessingState>? Processed;

    /// <summary>Raised when the worker starts/stops working on something.</summary>
    public event Action<bool>? BusyChanged;

    /// <summary>Raised on the worker thread as each stage of a meeting begins.</summary>
    public event Action<RecordingFolder, Stage>? StageStarted;

    public int Waiting { get; private set; }

    /// <summary>The meeting being worked on and the stage it is leaving; null while idle.</summary>
    public (string Recording, Stage Stage)? Current { get; private set; }

    public void Kick() => _wake.Release();

    /// <summary>Try this meeting at the next pass, whatever its retry schedule says (the Recordings window's "Retry now").</summary>
    public void RetryNow(string recordingName)
    {
        _retryNow[recordingName] = true;
        Kick();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(ct);
                AudioCleanUp.Run(_settings()); // each meeting logged
                await DescribeOneAsync(_settings(), ct);
                if (!_namesCaughtUp)
                {
                    _namesCaughtUp = true; // once a start: naming puts names into descriptions itself now
                    new SpeakerNamer(_settings()).CatchUp(_settings().ResolvedRecordingsFolder);
                }
                var pages = NotePages.RefreshAll(_settings().ResolvedRecordingsFolder, _settings().DisplayName, max: 20);
                if (pages > 0) Log.Info($"{pages} note {(pages == 1 ? "page" : "pages")} brought up to date");
                // 20 a pass, but every one at once while the transcript is left out: a file that still has it shouldn't wait.
                bool transcript = _settings().MeetingFile.IncludeTranscript;
                MeetingFile.RefreshAll(_settings().ResolvedRecordingsFolder, _settings().DisplayName, new Vocabulary(_settings().Vocabulary),
                    max: transcript ? 20 : int.MaxValue, transcript: transcript); // each one logged
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error("processing queue pass failed", ex);
            }
            try { await _wake.WaitAsync(TimeSpan.FromMinutes(1), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var settings = _settings();
        var pending = new List<(RecordingFolder Rec, ProcessingState State)>();
        foreach (var rec in RecordingFolder.All(settings.ResolvedRecordingsFolder))
        {
            try
            {
                var state = rec.LoadState();
                _unreadable.Remove(rec.Name);
                if (state.Stage != Stage.Done) pending.Add((rec, state));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                // A state.json that can't be read (a hand edit, a power cut) used to fail the whole pass: no meeting got
                // notes, and nothing was cleaned up. It is left alone until it can be read, and said once.
                if (_unreadable.Add(rec.Name)) Log.Warn($"{rec.Name}: its state.json can't be read ({ex.GetType().Name}); it is left alone until it can");
            }
        }
        Waiting = pending.Count;
        foreach (var (rec, state) in pending)
        {
            ct.ThrowIfCancellationRequested();
            bool asked = _retryNow.TryRemove(rec.Name, out _);
            if (!asked && state.NextAttemptAt is { } next && next > DateTimeOffset.Now) continue;
            BusyChanged?.Invoke(true);
            try
            {
                Starting?.Invoke(rec, state);
                var processor = _processor();
                processor.Working += (r, stage) =>
                {
                    Current = (r.Name, stage);
                    StageStarted?.Invoke(r, stage);
                };
                var result = await processor.ProcessAsync(rec, ct);
                if (result.Stage == Stage.Done) Waiting--;
                Processed?.Invoke(rec, result);
            }
            finally
            {
                Current = null;
                BusyChanged?.Invoke(false);
            }
        }
    }

    /// <summary>
    /// Finished meetings without a description (summarized before Lemur wrote them, or whose summary came back without
    /// one): the newest gets a title and description from the summary model, from its summary. One that has no title is
    /// renamed too — unless the user titled it or edited its note, which a background job never overrides. One per pass,
    /// so meetings waiting for notes come first; meetings finished in the last half hour are left alone (their notes may
    /// be open, and the toast points at them). After a failure the whole job pauses for half an hour, and a meeting is
    /// tried at most three times while the app runs.
    /// </summary>
    public async Task DescribeOneAsync(AppSettings settings, CancellationToken ct)
    {
        var now = _clock();
        if (_chat is null || now < _describePausedUntil) return;
        foreach (var rec in RecordingFolder.All(settings.ResolvedRecordingsFolder).Reverse())
        {
            if (!File.Exists(rec.SummaryFile)) continue;
            if (_describeTries.TryGetValue(rec.Name, out var tried) && (tried.Tries >= 3 || tried.Next > now)) continue;
            RecordingMeta meta;
            string summary;
            try
            {
                var state = rec.LoadState();
                if (state.Stage != Stage.Done || state.CompletedAt is { } done && now - done < TimeSpan.FromMinutes(30)) continue;
                meta = rec.LoadMeta();
                if (meta.Description is not null) continue;
                summary = await File.ReadAllTextAsync(rec.SummaryFile, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { continue; }
            if (!MeetingAbout.HasSubstance(summary))
            {
                try { rec.UpdateMeta(m => m.Description ??= ""); } // nothing to describe it from
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
                continue;
            }

            try
            {
                using var chat = _chat();
                if (chat is null) return; // the summary model isn't set up
                using var patience = CancellationTokenSource.CreateLinkedTokenSource(ct);
                patience.CancelAfter(MeetingDescriber.Patience);
                var (title, description) = await MeetingDescriber.AskAsync(chat, summary, patience.Token);
                if (title is null && description is null)
                    throw new InvalidDataException("the summary model's answer had neither a title nor a description");

                // The title first: if renaming fails, nothing is kept and the next try asks again.
                bool renamed = false;
                using (RecordingFolder.Lock())
                {
                    var latest = rec.LoadMeta();
                    var state = rec.LoadState();
                    bool untouched = state.NotePath is { } note && File.Exists(note) && state.NoteHash is not null &&
                                     NoteWriter.Hash(note) == state.NoteHash;
                    if (title is not null && MeetingAbout.NeedsTitle(latest) && untouched)
                    {
                        MeetingRename.Apply(rec, title, byUser: false);
                        renamed = true;
                    }
                    rec.UpdateMeta(m =>
                    {
                        m.Description = description ?? ""; // asked: don't ask again
                        if (title is not null) m.SuggestedTitle = title;
                    });
                }
                Log.Info($"{rec.Name}: described{(renamed ? "; it had no title, so it is named from its summary" : "")}");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                int tries = (_describeTries.TryGetValue(rec.Name, out var t) ? t.Tries : 0) + 1;
                _describeTries[rec.Name] = (tries, now.AddMinutes(30));
                _describePausedUntil = now.AddMinutes(30);
                Log.Warn($"{rec.Name}: couldn't describe it (try {tries}; trying again in half an hour): {ex.GetType().Name}: {ex.Message}");
            }
            return;
        }
    }
}
