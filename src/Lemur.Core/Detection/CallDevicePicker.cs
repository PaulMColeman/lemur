using Lemur.Config;

namespace Lemur.Detection;

/// <param name="MicId">Endpoint id to record as "mic", or null for the Windows default.</param>
/// <param name="OutputId">Endpoint id to record as "everyone else" (loopback), or null for the Windows default.</param>
public sealed record DevicePick(string? MicId, string? MicName, string? OutputId, string? OutputName, string Reason)
{
    public static readonly DevicePick Defaults = new(null, null, null, null, "Windows default devices");
}

/// <summary>
/// Finds the devices a call is really using. Teams can be pointed at a headset that is not the Windows
/// default (e.g. the Jabra is default, but a private call goes to the Arctis); recording the defaults
/// would then capture the wrong microphone and miss the other side entirely.
/// </summary>
public static class CallDevicePicker
{
    public static DevicePick Pick(WatchRule rule, IReadOnlyList<AudioSessionInfo> sessions, IReadOnlyList<AudioEndpoint> endpoints)
    {
        if (rule.AudioProcesses.Count == 0) return DevicePick.Defaults with { Reason = "rule lists no audio processes; Windows defaults" };
        bool IsCallApp(AudioSessionInfo s) => rule.AudioProcesses.Contains(s.ProcessName, StringComparer.OrdinalIgnoreCase);

        var mic = sessions.FirstOrDefault(s => s.IsCapture && IsCallApp(s));
        if (mic.DeviceId is null) return DevicePick.Defaults with { Reason = "call app has no active microphone stream; Windows defaults" };

        // A headset's mic and output share its device name: "Microphone (Arctis 7+)" / "Headphones (Arctis 7+)".
        var paired = string.IsNullOrWhiteSpace(mic.Adapter) ? []
            : endpoints.Where(e => !e.IsCapture && string.Equals(e.Adapter, mic.Adapter, StringComparison.OrdinalIgnoreCase)).ToList();
        var plays = sessions.Where(s => !s.IsCapture && IsCallApp(s)).GroupBy(s => s.DeviceId).Select(g => g.First()).ToList();
        var heard = paired.Where(e => plays.Any(p => p.DeviceId == e.Id)).ToList();
        if (heard.Count == 1)
            return new DevicePick(mic.DeviceId, mic.DeviceName, heard[0].Id, heard[0].Name, $"call is on {mic.Adapter}");

        // Separate mic and speakers (webcam mic + desk speakers), or a mic whose card the call doesn't play on (Zoom on
        // Windows' default mic, Stereo Mix, playing on the Jabra: 2026-10-21): follow where the call app plays, if that is
        // unambiguous. Its mic's card is only a guess, and a card may have outputs nobody listens to (a digital output).
        if (plays.Count == 1)
            return new DevicePick(mic.DeviceId, mic.DeviceName, plays[0].DeviceId, plays[0].DeviceName,
                "call mic followed; output = where the call app plays");
        if (paired.Count == 1)
            return new DevicePick(mic.DeviceId, mic.DeviceName, paired[0].Id, paired[0].Name, $"call is on {mic.Adapter}");

        return new DevicePick(mic.DeviceId, mic.DeviceName, null, null, "call mic followed; output = Windows default");
    }

    /// <summary>
    /// What a manual recording follows, having no rule of its own: the apps that had a mic open when it started; if none had
    /// (started before joining), or they have all closed theirs, whichever apps have one open now. Null while none has.
    /// </summary>
    /// <param name="self">This app's process, whose own capture isn't a call.</param>
    public static WatchRule? ManualRule(WatchRule? following, IReadOnlyList<AudioSessionInfo> sessions, string self)
    {
        var capturing = sessions.Where(s => s.IsCapture && !s.ProcessName.Equals(self, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (following is not null && capturing.Any(p => following.AudioProcesses.Contains(p, StringComparer.OrdinalIgnoreCase))) return following;
        return capturing.Count == 0 ? following : new WatchRule { Name = "Manual", AudioProcesses = capturing };
    }
}
