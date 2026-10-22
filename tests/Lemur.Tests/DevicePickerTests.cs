using Lemur.Config;
using Lemur.Detection;

namespace Lemur.Tests;

public class CallDevicePickerTests
{
    static readonly WatchRule Edge = WatchRule.Defaults().Single(r => r.Name == "Teams (Edge)");

    static readonly AudioEndpoint JabraMic = new("jabra-in", "Microphone (Jabra SPEAK 410 USB)", "Jabra SPEAK 410 USB", true);
    static readonly AudioEndpoint JabraOut = new("jabra-out", "Speakers (Jabra SPEAK 410 USB)", "Jabra SPEAK 410 USB", false);
    static readonly AudioEndpoint ArctisMic = new("arctis-in", "Microphone (Arctis 7+)", "Arctis 7+", true);
    static readonly AudioEndpoint ArctisOut = new("arctis-out", "Headphones (Arctis 7+)", "Arctis 7+", false);
    static readonly AudioEndpoint WebcamMic = new("c920-in", "Microphone (HD Pro Webcam C920)", "HD Pro Webcam C920", true);
    static readonly AudioEndpoint StereoMix = new("stereo-mix", "Stereo Mix (Realtek(R) Audio)", "Realtek(R) Audio", true);
    static readonly AudioEndpoint RealtekDigital = new("realtek-spdif", "Realtek Digital Output (Realtek(R) Audio)", "Realtek(R) Audio", false);
    static readonly AudioEndpoint[] All = [JabraMic, JabraOut, ArctisMic, ArctisOut, WebcamMic, StereoMix, RealtekDigital];

    static AudioSessionInfo On(AudioEndpoint e, string process) => new(process, e.Id, e.Name, e.Adapter, e.IsCapture);

    [Fact]
    public void Everyday_Jabra_call_records_the_Jabra()
    {
        var pick = CallDevicePicker.Pick(Edge, [On(JabraMic, "msedge"), On(JabraOut, "msedge")], All);
        Assert.Equal("jabra-in", pick.MicId);
        Assert.Equal("jabra-out", pick.OutputId);
    }

    [Fact]
    public void Private_call_on_the_Arctis_is_followed_even_with_YouTube_on_the_default_Jabra()
    {
        // Teams (Edge) captures from the Arctis; YouTube (also Edge) plays on the Jabra.
        var pick = CallDevicePicker.Pick(Edge, [On(ArctisMic, "msedge"), On(JabraOut, "msedge"), On(ArctisOut, "msedge")], All);
        Assert.Equal("arctis-in", pick.MicId);
        Assert.Equal("arctis-out", pick.OutputId); // paired by device name, not "wherever Edge plays"
        Assert.Contains("Arctis 7+", pick.Reason);
    }

    [Fact]
    public void Webcam_mic_with_speakers_follows_where_the_call_plays()
    {
        var pick = CallDevicePicker.Pick(Edge, [On(WebcamMic, "msedge"), On(JabraOut, "msedge")], All);
        Assert.Equal("c920-in", pick.MicId);
        Assert.Equal("jabra-out", pick.OutputId);
    }

    [Fact]
    public void A_call_playing_away_from_its_mics_card_is_recorded_where_it_plays()
    {
        // Paul's Zoom call in Edge, 2026-10-21: Zoom took Windows' default mic, Stereo Mix, and played on the Jabra. Paired
        // by card name, the call audio came from the card's unused digital output: 33 minutes of silence.
        var pick = CallDevicePicker.Pick(Edge, [On(StereoMix, "msedge"), On(JabraOut, "msedge")], All);
        Assert.Equal("stereo-mix", pick.MicId);
        Assert.Equal("jabra-out", pick.OutputId);
    }

    [Fact]
    public void A_manual_recording_follows_the_app_that_had_a_mic_open_when_it_started()
    {
        var rule = CallDevicePicker.ManualRule(null, [On(StereoMix, "msedge"), On(JabraOut, "msedge")], self: "Lemur");
        Assert.Equal(["msedge"], rule!.AudioProcesses);

        // The mic changed in Zoom; Discord opened one too. Still Edge's, now on the Jabra.
        IReadOnlyList<AudioSessionInfo> later = [On(ArctisMic, "Discord"), On(JabraMic, "msedge"), On(JabraOut, "msedge"), On(JabraMic, "Lemur")];
        Assert.Same(rule, CallDevicePicker.ManualRule(rule, later, self: "Lemur"));
        Assert.Equal(("jabra-in", "jabra-out"), (CallDevicePicker.Pick(rule, later, All).MicId, CallDevicePicker.Pick(rule, later, All).OutputId));

        // Started before joining: nothing to follow until an app opens a mic, then that app.
        Assert.Null(CallDevicePicker.ManualRule(null, [On(JabraOut, "msedge"), On(JabraMic, "Lemur")], self: "Lemur"));
        Assert.Equal(["msedge"], CallDevicePicker.ManualRule(null, [On(JabraMic, "msedge")], self: "Lemur")!.AudioProcesses);
    }

    [Fact]
    public void Ambiguous_output_falls_back_to_the_default()
    {
        var pick = CallDevicePicker.Pick(Edge, [On(WebcamMic, "msedge"), On(JabraOut, "msedge"), On(ArctisOut, "msedge")], All);
        Assert.Equal("c920-in", pick.MicId);
        Assert.Null(pick.OutputId);
    }

    [Fact]
    public void Other_apps_using_devices_are_ignored()
    {
        var pick = CallDevicePicker.Pick(Edge, [On(ArctisMic, "Discord"), On(JabraMic, "msedge")], All);
        Assert.Equal("jabra-in", pick.MicId);
    }

    [Fact]
    public void No_call_mic_stream_means_Windows_defaults()
    {
        var pick = CallDevicePicker.Pick(Edge, [On(JabraOut, "msedge")], All);
        Assert.Null(pick.MicId);
        Assert.Null(pick.OutputId);
    }

    [Fact]
    public void Old_settings_files_get_audio_processes_filled_in()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            { "detection": { "sources": [
                { "name": "Teams (Edge)", "appKeyContains": "#msedge.exe", "windowProcess": "msedge", "windowTitleContains": "Microsoft Teams" },
                { "name": "My Zoom", "appKeyContains": "#Zoom.exe" } ] } }
            """);
        var s = SettingsStore.LoadOrCreate(path);
        Assert.Equal(["msedge"], s.Detection.Sources[0].AudioProcesses);
        Assert.Empty(s.Detection.Sources[1].AudioProcesses); // custom rule: nothing to borrow
        Assert.True(s.Audio.FollowCallDevices);
    }
}

/// <summary>Device names are read once and kept until Windows says they changed (reading one takes ~50 ms).</summary>
public class DeviceNameTests
{
    [Fact]
    public void A_rename_or_a_device_going_away_forgets_its_names_and_nothing_else_does()
    {
        var watch = new WasapiSessionSource.NameWatch();
        string id = "{0.0.1.00000000}." + Guid.NewGuid();
        WasapiSessionSource.NameWatch.Remember(id, "Microphone (Jabra SPEAK 410 USB)", "Jabra SPEAK 410 USB");

        watch.OnPropertyValueChanged(id, new NAudio.CoreAudioApi.PropertyKey(new Guid("f19f064d-082c-4e27-bc73-6882a1bb8e4c"), 0)); // its format
        watch.OnDefaultDeviceChanged(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.Role.Console, id);
        watch.OnDeviceAdded(id);
        Assert.True(WasapiSessionSource.NameWatch.Knows(id));

        watch.OnPropertyValueChanged(id.ToUpperInvariant(), NAudio.CoreAudioApi.PropertyKeys.PKEY_Device_DeviceDesc); // renamed in Sound settings
        Assert.False(WasapiSessionSource.NameWatch.Knows(id));

        Action<string>[] goneOrRenamed =
        [
            i => watch.OnDeviceRemoved(i),
            i => watch.OnDeviceStateChanged(i, NAudio.CoreAudioApi.DeviceState.Unplugged),
            i => watch.OnPropertyValueChanged(i, NAudio.CoreAudioApi.PropertyKeys.PKEY_DeviceInterface_FriendlyName),
        ];
        foreach (var gone in goneOrRenamed)
        {
            WasapiSessionSource.NameWatch.Remember(id, "Microphone (Arctis 7+)", "Arctis 7+");
            gone(id);
            Assert.False(WasapiSessionSource.NameWatch.Knows(id));
        }
    }
}
