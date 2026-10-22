using Lemur.Audio;
using Lemur.Config;
using Lemur.Detection;
using Lemur.Logging;

namespace Lemur.Pipeline;

/// <summary>One meeting being recorded right now.</summary>
public sealed class RecordingSession : IDisposable
{
    readonly DualTrackRecorder _recorder;
    bool _finished;

    RecordingSession(RecordingFolder folder, DualTrackRecorder recorder, string source, string title, bool manual)
    {
        Folder = folder;
        _recorder = recorder;
        Source = source;
        Title = title;
        Manual = manual;
    }

    public RecordingFolder Folder { get; }
    public string Source { get; }
    public string Title { get; }
    public bool Manual { get; }

    /// <summary>"Keep recording" ticked while it is being recorded (the Recordings window): it goes into meta.json at the end.</summary>
    public bool KeepRecording { get; set; }

    public DateTimeOffset StartedAt => _recorder.StartedAt;
    public TimeSpan Elapsed => _recorder.Elapsed;

    public string? MicDeviceId => _recorder.MicDeviceId;
    public string? OutputDeviceId => _recorder.OutputDeviceId;

    /// <param name="devices">The devices the call is using (null = Windows defaults).</param>
    public static RecordingSession Start(AppSettings settings, string source, string? title, bool manual, DevicePick? devices = null)
    {
        var folder = RecordingFolder.CreateNew(settings.ResolvedRecordingsFolder, DateTimeOffset.Now);
        var recorder = new DualTrackRecorder(folder.Path, settings.Audio, devices?.MicId, devices?.OutputId);
        try
        {
            recorder.Start();
        }
        catch
        {
            recorder.Dispose();
            TryDelete(folder.Path);
            throw;
        }
        var session = new RecordingSession(folder, recorder, source, title ?? "", manual);
        Log.Info($"{folder.Name}: recording started ({source}{(manual ? ", manual" : "")})");
        return session;
    }

    /// <summary>The call switched devices: follow it. A pick without a microphone is ignored.</summary>
    public void Follow(DevicePick pick)
    {
        if (_finished || pick.MicId is null) return;
        Log.Info($"{Folder.Name}: following the call to mic='{pick.MicName}' output='{pick.OutputName ?? "default"}' ({pick.Reason})");
        _recorder.Retarget(pick.MicId, pick.OutputId);
    }

    /// <summary>Stops and queues the meeting for processing.</summary>
    public RecordingMeta Stop()
    {
        var meta = Finish();
        Folder.SaveMeta(meta);
        Folder.SaveState(new ProcessingState());
        Log.Info($"{Folder.Name}: recording stopped after {meta.DurationSeconds:0}s");
        return meta;
    }

    /// <summary>Stops and deletes everything recorded.</summary>
    public void Discard(string reason)
    {
        Finish();
        TryDelete(Folder.Path);
        Log.Info($"{Folder.Name}: discarded ({reason})");
    }

    RecordingMeta Finish()
    {
        if (_finished) throw new InvalidOperationException("already stopped");
        _finished = true;
        var stats = _recorder.Stop();
        _recorder.Dispose();
        return new RecordingMeta
        {
            Title = Title,
            Source = Source,
            Manual = Manual,
            KeepRecording = KeepRecording,
            StartedAt = _recorder.StartedAt,
            EndedAt = DateTimeOffset.Now,
            DurationSeconds = Math.Round(stats.Duration.TotalSeconds, 1),
            MicDevice = stats.MicDevice,
            OutputDevice = stats.OutputDevice,
            MicPaddedSeconds = stats.MicPaddedSeconds,
            OthersPaddedSeconds = stats.OthersPaddedSeconds,
            DeviceRestarts = stats.DeviceRestarts,
        };
    }

    static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) { Log.Warn($"could not delete {path}: {ex.Message}"); }
    }

    public void Dispose()
    {
        if (!_finished)
        {
            try { Stop(); } catch (Exception ex) { Log.Error("stopping recording during shutdown failed", ex); }
        }
    }
}
