using System.Text.RegularExpressions;
using Lemur.Config;

namespace Lemur.Detection;

public abstract record DetectorEvent;

/// <param name="Source">The <see cref="WatchRule.Name"/> that fired.</param>
/// <param name="WindowTitle">Raw title of the window that confirmed it, if any (used to name the meeting).</param>
public sealed record CallStarted(string Source, string? WindowTitle) : DetectorEvent;

public sealed record CallEnded(string Source) : DetectorEvent;

/// <summary>
/// The call app went straight from one meeting into the next (left, and joined another within the stop delay):
/// the recording should end here and a new one begin. <paramref name="WindowTitle"/> names the new meeting.
/// </summary>
public sealed record NextCall(string Source, string? WindowTitle) : DetectorEvent;

public enum DetectorPhase
{
    Idle,
    /// <summary>A watched app has the mic; waiting out the start delay and/or for a window that says which app's call it is.</summary>
    Pending,
    Recording,
    /// <summary>The user stopped an automatic recording; ignore that source until it lets go of the mic.</summary>
    Suppressed,
}

/// <summary>
/// Turns once-a-second snapshots of "who has the mic" and "which windows exist" into
/// start/stop events. Pure logic — no clock, registry or UI — so every timing rule is testable.
/// </summary>
public sealed class CallDetector
{
    readonly DetectionSettings _settings;
    WatchRule? _rule;
    DateTimeOffset _since;

    /// <summary>A mic release and re-take this recent may be the start of the next meeting.</summary>
    const double ReopenWindowSeconds = 120;

    long _micStarted;            // when the call app last opened the mic, to notice a re-open between ticks
    bool _gap;                   // a tick without the mic since it was last seen
    DateTimeOffset? _reopenedAt; // the mic was released and taken again: the next meeting, or just a device change
    string? _meeting;            // the meeting the call window showed while the mic was held (MeetingTitle.Key)

    public CallDetector(DetectionSettings settings) => _settings = settings;

    public DetectorPhase Phase { get; private set; } = DetectorPhase.Idle;
    public string? ActiveSource => _rule?.Name;

    /// <summary>When true no new recording starts; one already running is left alone.</summary>
    public bool Paused { get; set; }

    /// <param name="activeCaptureProcesses">
    /// Processes Windows says are capturing audio right now, or null if unknown. Browser rules
    /// (which name a <see cref="WatchRule.WindowProcess"/>) require their process to be in it, so a
    /// registry entry left "in use" by a crash can't hold a recording open.
    /// </param>
    public DetectorEvent? Tick(DateTimeOffset now, IReadOnlyList<MicUsage> mic, IReadOnlyList<WindowInfo> windows,
        IReadOnlySet<string>? activeCaptureProcesses = null)
    {
        _capturing = activeCaptureProcesses;
        switch (Phase)
        {
            case DetectorPhase.Idle:
                if (Paused || !_settings.Enabled || !RulesInUse(mic).Any()) return null;
                _since = now;
                Phase = DetectorPhase.Pending;
                return TryStart(now, mic, windows);

            case DetectorPhase.Pending:
                if (Paused || !_settings.Enabled || !RulesInUse(mic).Any())
                {
                    Reset();
                    return null;
                }
                return TryStart(now, mic, windows);

            case DetectorPhase.Recording:
                if (!InUse(_rule!, mic))
                {
                    _gap = true;
                    if ((now - _since).TotalSeconds < _settings.StopAfterSeconds) return null;
                    var ended = new CallEnded(_rule!.Name);
                    Reset();
                    return ended;
                }
                _since = now; // last moment we saw the call alive
                long started = MicStarted(_rule!, mic);
                if (_gap || (started != 0 && _micStarted != 0 && started != _micStarted)) _reopenedAt = now;
                _gap = false;
                _micStarted = started;
                return NextMeeting(now, windows);

            case DetectorPhase.Suppressed:
                if (!InUse(_rule!, mic)) Reset();
                return null;
        }
        return null;
    }

    /// <summary>The user stopped or discarded the automatic recording: don't restart it during this call.</summary>
    public void Suppress()
    {
        if (_rule is not null) Phase = DetectorPhase.Suppressed;
    }

    public void Reset()
    {
        Phase = DetectorPhase.Idle;
        _rule = null;
    }

    /// <summary>The first rule whose app has the mic and, if it needs one, its window: browsers serve several apps from one mic entry.</summary>
    DetectorEvent? TryStart(DateTimeOffset now, IReadOnlyList<MicUsage> mic, IReadOnlyList<WindowInfo> windows)
    {
        if ((now - _since).TotalSeconds < _settings.StartAfterSeconds) return null;
        foreach (var rule in RulesInUse(mic))
        {
            if (!TryConfirm(rule, windows, out var title)) continue; // browser mic in use, but not this app's window (yet)
            _rule = rule;
            Phase = DetectorPhase.Recording;
            _since = now;
            _micStarted = MicStarted(rule, mic);
            _gap = false;
            _reopenedAt = null;
            _meeting = MeetingTitle.Key(title);
            return new CallStarted(rule.Name, title);
        }
        return null;
    }

    /// <summary>
    /// Back-to-back meetings: Teams' join screen takes the mic a second or two after the last meeting let it go,
    /// well inside the stop delay. So after a release and re-take, the call window showing the join screen or a
    /// different meeting ends this recording and begins the next. A device change also re-takes the mic, but
    /// leaves the window on the same meeting. Joining the same meeting again counts as a new one (it shows the
    /// join screen): two notes for one meeting beat one note mixing two.
    /// </summary>
    DetectorEvent? NextMeeting(DateTimeOffset now, IReadOnlyList<WindowInfo> windows)
    {
        if (!TryConfirm(_rule!, windows, out var title) || MeetingTitle.Key(title) is not { } meeting)
            return null; // another tab in front, or an app that doesn't name its meetings
        bool joinScreen = !string.IsNullOrWhiteSpace(_settings.JoinScreenTitleContains) &&
                          title!.Contains(_settings.JoinScreenTitleContains, StringComparison.OrdinalIgnoreCase);
        if (_reopenedAt is { } at && (now - at).TotalSeconds <= ReopenWindowSeconds && _meeting is not null &&
            (joinScreen || !string.Equals(meeting, _meeting, StringComparison.OrdinalIgnoreCase)))
        {
            _reopenedAt = null;
            _meeting = meeting;
            return new NextCall(_rule!.Name, title);
        }
        _meeting = meeting;
        return null;
    }

    IEnumerable<WatchRule> RulesInUse(IReadOnlyList<MicUsage> mic) => _settings.Sources.Where(r => r.Enabled && InUse(r, mic));

    static bool MatchesKey(WatchRule rule, MicUsage m) =>
        !string.IsNullOrWhiteSpace(rule.AppKeyContains) && m.AppKey.Contains(rule.AppKeyContains, StringComparison.OrdinalIgnoreCase);

    static long MicStarted(WatchRule rule, IReadOnlyList<MicUsage> mic) =>
        mic.Where(m => m.InUse && MatchesKey(rule, m)).Select(m => m.StartedFileTime).DefaultIfEmpty(0).Max();

    IReadOnlySet<string>? _capturing;

    bool InUse(WatchRule rule, IReadOnlyList<MicUsage> mic) =>
        mic.Any(m => m.InUse && MatchesKey(rule, m)) &&
        (_capturing is null || string.IsNullOrWhiteSpace(rule.WindowProcess) || _capturing.Contains(rule.WindowProcess));

    /// <summary>
    /// Browser rules need a matching window; app rules are confirmed by the mic alone and only
    /// look for a "… | Microsoft Teams" window to name the meeting.
    /// </summary>
    public static bool TryConfirm(WatchRule rule, IReadOnlyList<WindowInfo> windows, out string? title)
    {
        if (rule.NeedsWindow)
        {
            foreach (var w in windows)
            {
                if (IsWindowOf(rule, w))
                {
                    title = w.Title;
                    return true;
                }
            }
            title = null;
            return false;
        }

        title = windows.FirstOrDefault(w => w.Title.Contains("Microsoft Teams", StringComparison.OrdinalIgnoreCase)).Title;
        return true;
    }

    /// <summary>A window that shows this rule's app (browser rules): the right process and title.</summary>
    public static bool IsWindowOf(WatchRule rule, WindowInfo w) =>
        (string.IsNullOrWhiteSpace(rule.WindowProcess) || string.Equals(w.ProcessName, rule.WindowProcess, StringComparison.OrdinalIgnoreCase)) &&
        (string.IsNullOrWhiteSpace(rule.WindowTitleContains) || w.Title.Contains(rule.WindowTitleContains, StringComparison.OrdinalIgnoreCase)) &&
        (string.IsNullOrWhiteSpace(rule.WindowTitleMatches) || TitleMatches(rule.WindowTitleMatches, w.Title));

    static bool TitleMatches(string pattern, string title)
    {
        try { return Regex.IsMatch(title, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)); }
        catch (ArgumentException) { return false; } // a broken pattern in settings matches nothing
        catch (RegexMatchTimeoutException) { return false; }
    }
}
