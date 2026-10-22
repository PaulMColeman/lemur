using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using Lemur.Config;
using Lemur.Logging;

namespace Lemur.Audio;

public sealed record RecordingStats(
    TimeSpan Duration,
    string? MicDevice,
    string? OutputDevice,
    double MicPaddedSeconds,
    double OthersPaddedSeconds,
    double MicTrimmedSeconds,
    double OthersTrimmedSeconds,
    int DeviceRestarts);

/// <summary>
/// Records two aligned 16 kHz mono WAVs into a folder: <c>mic.wav</c> (your microphone) and
/// <c>others.wav</c> (a loopback copy of what the speakers/headset play — everyone else on the call).
/// Follows Windows default-device changes and survives a device disappearing mid-recording.
/// </summary>
public sealed class DualTrackRecorder : IDisposable
{
    public const string MicFile = "mic.wav";
    public const string OthersFile = "others.wav";

    readonly AudioSettings _settings;
    readonly Stopwatch _clock = new();
    readonly MMDeviceEnumerator _enumerator = new();
    readonly TrackCapture _mic;
    readonly TrackCapture _others;
    readonly DeviceWatcher _watcher;
    readonly object _keepAliveGate = new();
    WasapiOut? _keepAlive;
    bool _started, _stopped;

    /// <param name="micDeviceId">Endpoint to record as the mic (e.g. the one the call uses); null = default.</param>
    /// <param name="outputDeviceId">Output endpoint to loop back; null = default. Pinned names in settings win over both.</param>
    public DualTrackRecorder(string folder, AudioSettings settings, string? micDeviceId = null, string? outputDeviceId = null)
    {
        _settings = settings;
        Directory.CreateDirectory(folder);
        _mic = new TrackCapture("mic", DataFlow.Capture, settings.MicDeviceContains, micDeviceId, _clock, _enumerator,
            new AnchoredTrackWriter(new WavSampleSink(Path.Combine(folder, MicFile))));
        _others = new TrackCapture("others", DataFlow.Render, settings.OutputDeviceContains, outputDeviceId, _clock, _enumerator,
            new AnchoredTrackWriter(new WavSampleSink(Path.Combine(folder, OthersFile))));
        _watcher = new DeviceWatcher(OnDefaultDeviceChanged);
    }

    public string? MicDeviceId => _mic.DeviceId;
    public string? OutputDeviceId => _others.DeviceId;

    /// <summary>Move to the devices the call is now using (it switched headsets mid-call). Gaps are padded.</summary>
    public void Retarget(string? micDeviceId, string? outputDeviceId)
    {
        if (_stopped) return;
        _mic.Retarget(micDeviceId, "the call moved to another microphone");
        if (_others.Retarget(outputDeviceId, "the call moved to another output"))
            Task.Run(() => { StopKeepAlive(); if (!_stopped) StartKeepAlive(); });
    }

    public DateTimeOffset StartedAt { get; private set; }
    public TimeSpan Elapsed => _clock.Elapsed;

    public void Start()
    {
        if (_started) throw new InvalidOperationException("already started");
        _started = true;
        StartedAt = DateTimeOffset.Now;
        _clock.Start();
        StartKeepAlive();
        _others.Open();
        _mic.Open();
        try { _enumerator.RegisterEndpointNotificationCallback(_watcher); }
        catch (Exception ex) { Log.Warn($"device change notifications unavailable: {ex.Message}"); }
        Log.Info($"recording: mic='{_mic.DeviceName}' output='{_others.DeviceName}'");
    }

    public RecordingStats Stop()
    {
        if (!_started || _stopped) throw new InvalidOperationException("not recording");
        _stopped = true;
        try { _enumerator.UnregisterEndpointNotificationCallback(_watcher); } catch { /* best effort */ }
        _mic.Close();
        _others.Close();
        StopKeepAlive();
        var duration = _clock.Elapsed;
        long total = (long)Math.Round(duration.TotalSeconds * AnchoredTrackWriter.SampleRate);
        _mic.Writer.PadTo(total);
        _others.Writer.PadTo(total);
        var stats = new RecordingStats(duration, _mic.DeviceName, _others.DeviceName,
            Seconds(_mic.Writer.PaddedSamples), Seconds(_others.Writer.PaddedSamples),
            Seconds(_mic.Writer.TrimmedSamples), Seconds(_others.Writer.TrimmedSamples),
            _mic.Restarts + _others.Restarts);
        _mic.Writer.Dispose();
        _others.Writer.Dispose();
        return stats;
    }

    static double Seconds(long samples) => Math.Round(samples / (double)AnchoredTrackWriter.SampleRate, 3);

    void OnDefaultDeviceChanged(DataFlow flow)
    {
        if (_stopped) return;
        if (flow == DataFlow.Capture && _mic.FollowsDefault) _mic.Restart("default microphone changed");
        if (flow == DataFlow.Render && _others.FollowsDefault)
        {
            _others.Restart("default output changed");
            Task.Run(() => { StopKeepAlive(); if (!_stopped) StartKeepAlive(); });
        }
    }

    void StartKeepAlive()
    {
        if (!_settings.KeepAliveOutput) return;
        lock (_keepAliveGate)
        {
            try
            {
                var device = TrackCapture.Resolve(_enumerator, DataFlow.Render, _settings.OutputDeviceContains, _others.TargetId);
                if (device is null) return;
                var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 200);
                output.Init(new SilenceProvider(device.AudioClient.MixFormat));
                output.Play();
                _keepAlive = output;
            }
            catch (Exception ex)
            {
                Log.Warn($"keep-alive output failed (loopback may gap during silence; gaps are padded): {ex.Message}");
            }
        }
    }

    void StopKeepAlive()
    {
        lock (_keepAliveGate)
        {
            try { _keepAlive?.Stop(); _keepAlive?.Dispose(); } catch { /* best effort */ }
            _keepAlive = null;
        }
    }

    public void Dispose()
    {
        if (_started && !_stopped)
        {
            try { Stop(); } catch (Exception ex) { Log.Error("stop during dispose failed", ex); }
        }
        _enumerator.Dispose();
    }

    /// <summary>Raised by Windows on its own COM thread; hand off immediately.</summary>
    sealed class DeviceWatcher(Action<DataFlow> onDefaultChanged) : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (role == Role.Console) Task.Run(() => onDefaultChanged(flow));
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}

/// <summary>One WASAPI stream (mic, or loopback of an output) feeding one anchored writer.</summary>
sealed class TrackCapture
{
    readonly string _name;
    readonly DataFlow _flow;
    readonly string? _deviceContains;
    volatile string? _targetId;
    readonly Stopwatch _clock;
    readonly MMDeviceEnumerator _enumerator;
    readonly object _gate = new();
    readonly float[] _converted = new float[AnchoredTrackWriter.SampleRate * 2];
    IWaveIn? _capture;
    ToMono16k? _converter;
    volatile bool _closing;
    int _restartPending;

    public TrackCapture(string name, DataFlow flow, string? deviceContains, string? targetId, Stopwatch clock,
        MMDeviceEnumerator enumerator, AnchoredTrackWriter writer)
    {
        _name = name;
        _flow = flow;
        _deviceContains = deviceContains;
        _targetId = targetId;
        _clock = clock;
        _enumerator = enumerator;
        Writer = writer;
    }

    public AnchoredTrackWriter Writer { get; }
    public string? DeviceName { get; private set; }
    public string? DeviceId { get; private set; }
    public string? TargetId => _targetId;
    public int Restarts { get; private set; }
    public bool FollowsDefault => string.IsNullOrWhiteSpace(_deviceContains) && _targetId is null;

    /// <summary>A device pinned by name in settings wins; then the device the call uses; then the Windows default.</summary>
    public static MMDevice? Resolve(MMDeviceEnumerator enumerator, DataFlow flow, string? nameContains, string? targetId = null)
    {
        if (!string.IsNullOrWhiteSpace(nameContains))
        {
            foreach (var d in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
                if (d.FriendlyName.Contains(nameContains, StringComparison.OrdinalIgnoreCase)) return d;
            Log.Warn($"no active {flow} device matches '{nameContains}'; using the Windows default");
        }
        else if (targetId is not null)
        {
            try
            {
                var d = enumerator.GetDevice(targetId);
                if (d.State == DeviceState.Active) return d;
            }
            catch { /* unplugged since it was picked */ }
            Log.Warn($"the call's {flow} device is no longer available; using the Windows default");
        }
        try { return enumerator.GetDefaultAudioEndpoint(flow, Role.Console); }
        catch { return null; } // no device of this kind at all
    }

    /// <summary>Switch to another endpoint; returns true when that means reopening.</summary>
    public bool Retarget(string? targetId, string reason)
    {
        if (targetId is null || targetId == DeviceId || !string.IsNullOrWhiteSpace(_deviceContains)) return false;
        _targetId = targetId;
        Restart(reason);
        return true;
    }

    public void Open()
    {
        lock (_gate)
        {
            if (_closing) return;
            var device = Resolve(_enumerator, _flow, _deviceContains, _targetId)
                         ?? throw new InvalidOperationException($"{_name}: no {_flow} audio device");
            if (_flow == DataFlow.Capture) AudioDucking.OptOut(device); // before the stream opens
            IWaveIn capture = _flow == DataFlow.Render
                ? new WasapiLoopbackCapture(device)
                : new WasapiCapture(device, true, 50);
            _converter = new ToMono16k(capture.WaveFormat);
            capture.DataAvailable += OnData;
            capture.RecordingStopped += OnStopped;
            capture.StartRecording();
            _capture = capture;
            DeviceName = device.FriendlyName;
            DeviceId = device.ID;
        }
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;
        var arrival = _clock.Elapsed;
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _capture) || _converter is null) return; // a stale stream after a restart
            _converter.Add(e.Buffer, e.BytesRecorded);
            int n;
            while ((n = _converter.Read(_converted)) > 0)
                Writer.Append(_converted.AsSpan(0, n), arrival);
        }
    }

    void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (_closing || !ReferenceEquals(sender, _capture)) return;
        Restart(e.Exception is null ? "stream stopped" : $"device error: {e.Exception.Message}");
    }

    /// <summary>Reopen on the current device; the writer pads whatever gap this causes.</summary>
    public void Restart(string reason)
    {
        if (_closing || Interlocked.Exchange(ref _restartPending, 1) == 1) return;
        Log.Warn($"{_name}: {reason}; reopening");
        Task.Run(async () =>
        {
            try
            {
                CloseCapture();
                for (int attempt = 1; attempt <= 30 && !_closing; attempt++)
                {
                    try
                    {
                        Open();
                        Restarts++;
                        Log.Info($"{_name}: now recording from '{DeviceName}'");
                        return;
                    }
                    catch (Exception ex)
                    {
                        if (attempt == 1 || attempt % 10 == 0) Log.Warn($"{_name}: reopen attempt {attempt} failed: {ex.Message}");
                        await Task.Delay(1000);
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _restartPending, 0);
            }
        });
    }

    public void Close()
    {
        _closing = true;
        CloseCapture();
    }

    void CloseCapture()
    {
        IWaveIn? capture;
        lock (_gate)
        {
            capture = _capture;
            _capture = null;
            _converter = null;
        }
        if (capture is null) return;
        try
        {
            capture.DataAvailable -= OnData;
            capture.RecordingStopped -= OnStopped;
            capture.StopRecording();
        }
        catch { /* the device may already be gone */ }
        try { capture.Dispose(); } catch { /* same */ }
    }
}
