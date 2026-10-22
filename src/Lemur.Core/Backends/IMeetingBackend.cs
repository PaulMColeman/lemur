using Lemur.Transcripts;

namespace Lemur.Backends;

/// <summary>
/// Where audio becomes text and text becomes a summary. The recorder owns everything else
/// (capture, naming "me", the prompt, the note), so swapping or moving a backend is a settings change.
/// </summary>
public interface IMeetingBackend : IDisposable
{
    string Kind { get; }

    /// <summary>Transcribe with speaker separation (diarization).</summary>
    Task<TranscriptResult> TranscribeAsync(string audioPath, string title, TimeSpan duration, CancellationToken ct);

    /// <summary>Optionally show friendly speaker names in the backend's own UI. Best effort.</summary>
    Task NameSpeakersAsync(TranscriptResult transcript, IReadOnlyDictionary<string, string> engineToName, CancellationToken ct);

    /// <summary>Returns Markdown.</summary>
    Task<string> SummarizeAsync(TranscriptResult transcript, string instructions, string labelledTranscript, CancellationToken ct);
}

/// <summary>A transcription job the server holds, by its id there and the page that shows it (if any).</summary>
public sealed record TranscriptionJob(string Id, string? WebUrl);

/// <summary>
/// A backend whose transcription runs as a job on the server. A retry picks the same job up again — still
/// running, finished meanwhile, or failed and restarted — instead of uploading the meeting a second time.
/// </summary>
public interface IResumableTranscription
{
    /// <param name="job">A job this backend started earlier for this audio, or null to start one.</param>
    /// <param name="started">Called once the server has the audio, so the job can be saved before the long wait.</param>
    Task<TranscriptResult> TranscribeAsync(string audioPath, string title, TimeSpan duration, string? job,
        Action<TranscriptionJob>? started, CancellationToken ct);
}

/// <summary>A backend said no. Messages are safe to log: they never echo request or job bodies.</summary>
public sealed class BackendException(string message) : Exception(message);
