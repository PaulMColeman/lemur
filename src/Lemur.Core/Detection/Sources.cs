using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Lemur.Logging;
using Microsoft.Win32;

namespace Lemur.Detection;

/// <summary>One app's entry in Windows' microphone privacy registry.</summary>
/// <param name="StartedFileTime">When the app last opened the mic (LastUsedTimeStart); a new value means it closed and re-opened it.</param>
public readonly record struct MicUsage(string AppKey, bool InUse, long StartedFileTime = 0);

public interface IMicUsageSource
{
    IReadOnlyList<MicUsage> Read();
}

/// <summary>
/// Reads which apps hold the microphone right now. Windows keeps this per user under
/// HKCU\...\CapabilityAccessManager\ConsentStore\microphone (the data behind the taskbar's
/// "app is using your microphone" icon): LastUsedTimeStop is 0 while an app has it open.
/// Store apps (new Teams) are direct subkeys; desktop apps (Edge, Chrome) sit under NonPackaged
/// with their exe path as the key name, '\' replaced by '#'.
/// </summary>
public sealed class RegistryMicUsageSource : IMicUsageSource
{
    const string BaseKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    /// <summary>
    /// An app that crashes while holding the mic leaves LastUsedTimeStop at 0 forever (seen in the
    /// wild: a 2024 Discord entry still "in use"). No real call outlasts this, so older starts are stale.
    /// </summary>
    public static readonly TimeSpan MaxInUseAge = TimeSpan.FromHours(12);

    public IReadOnlyList<MicUsage> Read()
    {
        var result = new List<MicUsage>();
        var now = DateTime.UtcNow;
        using var root = Registry.CurrentUser.OpenSubKey(BaseKey);
        if (root is null) return result;
        foreach (var name in root.GetSubKeyNames())
        {
            if (string.Equals(name, "NonPackaged", StringComparison.OrdinalIgnoreCase))
            {
                using var nonPackaged = root.OpenSubKey(name);
                if (nonPackaged is null) continue;
                foreach (var app in nonPackaged.GetSubKeyNames()) AddEntry(nonPackaged, app, result, now);
            }
            else
            {
                AddEntry(root, name, result, now);
            }
        }
        return result;
    }

    static void AddEntry(RegistryKey parent, string name, List<MicUsage> into, DateTime nowUtc)
    {
        using var key = parent.OpenSubKey(name);
        if (key is null) return;
        long start = key.GetValue("LastUsedTimeStart") is long s ? s : 0;
        long stop = key.GetValue("LastUsedTimeStop") is long t ? t : -1;
        into.Add(new MicUsage(name, IsInUse(start, stop, nowUtc), start));
    }

    internal static bool IsInUse(long startFileTime, long stopFileTime, DateTime nowUtc)
    {
        if (startFileTime <= 0 || stopFileTime != 0) return false;
        try
        {
            return nowUtc - DateTime.FromFileTimeUtc(startFileTime) < MaxInUseAge;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}

/// <summary>
/// Which processes are capturing audio right now, from Windows' audio session list — the ground truth
/// the registry can lose after a crash. Null when it cannot be read (callers then trust the registry).
/// </summary>
public interface ICaptureSessionSource
{
    IReadOnlySet<string>? ActiveCaptureProcesses();
}

/// <summary>An active audio stream: which process, on which endpoint.</summary>
/// <param name="Adapter">The physical device's name ("Arctis 7+"), shared by its mic and output endpoints.</param>
public readonly record struct AudioSessionInfo(string ProcessName, string DeviceId, string DeviceName, string Adapter, bool IsCapture);

/// <summary>An active audio endpoint (a microphone, or an output that loopback can record).</summary>
public readonly record struct AudioEndpoint(string Id, string Name, string Adapter, bool IsCapture);

public interface IAudioSessionSource
{
    IReadOnlyList<AudioSessionInfo> ActiveSessions();
    IReadOnlyList<AudioEndpoint> Endpoints();
}

public sealed class WasapiSessionSource : ICaptureSessionSource, IAudioSessionSource
{
    public IReadOnlySet<string>? ActiveCaptureProcesses()
    {
        try
        {
            return ActiveSessionsCore(captureOnly: true).Select(s => s.ProcessName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<AudioSessionInfo> ActiveSessions()
    {
        try { return ActiveSessionsCore(captureOnly: false); }
        catch { return []; }
    }

    public IReadOnlyList<AudioEndpoint> Endpoints()
    {
        try
        {
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            return enumerator.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.All, NAudio.CoreAudioApi.DeviceState.Active)
                .Select(d =>
                {
                    var (name, adapter) = NamesOf(d);
                    return new AudioEndpoint(d.ID, name, adapter, d.DataFlow == NAudio.CoreAudioApi.DataFlow.Capture);
                })
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    static List<AudioSessionInfo> ActiveSessionsCore(bool captureOnly)
    {
        var list = new List<AudioSessionInfo>();
        var names = new Dictionary<uint, string>();
        using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        var flow = captureOnly ? NAudio.CoreAudioApi.DataFlow.Capture : NAudio.CoreAudioApi.DataFlow.All;
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, NAudio.CoreAudioApi.DeviceState.Active))
        {
            var sessions = device.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (session.State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive) continue;
                uint pid = session.GetProcessID;
                if (!names.TryGetValue(pid, out var name))
                {
                    try
                    {
                        using var p = Process.GetProcessById((int)pid);
                        name = p.ProcessName;
                    }
                    catch
                    {
                        name = "";
                    }
                    names[pid] = name;
                }
                if (name.Length == 0) continue;
                var (deviceName, adapter) = NamesOf(device);
                list.Add(new AudioSessionInfo(name, device.ID, deviceName, adapter,
                    device.DataFlow == NAudio.CoreAudioApi.DataFlow.Capture));
            }
        }
        return list;
    }

    /// <summary>
    /// Each endpoint's name and its device's, by endpoint ID. Reading a name opens the endpoint's property store, about
    /// 50 ms a name on Paul's PC (13 endpoints), while listing endpoints and their sessions takes a few ms. Read at every
    /// look, the names took 0.2 s of every second of a call (is the browser capturing?) and 1.8 s of every five (following
    /// the call's devices), on the tray's UI thread: every window was sluggish while recording (2026-10-19). A name
    /// changes only when the device is renamed, which Windows reports (<see cref="NameWatch"/>).
    /// </summary>
    static readonly ConcurrentDictionary<string, (string Name, string Adapter)> Names = new(StringComparer.OrdinalIgnoreCase);

    static (string Name, string Adapter) NamesOf(NAudio.CoreAudioApi.MMDevice device)
    {
        NameWatch.Start();
        return Names.GetOrAdd(device.ID, _ => (device.FriendlyName, Adapter(device)));
    }

    static string Adapter(NAudio.CoreAudioApi.MMDevice device)
    {
        try { return device.DeviceFriendlyName ?? ""; }
        catch { return ""; }
    }

    /// <summary>
    /// Forgets an endpoint's names when Windows says they changed (renamed in Sound settings, or the device went away), so
    /// the next look reads them again. Registered once, for the app's life: its enumerator has to outlive it. If Windows
    /// won't take it, names stay as first read until Lemur restarts.
    /// </summary>
    internal sealed class NameWatch : NAudio.CoreAudioApi.Interfaces.IMMNotificationClient
    {
        static readonly object Gate = new();
        static NAudio.CoreAudioApi.MMDeviceEnumerator? _enumerator;
        static NameWatch? _client;
        static bool _started;

        public static void Start()
        {
            if (Volatile.Read(ref _started)) return;
            lock (Gate)
            {
                if (_started) return;
                try
                {
                    _client = new NameWatch();
                    _enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                    _enumerator.RegisterEndpointNotificationCallback(_client);
                }
                catch (Exception ex)
                {
                    Log.Warn($"audio devices: Windows won't say when one is renamed ({Log.TypeAndCode(ex)}); names stay as first read");
                }
                Volatile.Write(ref _started, true);
            }
        }

        /// <summary>A name is in these groups: the endpoint's own (its description and FriendlyName) and its device's.</summary>
        internal static bool Renames(NAudio.CoreAudioApi.PropertyKey key) =>
            key.formatId == NAudio.CoreAudioApi.PropertyKeys.PKEY_Device_FriendlyName.formatId ||
            key.formatId == NAudio.CoreAudioApi.PropertyKeys.PKEY_DeviceInterface_FriendlyName.formatId;

        internal static void Remember(string id, string name, string adapter) => Names[id] = (name, adapter);
        internal static bool Knows(string id) => Names.ContainsKey(id);

        // Windows' threads call these: they only drop an entry.
        public void OnPropertyValueChanged(string pwstrDeviceId, NAudio.CoreAudioApi.PropertyKey key)
        {
            if (Renames(key)) Names.TryRemove(pwstrDeviceId, out _);
        }

        public void OnDeviceStateChanged(string deviceId, NAudio.CoreAudioApi.DeviceState newState) => Names.TryRemove(deviceId, out _);
        public void OnDeviceRemoved(string deviceId) => Names.TryRemove(deviceId, out _);
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDefaultDeviceChanged(NAudio.CoreAudioApi.DataFlow flow, NAudio.CoreAudioApi.Role role, string defaultDeviceId) { }
    }
}

/// <summary>A visible top-level window.</summary>
public readonly record struct WindowInfo(string ProcessName, string Title);

public interface IWindowSource
{
    IReadOnlyList<WindowInfo> Read();
}

public sealed class Win32WindowSource : IWindowSource
{
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public IReadOnlyList<WindowInfo> Read()
    {
        var processNames = new Dictionary<uint, string>();
        var windows = new List<WindowInfo>();
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            int length = GetWindowTextLengthW(hWnd);
            if (length <= 0) return true;
            var text = new StringBuilder(length + 1);
            GetWindowTextW(hWnd, text, text.Capacity);
            GetWindowThreadProcessId(hWnd, out var pid);
            if (!processNames.TryGetValue(pid, out var processName))
            {
                try
                {
                    using var p = Process.GetProcessById((int)pid);
                    processName = p.ProcessName;
                }
                catch
                {
                    processName = "";
                }
                processNames[pid] = processName;
            }
            windows.Add(new WindowInfo(processName, text.ToString()));
            return true;
        }, IntPtr.Zero);
        return windows;
    }
}
