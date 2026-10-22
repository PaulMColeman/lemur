using Lemur.Audio;
using Lemur.Backends;
using Lemur.Config;
using Lemur.Logging;
using Lemur.Speakers;
using Lemur.Transcripts;

namespace Lemur.Pipeline;

/// <summary>
/// Recorded → Mixed → Transcribed → Identified → Summarized → Done, saving after every stage.
/// A failure records the error and a retry time; the next run picks up at the failed stage.
/// </summary>
public sealed class MeetingProcessor
{
    readonly AppSettings _settings;
    readonly Func<IMeetingBackend> _backendFactory;
    readonly SpeakerIdentifier? _identifier;
    readonly Func<IChatModel?>? _chat;

    /// <param name="identifier">Names other speakers (design 3.1); null skips the Identified stage's work.</param>
    /// <param name="chat">The summary model, asked for a title and description when the summary came back without them.</param>
    public MeetingProcessor(AppSettings settings, Func<IMeetingBackend> backendFactory, SpeakerIdentifier? identifier = null,
        Func<IChatModel?>? chat = null)
    {
        _settings = settings;
        _backendFactory = backendFactory;
        _identifier = identifier;
        _chat = chat;
    }

    /// <summary>Raised as work on each stage begins, with the stage being left (Mixed = transcribing now).</summary>
    public event Action<RecordingFolder, Stage>? Working;

    /// <summary>The labelled transcript with any names from identified.json applied.</summary>
    List<LabelledSegment> NamedSegments(RecordingFolder rec)
    {
        var labelled = rec.Read<List<LabelledSegment>>(rec.LabelledFile) ?? throw new InvalidDataException("labelled.json missing");
        return [.. Naming.Apply(labelled, rec.Read<IdentifiedResult>(rec.IdentifiedFile), _settings.DisplayName)];
    }

    public async Task<ProcessingState> ProcessAsync(RecordingFolder rec, CancellationToken ct)
    {
        var state = rec.LoadState();
        if (state.Stage == Stage.Done) return state;
        var meta = rec.LoadMeta();
        var title = string.IsNullOrWhiteSpace(meta.Title) ? $"Meeting {meta.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm}" : meta.Title;

        IMeetingBackend? backend = null;
        try
        {
            if (state.Stage == Stage.Recorded)
            {
                Begin(rec, state);
                var mix = Mixer.MixTracks(rec.MicWav, rec.OthersWav, rec.MixWav);
                rec.Write(rec.EnvelopesFile, new Envelopes(mix.Mic, mix.Others));
                Log.Info($"{rec.Name}: mixed {mix.DurationSeconds:0}s (mic {mix.MicGainDb:+0.0;-0.0} dB, others {mix.OthersGainDb:+0.0;-0.0} dB)");
                Advance(rec, state, Stage.Mixed);
            }

            if (state.Stage == Stage.Mixed)
            {
                Begin(rec, state);
                backend ??= _backendFactory();
                var started = DateTimeOffset.Now;
                var duration = TimeSpan.FromSeconds(meta.DurationSeconds);
                TranscriptResult transcript;
                if (backend is IResumableTranscription resumable)
                {
                    // Saved as soon as the server has the audio: a retry resumes this job rather than uploading again.
                    var earlier = state.Backend == backend.Kind ? state.JobId : null;
                    if (earlier is not null) Log.Info($"{rec.Name}: checking on {backend.Kind} job {earlier} from the last try");
                    transcript = await resumable.TranscribeAsync(rec.MixWav, title, duration, earlier, job =>
                    {
                        state.Backend = backend.Kind;
                        state.JobId = job.Id;
                        state.WebUrl = job.WebUrl;
                        rec.SaveState(state);
                    }, ct);
                }
                else
                {
                    transcript = await backend.TranscribeAsync(rec.MixWav, title, duration, ct);
                }
                rec.Write(rec.TranscriptFile, transcript);
                state.Backend = backend.Kind;
                state.JobId = transcript.JobId;
                state.WebUrl = transcript.WebUrl;
                Log.Info($"{rec.Name}: transcribed by {backend.Kind} in {(DateTimeOffset.Now - started).TotalSeconds:0}s, {transcript.Segments.Count} segments");
                Advance(rec, state, Stage.Transcribed);
            }

            if (state.Stage == Stage.Transcribed)
            {
                Begin(rec, state);
                backend ??= _backendFactory();
                var transcript = rec.Read<TranscriptResult>(rec.TranscriptFile) ?? throw new InvalidDataException("transcript.json missing");
                var env = rec.Read<Envelopes>(rec.EnvelopesFile) ?? throw new InvalidDataException("envelopes.json missing");
                var labels = SpeakerLabeler.Label(transcript.Segments, env.Mic, env.Others, _settings.DisplayName);
                // Misheard words fixed before anything reads them: introductions, the summary, the note, search.
                labels = labels with { Segments = new Vocabulary(_settings.Vocabulary).Fix(labels.Segments, out var fixes) };
                if (fixes > 0) Log.Info($"{rec.Name}: vocabulary fixed {fixes} {(fixes == 1 ? "word" : "words")}");
                rec.Write(rec.LabelledFile, labels.Segments);
                Log.Info($"{rec.Name}: labelled — me={labels.Segments.Count(s => s.IsMe)}/{labels.Segments.Count} segments, " +
                         $"threshold {(!double.IsNaN(labels.ThresholdDb) ? $"{labels.ThresholdDb:0.0} dB" : env.Others.Percentile(95) > Mixer.SilentTrackDb ? "n/a (your mic was silent)" : "n/a (no call audio)")}, " +
                         $"{transcript.Segments.Count(s => s.Words is not null)} of {transcript.Segments.Count} with word times, " +
                         $"{labels.SplitLines} split between you and someone else");

                IdentifiedResult? identified = null;
                if (_identifier is not null)
                {
                    identified = await _identifier.IdentifyAsync(rec, meta, labels, env, ct);
                    rec.Write(rec.IdentifiedFile, identified);
                    var named = identified.Speakers.Count(x => x.Status is SpeakerStatus.Named or SpeakerStatus.Introduced or SpeakerStatus.Maybe);
                    Log.Info($"{rec.Name}: identified — {named} of {identified.Speakers.Count(x => !x.IsMe)} named; " +
                             $"voices {identified.Voices}; introductions {identified.Introductions}");
                }
                else if (File.Exists(rec.IdentifiedFile))
                {
                    File.Delete(rec.IdentifiedFile); // from an earlier run whose labels may no longer line up
                }

                var names = Naming.ForBackend(identified, _settings.DisplayName, labels.MeEngineSpeaker);
                if (names.Count > 0)
                {
                    try
                    {
                        await backend.NameSpeakersAsync(transcript, names, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Log.Warn($"{rec.Name}: could not rename speakers in backend (note is unaffected): {ex.Message}");
                    }
                }
                Advance(rec, state, Stage.Identified);
            }

            if (state.Stage == Stage.Identified)
            {
                Begin(rec, state);
                backend ??= _backendFactory();
                var transcript = rec.Read<TranscriptResult>(rec.TranscriptFile) ?? throw new InvalidDataException("transcript.json missing");
                var vocabulary = new Vocabulary(_settings.Vocabulary);
                FixWords(rec, vocabulary); // words added since it was transcribed
                var said = TranscriptFormat.ToPlainText(NamedSegments(rec));
                var started = DateTimeOffset.Now;
                var reply = await backend.SummarizeAsync(transcript,
                    SummaryPrompt.For(_settings.DisplayName, _settings.Summary.PromptFile, meta.StartedAt, vocabulary.Glossary(said)),
                    said, ct);
                var (summary, suggested, description) = MeetingAbout.Split(reply);
                summary = vocabulary.Fix(summary);
                suggested = suggested is null ? null : vocabulary.Fix(suggested);
                description = description is null ? null : vocabulary.Fix(description);
                // A reply cut off after its first lines (Scriberr's 5-minute cap, summary.maxTokens) is no summary.
                if (string.IsNullOrWhiteSpace(summary))
                    throw new BackendException("summary: only the title came back, no summary (cut off?); it will be retried");
                File.WriteAllText(rec.SummaryFile, summary);
                Log.Info($"{rec.Name}: summarized in {(DateTimeOffset.Now - started).TotalSeconds:0}s");
                await DescribeAsync(rec, summary, suggested, description, ct);
                Advance(rec, state, Stage.Summarized);
            }

            if (state.Stage == Stage.Summarized)
            {
                Begin(rec, state);
                FixWords(rec, new Vocabulary(_settings.Vocabulary)); // words added since it was summarized
                meta = rec.LoadMeta(); // titled from the summary, or renamed in the Recordings window, since it began
                var summary = File.ReadAllText(rec.SummaryFile);
                var identified = rec.Read<IdentifiedResult>(rec.IdentifiedFile);
                state.NotePath = NoteWriter.Write(_settings.NotesFolder, meta, summary, NamedSegments(rec), state.Backend ?? "?",
                    state.WebUrl, Naming.People(identified), rec.Name, rec.MixWav);
                state.NoteHash = NoteWriter.Hash(state.NotePath);
                state.PageVersion = NoteHtml.PageVersion;
                state.CompletedAt = DateTimeOffset.Now;
                state.LastError = null;
                state.NextAttemptAt = null;
                Log.Info($"{rec.Name}: note written");
                Advance(rec, state, Stage.Done);

                // Renamed in the Recordings window while the note was being written: the note takes that title too.
                if (rec.LoadMeta().Title is var now && now != meta.Title && !string.IsNullOrWhiteSpace(now))
                {
                    try
                    {
                        MeetingRename.Apply(rec, now, byUser: false); // whoever chose it, it stays theirs
                        state = rec.LoadState();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Log.Warn($"{rec.Name}: the note keeps the title it was written with; renaming it failed ({ex.GetType().Name})");
                    }
                }

                // The meeting file beside the note; if it can't be written, the note still counts and the queue tries again.
                if (MeetingFile.TryWrite(rec, _settings.DisplayName, new Vocabulary(_settings.Vocabulary),
                        transcript: _settings.MeetingFile.IncludeTranscript)) state = rec.LoadState();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            state.Attempts++;
            state.LastError = ex is BackendException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            state.NextAttemptAt = DateTimeOffset.Now + RetryDelay(state.Attempts);
            rec.SaveState(state);
            Log.Error($"{rec.Name}: failed at {state.Stage} (attempt {state.Attempts}, retry {state.NextAttemptAt:HH:mm}): {state.LastError}");
        }
        finally
        {
            backend?.Dispose();
        }
        return state;
    }

    /// <summary>
    /// Keeps what the summary says the meeting was about: its one-sentence description, and its title, which becomes the
    /// meeting's title when the call app gave none (Meet, GoTo, Zoom, a manual recording). If the summary came without
    /// them, the summary model is asked from the summary alone. Never fails the stage: without them the note is written
    /// as before, and the queue asks again later.
    /// </summary>
    async Task DescribeAsync(RecordingFolder rec, string summary, string? title, string? description, CancellationToken ct)
    {
        bool untitled = MeetingAbout.NeedsTitle(rec.LoadMeta());
        if ((description is null || (title is null && untitled)) && _chat is not null && MeetingAbout.HasSubstance(summary))
        {
            using var patience = CancellationTokenSource.CreateLinkedTokenSource(ct);
            patience.CancelAfter(MeetingDescriber.Patience);
            try
            {
                using var chat = _chat();
                if (chat is not null)
                {
                    var asked = await MeetingDescriber.AskAsync(chat, summary, patience.Token);
                    title ??= asked.Title;
                    description ??= asked.Description;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log.Warn($"{rec.Name}: no title or description from the summary model this time: {ex.Message}");
            }
        }
        if (title is null && description is null) return;
        try
        {
            var meta = rec.UpdateMeta(m =>
            {
                if (description is not null) m.Description = description;
                if (title is null) return;
                m.SuggestedTitle = title;
                if (!MeetingAbout.NeedsTitle(m)) return; // not if it was renamed meanwhile
                m.Title = title;
                m.TitleFromFile = false;
            });
            Log.Info($"{rec.Name}: {(description is null ? "no description" : "described")}" +
                     (title is not null && meta.Title == title ? "; titled from its summary" : ""));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Log.Warn($"{rec.Name}: couldn't save its title and description: {ex.Message}"); // the queue describes it later
        }
    }

    /// <summary>
    /// The vocabulary again, for a meeting that was transcribed (or summarized) before a word was added to it: its
    /// labelled.json and summary.md are fixed, so the summary and note have the word right, and so does a note written
    /// again later (naming speakers rewrites it from these files).
    /// </summary>
    static void FixWords(RecordingFolder rec, Vocabulary vocabulary)
    {
        if (!vocabulary.CanFix) return;
        if (rec.Read<List<LabelledSegment>>(rec.LabelledFile) is { } labelled)
        {
            var fixedSegments = vocabulary.Fix(labelled, out var n);
            if (n > 0) rec.Write(rec.LabelledFile, fixedSegments);
        }
        if (File.Exists(rec.SummaryFile))
        {
            var summary = vocabulary.Fix(File.ReadAllText(rec.SummaryFile), out var n);
            if (n > 0) File.WriteAllText(rec.SummaryFile, summary);
        }
        var meta = rec.LoadMeta();
        bool mentioned = vocabulary.Mentions(meta.Description) || vocabulary.Mentions(meta.SuggestedTitle) ||
                         (!meta.TitleByUser && vocabulary.Mentions(meta.Title));
        if (!mentioned) return;
        rec.UpdateMeta(m =>
        {
            if (m.Description is not null) m.Description = vocabulary.Fix(m.Description);
            if (m.SuggestedTitle is not null) m.SuggestedTitle = vocabulary.Fix(m.SuggestedTitle);
            if (!m.TitleByUser) m.Title = vocabulary.Fix(m.Title); // the note isn't written yet: no files to rename
        });
    }

    public static TimeSpan RetryDelay(int attempts) => attempts switch
    {
        <= 1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(5),
        3 => TimeSpan.FromMinutes(15),
        _ => TimeSpan.FromHours(1),
    };

    /// <summary>Starts the step that moves the meeting on from its stage: saves when it began, then tells the queue.</summary>
    void Begin(RecordingFolder rec, ProcessingState state)
    {
        if (PipelineSteps.From(state.Stage) is { } step)
        {
            state.StepStarted(step, DateTimeOffset.Now);
            rec.SaveState(state);
        }
        Working?.Invoke(rec, state.Stage);
    }

    static void Advance(RecordingFolder rec, ProcessingState state, Stage next)
    {
        if (PipelineSteps.From(state.Stage) is { } step) state.StepFinished(step, DateTimeOffset.Now);
        state.Stage = next;
        rec.SaveState(state);
    }

    public sealed record Envelopes(Envelope Mic, Envelope Others);
}
