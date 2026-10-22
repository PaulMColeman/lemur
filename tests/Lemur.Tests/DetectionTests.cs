using Lemur.Config;
using Lemur.Detection;

namespace Lemur.Tests;

public class CallDetectorTests
{
    const string Edge = @"C:#Program Files (x86)#Microsoft#Edge#Application#msedge.exe";
    const string NewTeams = "MSTeams_8wekyb3d8bbwe";
    static readonly DateTimeOffset T0 = new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);

    static readonly WindowInfo TeamsTab = new("msedge", "Weekly sync | Microsoft Teams and 11 more pages - Personal - Microsoft​ Edge");
    static readonly WindowInfo OtherTab = new("msedge", "YouTube - Personal - Microsoft Edge");

    static MicUsage[] Mic(params string[] inUse) =>
        [.. inUse.Select(k => new MicUsage(k, true)), new MicUsage("C:#Windows#explorer.exe", false)];

    static CallDetector Detector() => new(new DetectionSettings());

    [Fact]
    public void Edge_mic_with_a_Teams_window_starts_after_the_delay()
    {
        var d = Detector();
        Assert.Null(d.Tick(T0, Mic(Edge), [TeamsTab]));
        Assert.Equal(DetectorPhase.Pending, d.Phase);
        Assert.Null(d.Tick(T0.AddSeconds(2), Mic(Edge), [TeamsTab]));
        var ev = Assert.IsType<CallStarted>(d.Tick(T0.AddSeconds(3), Mic(Edge), [TeamsTab]));
        Assert.Equal("Teams (Edge)", ev.Source);
        Assert.Contains("Weekly sync", ev.WindowTitle);
        Assert.Equal(DetectorPhase.Recording, d.Phase);
    }

    [Fact]
    public void Edge_mic_without_a_Teams_window_never_starts()
    {
        var d = Detector();
        for (int s = 0; s < 120; s++) Assert.Null(d.Tick(T0.AddSeconds(s), Mic(Edge), [OtherTab]));
        Assert.Equal(DetectorPhase.Pending, d.Phase);
    }

    [Fact]
    public void A_Teams_window_appearing_later_still_starts_the_recording()
    {
        var d = Detector();
        d.Tick(T0, Mic(Edge), [OtherTab]);
        Assert.Null(d.Tick(T0.AddSeconds(10), Mic(Edge), [OtherTab]));
        Assert.IsType<CallStarted>(d.Tick(T0.AddSeconds(11), Mic(Edge), [OtherTab, TeamsTab]));
    }

    [Fact]
    public void A_short_mic_blip_does_not_start()
    {
        var d = Detector();
        d.Tick(T0, Mic(Edge), [TeamsTab]);
        Assert.Null(d.Tick(T0.AddSeconds(1), Mic(), [TeamsTab]));
        Assert.Equal(DetectorPhase.Idle, d.Phase);
    }

    [Fact]
    public void Teams_app_needs_no_window_and_finds_a_title_if_there_is_one()
    {
        var d = Detector();
        d.Tick(T0, Mic(NewTeams), []);
        var ev = Assert.IsType<CallStarted>(d.Tick(T0.AddSeconds(3), Mic(NewTeams), [new WindowInfo("ms-teams", "Standup | Microsoft Teams")]));
        Assert.Equal("Teams (app)", ev.Source);
        Assert.Equal("Standup | Microsoft Teams", ev.WindowTitle);
    }

    [Fact]
    public void Recording_survives_short_mic_gaps_and_stops_after_the_stop_delay()
    {
        var d = Detector();
        d.Tick(T0, Mic(Edge), [TeamsTab]);
        d.Tick(T0.AddSeconds(3), Mic(Edge), [TeamsTab]);
        // Headset switch: mic released for 10 s, then back. The window title changing doesn't matter mid-call.
        for (int s = 4; s < 14; s++) Assert.Null(d.Tick(T0.AddSeconds(s), Mic(), []));
        Assert.Null(d.Tick(T0.AddSeconds(14), Mic(Edge), [OtherTab]));
        Assert.Equal(DetectorPhase.Recording, d.Phase);
        // Call over: 15 s after the last sighting, it ends.
        Assert.Null(d.Tick(T0.AddSeconds(28), Mic(), []));
        var ended = Assert.IsType<CallEnded>(d.Tick(T0.AddSeconds(29), Mic(), []));
        Assert.Equal("Teams (Edge)", ended.Source);
        Assert.Equal(DetectorPhase.Idle, d.Phase);
    }

    [Fact]
    public void Suppressed_call_is_not_restarted_until_the_mic_is_released()
    {
        var d = Detector();
        d.Tick(T0, Mic(Edge), [TeamsTab]);
        d.Tick(T0.AddSeconds(3), Mic(Edge), [TeamsTab]);
        d.Suppress();
        for (int s = 4; s < 100; s++) Assert.Null(d.Tick(T0.AddSeconds(s), Mic(Edge), [TeamsTab]));
        Assert.Equal(DetectorPhase.Suppressed, d.Phase);
        d.Tick(T0.AddSeconds(100), Mic(), []);
        Assert.Equal(DetectorPhase.Idle, d.Phase);
        d.Tick(T0.AddSeconds(200), Mic(Edge), [TeamsTab]);
        Assert.IsType<CallStarted>(d.Tick(T0.AddSeconds(203), Mic(Edge), [TeamsTab]));
    }

    [Fact]
    public void Paused_detector_ignores_calls()
    {
        var d = Detector();
        d.Paused = true;
        for (int s = 0; s < 10; s++) Assert.Null(d.Tick(T0.AddSeconds(s), Mic(Edge), [TeamsTab]));
        Assert.Equal(DetectorPhase.Idle, d.Phase);
    }

    [Fact]
    public void Disabled_rule_is_ignored()
    {
        var settings = new DetectionSettings();
        settings.Sources.Single(r => r.Name == "Teams (Edge)").Enabled = false;
        var d = new CallDetector(settings);
        for (int s = 0; s < 10; s++) Assert.Null(d.Tick(T0.AddSeconds(s), Mic(Edge), [TeamsTab]));
    }

    // Paul's test call, 2026-10-14 (Teams in Edge, titles as Windows reported them).
    static readonly WindowInfo InCall = new("msedge", "Calendar | Test Meeting | Microsoft Teams and 15 more pages - Personal - Microsoft​ Edge");
    static readonly WindowInfo JoinScreen = new("msedge", "Calendar | Meeting join | Test Meeting | Microsoft Teams and 15 more pages - Personal - Microsoft​ Edge");

    static MicUsage[] MicSince(string key, long started) => [new MicUsage(key, true, started), new MicUsage("C:#Windows#explorer.exe", false)];

    [Fact]
    public void Leaving_and_joining_the_next_meeting_within_the_stop_delay_splits_the_recording()
    {
        var d = Detector();
        d.Tick(T0, MicSince(Edge, 100), [JoinScreen]);                                     // 00:09:05 join screen takes the mic
        var started = Assert.IsType<CallStarted>(d.Tick(T0.AddSeconds(4), MicSince(Edge, 100), [InCall]));
        Assert.Equal("Test Meeting", MeetingTitle.FromWindowTitle(started.WindowTitle));
        for (int s = 5; s < 103; s++) Assert.Null(d.Tick(T0.AddSeconds(s), MicSince(Edge, 100), [InCall])); // muted: nothing changes
        Assert.Null(d.Tick(T0.AddSeconds(103), Mic(), [InCall]));                           // 00:10:52 left
        Assert.Null(d.Tick(T0.AddSeconds(104), Mic(), [InCall]));
        var next = Assert.IsType<NextCall>(d.Tick(T0.AddSeconds(105), MicSince(Edge, 200), [JoinScreen])); // 00:10:54 next join screen
        Assert.Equal("Teams (Edge)", next.Source);
        Assert.Equal("Test Meeting", MeetingTitle.FromWindowTitle(next.WindowTitle));
        for (int s = 106; s < 136; s++) Assert.Null(d.Tick(T0.AddSeconds(s), MicSince(Edge, 200), [InCall]));
        Assert.Equal(DetectorPhase.Recording, d.Phase);
        for (int s = 136; s < 150; s++) Assert.Null(d.Tick(T0.AddSeconds(s), Mic(), [InCall])); // 00:11:25 left for good
        Assert.IsType<CallEnded>(d.Tick(T0.AddSeconds(150), Mic(), [InCall]));
    }

    [Fact]
    public void A_mic_reopened_between_ticks_is_noticed_by_its_new_start_time()
    {
        var d = Detector();
        d.Tick(T0, MicSince(Edge, 100), [InCall]);
        d.Tick(T0.AddSeconds(3), MicSince(Edge, 100), [InCall]);
        Assert.Null(d.Tick(T0.AddSeconds(60), MicSince(Edge, 100), [InCall]));
        Assert.IsType<NextCall>(d.Tick(T0.AddSeconds(61), MicSince(Edge, 300), [JoinScreen])); // no tick saw the gap
    }

    [Fact]
    public void A_device_change_reopens_the_mic_but_stays_one_recording()
    {
        var d = Detector();
        d.Tick(T0, MicSince(Edge, 100), [InCall]);
        for (int s = 3; s < 60; s++) d.Tick(T0.AddSeconds(s), MicSince(Edge, 100), [InCall]);
        Assert.Null(d.Tick(T0.AddSeconds(60), Mic(), [InCall]));
        for (int s = 61; s < 300; s++) Assert.Null(d.Tick(T0.AddSeconds(s), MicSince(Edge, 300), [InCall]));
        Assert.Equal(DetectorPhase.Recording, d.Phase);
    }

    [Fact]
    public void A_different_meeting_after_a_reopen_splits_even_without_the_join_screen()
    {
        var settings = new DetectionSettings { JoinScreenTitleContains = null }; // e.g. Teams in another language
        var d = new CallDetector(settings);
        d.Tick(T0, MicSince(Edge, 100), [InCall]);
        d.Tick(T0.AddSeconds(3), MicSince(Edge, 100), [InCall]);
        Assert.Null(d.Tick(T0.AddSeconds(60), MicSince(Edge, 300), [new WindowInfo("msedge", "YouTube - Personal - Microsoft Edge")]));
        var next = Assert.IsType<NextCall>(d.Tick(T0.AddSeconds(70), MicSince(Edge, 300), [new WindowInfo("msedge", "Kalender | Planung Q4 | Microsoft Teams")]));
        Assert.Contains("Planung Q4", next.WindowTitle);
        // Coming back to the same meeting much later, without a reopen, changes nothing.
        Assert.Null(d.Tick(T0.AddSeconds(400), MicSince(Edge, 300), [InCall]));
    }

    [Fact]
    public void Meet_in_the_same_browser_as_Teams_is_recognised_as_Meet()
    {
        var d = Detector();
        var meet = new WindowInfo("msedge", "Meet - abc-defg-hij and 4 more pages - Personal - Microsoft​ Edge");
        d.Tick(T0, Mic(Edge), [meet]);
        var ev = Assert.IsType<CallStarted>(d.Tick(T0.AddSeconds(3), Mic(Edge), [OtherTab, meet]));
        Assert.Equal("Google Meet (Edge)", ev.Source);
        Assert.Null(MeetingTitle.FromWindowTitle(ev.WindowTitle)); // the code is no name: the note gets a dated title
    }

    [Theory]
    [InlineData("Zoom and 2 more pages - Personal - Microsoft​ Edge", true)]
    [InlineData("Zoom Meeting - Personal - Microsoft​ Edge", true)]
    [InlineData("Launch Meeting - Zoom - Personal - Microsoft​ Edge", true)]
    [InlineData("How to zoom in on a chart - Personal - Microsoft​ Edge", false)]
    [InlineData("YouTube - Personal - Microsoft Edge", false)]
    public void Zoom_in_a_browser_is_recognised_by_its_tab(string title, bool zoom)
    {
        // Paul's Zoom call in Edge (2026-10-21) wasn't recorded: Zoom's only rule was its desktop app. The web client's tab
        // says "Zoom".
        var d = Detector();
        var tab = new WindowInfo("msedge", title);
        d.Tick(T0, Mic(Edge), [OtherTab, tab]);
        var ev = d.Tick(T0.AddSeconds(3), Mic(Edge), [OtherTab, tab]);
        if (!zoom)
        {
            Assert.Null(ev);
            return;
        }
        Assert.Equal("Zoom (Edge)", Assert.IsType<CallStarted>(ev).Source);
        Assert.Null(MeetingTitle.FromWindowTitle(Assert.IsType<CallStarted>(ev).WindowTitle)); // titled from its summary
    }

    [Theory]
    [InlineData(@"C:#Users#me#AppData#Roaming#Zoom#bin#Zoom.exe", "Zoom")]
    [InlineData(@"C:#Program Files#Zoom#bin#Zoom.exe", "Zoom")]
    [InlineData(@"C:#Users#me#AppData#Local#Programs#GoTo#GoTo.exe", "GoTo Meeting")]
    [InlineData(@"C:#Users#me#AppData#Local#GoToMeeting#19950#g2mcomm.exe", "GoTo Meeting (classic)")]
    public void Desktop_meeting_apps_start_on_the_mic_alone(string key, string source)
    {
        var d = Detector();
        d.Tick(T0, Mic(key), []);
        Assert.Equal(source, Assert.IsType<CallStarted>(d.Tick(T0.AddSeconds(3), Mic(key), [])).Source);
    }

    [Fact]
    public void Classic_rule_does_not_match_new_Teams_exe()
    {
        var rule = WatchRule.Defaults().Single(r => r.Name == "Teams (classic)");
        Assert.DoesNotContain(rule.AppKeyContains, @"C:#Program Files#WindowsApps#ms-teams.exe", StringComparison.OrdinalIgnoreCase);
        Assert.Contains(rule.AppKeyContains, @"C:#Users#me#AppData#Local#Microsoft#Teams#current#Teams.exe", StringComparison.OrdinalIgnoreCase);
    }
}

public class MeetingTitleTests
{
    [Theory]
    [InlineData("Weekly sync | Microsoft Teams and 11 more pages - Personal - Microsoft​ Edge", "Weekly sync")]
    [InlineData("Chat | Jon, Roy | Microsoft Teams", "Jon, Roy")]
    [InlineData("(3) Chat | Dana | Microsoft Teams", "Dana")]
    [InlineData("Calendar | Test Meeting | Microsoft Teams and 15 more pages - Personal - Microsoft​ Edge", "Test Meeting")]
    [InlineData("Calendar | Meeting join | Test Meeting | Microsoft Teams and 15 more pages - Personal - Microsoft​ Edge", "Test Meeting")]
    [InlineData("Calendar | Microsoft Teams", null)]
    [InlineData("Standup | Microsoft Teams", "Standup")]
    [InlineData("| Microsoft Teams", null)]
    [InlineData("YouTube - Microsoft Edge", null)]
    [InlineData(null, null)]
    public void Extracts_the_meeting_name(string? window, string? expected) =>
        Assert.Equal(expected, MeetingTitle.FromWindowTitle(window));
}

public class StaleEntryTests
{
    static readonly DateTime Now = new(2026, 10, 14, 15, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Recent_start_with_no_stop_is_in_use() =>
        Assert.True(RegistryMicUsageSource.IsInUse(Now.AddMinutes(-45).ToFileTimeUtc(), 0, Now));

    [Fact]
    public void A_start_from_long_ago_with_no_stop_is_a_crash_leftover() =>
        Assert.False(RegistryMicUsageSource.IsInUse(new DateTime(2024, 1, 26, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc(), 0, Now));

    [Fact]
    public void A_stopped_entry_is_not_in_use() =>
        Assert.False(RegistryMicUsageSource.IsInUse(Now.AddMinutes(-5).ToFileTimeUtc(), Now.AddMinutes(-1).ToFileTimeUtc(), Now));

    [Fact]
    public void Browser_must_actually_be_capturing_when_sessions_are_known()
    {
        var d = new CallDetector(new DetectionSettings());
        var t0 = DateTimeOffset.UtcNow;
        MicUsage[] edgeInUse = [new(@"C:#Program Files (x86)#Microsoft#Edge#Application#msedge.exe", true)];
        WindowInfo[] teams = [new("msedge", "Sync | Microsoft Teams")];
        var notCapturing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Discord" };
        var capturing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "msedge" };

        for (int s = 0; s < 10; s++) Assert.Null(d.Tick(t0.AddSeconds(s), edgeInUse, teams, notCapturing));
        Assert.Equal(DetectorPhase.Idle, d.Phase); // stale registry entry, Edge not capturing: ignored

        d.Tick(t0.AddSeconds(20), edgeInUse, teams, capturing);
        Assert.IsType<CallStarted>(d.Tick(t0.AddSeconds(23), edgeInUse, teams, capturing));

        // Edge crashes mid-call: registry still says in use, sessions don't → the call ends.
        Assert.Null(d.Tick(t0.AddSeconds(30), edgeInUse, [], notCapturing));
        Assert.IsType<CallEnded>(d.Tick(t0.AddSeconds(46), edgeInUse, [], notCapturing));
    }
}
