using System.Reflection;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Lemur.Logging;

namespace Lemur.Audio;

/// <summary>
/// Windows turns other apps down ("communications ducking", 80% by default) whenever something opens
/// the default communications microphone. That is Teams' job during a call; a recorder doing it on its
/// own — e.g. a manual in-person recording — would silently duck the user's music. So we opt out.
/// </summary>
static class AudioDucking
{
    // NAudio 2.2.1 wraps IAudioSessionControl2 but does not expose SetDuckingPreference on the wrapper.
    static readonly FieldInfo? Control2Field =
        typeof(AudioSessionControl).GetField("audioSessionControlInterface2", BindingFlags.NonPublic | BindingFlags.Instance);

    /// <summary>Call before the stream starts; applies to this process's default session on the device.</summary>
    public static void OptOut(MMDevice device)
    {
        try
        {
            var control = device.AudioSessionManager.AudioSessionControl;
            if (Control2Field?.GetValue(control) is IAudioSessionControl2 control2)
                control2.SetDuckingPreference(true);
            else
                Log.Warn("ducking opt-out unavailable (NAudio internals changed); other apps may be turned down while recording");
        }
        catch (Exception ex)
        {
            Log.Warn($"ducking opt-out failed: {ex.Message}");
        }
    }
}
