using System.Diagnostics;
using Microsoft.Win32;
using Lemur.Backends;
using Lemur.Config;
using Lemur.Detection;
using Lemur.Logging;
using Lemur.Pipeline;
using Lemur.App.Windows;
using Lemur.Speakers;

namespace Lemur.App;

/// <summary>
/// The whole UI: a tray icon. Once a second it asks Windows who holds the microphone; a Teams call
/// starts a recording, the end of the call stops it, and finished recordings are turned into notes
/// in the background.
/// </summary>
sealed class TrayApp : ApplicationContext
{
    readonly NotifyIcon _tray;
    readonly ContextMenuStrip _menu = new();
    readonly ToolStripMenuItem _status = new() { Enabled = false };
    readonly ToolStripMenuItem _startStop = new();
    readonly ToolStripMenuItem _discard = new("Discard this recording");
    readonly ToolStripMenuItem _next = new("Start a new recording (next meeting)");
    readonly ToolStripMenuItem _import = new("Import a recording…");
    readonly ToolStripMenuItem _pause = new();
    readonly ToolStripMenuItem _openLast = new("Open last meeting notes");
    readonly ToolStripMenuItem _nameLast = new("Name voices in last meeting…");
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    readonly IMicUsageSource _mic = new RegistryMicUsageSource();
    readonly IWindowSource _windows = new Win32WindowSource();
    readonly WasapiSessionSource _audio = new();
    readonly ISecretStore _secrets = new WindowsCredentialStore();
    readonly CancellationTokenSource _shutdown = new();
    readonly SynchronizationContext _ui;
    readonly ProcessingQueue _queue;

    /// <summary>Set by a second launch of the app (see Program): show the Recordings window.</summary>
    public static string ShowRecordingsEvent => @"Local\Lemur.ShowRecordings" + AppPaths.InstanceSuffix;
    readonly EventWaitHandle _showRequested = new(false, EventResetMode.AutoReset, ShowRecordingsEvent);
    readonly RegisteredWaitHandle _showWait;

    /// <summary>
    /// Set by "Lemur.exe --quit" (the installer, before an upgrade or uninstall): finish any recording and exit. Manual
    /// reset, so a naming window opened from a note page's link (its own process) closes too.
    /// </summary>
    public static string QuitEvent => @"Local\Lemur.Quit" + AppPaths.InstanceSuffix;
    readonly EventWaitHandle _quitRequested = new(false, EventResetMode.ManualReset, QuitEvent);
    readonly RegisteredWaitHandle _quitWait;

    AppSettings _settings;
    CallDetector _detector;
    RecordingSession? _session;
    DateTimeOffset? _pausedUntil;
    bool _processing;
    string? _lastNote;
    RecordingFolder? _lastRecording;
    Action? _balloonAction;

    /// <summary>Notes toasts that came in during a call; shown when it ends, so they don't pop up over the next meeting.</summary>
    readonly List<(string Title, string Text, ToolTipIcon Icon, Action? OnClick, Held Kind)> _held = [];

    enum Held { Ready, Failed, NotImported }

    public TrayApp(AppSettings settings)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _settings = settings;
        _settingsStamp = SettingsStamp();
        _detector = new CallDetector(settings.Detection);
        _queue = new ProcessingQueue(() => _settings,
            () => new MeetingProcessor(_settings, () => BackendFactory.Create(_settings, _secrets),
                SpeakerIdentifier.FromSettings(_settings, _secrets), SummaryModel),
            SummaryModel);
        _queue.Starting += (rec, state) => _ui.Post(_ => OnStarting(rec, state), null);
        _queue.Processed += (rec, state) => _ui.Post(_ => OnProcessed(rec, state), null);
        _queue.BusyChanged += busy => _ui.Post(_ => { _processing = busy; RefreshUi(); }, null);

        _startStop.Click += (_, _) => { if (_session is null) StartRecording("Manual", null, manual: true, PickManualDevices()); else StopRecording(userInitiated: true); };
        _discard.Click += (_, _) => Discard();
        _next.Click += (_, _) => NextMeeting();
        _import.Click += (_, _) =>
        {
            ShowRecordings();
            _recordings?.Import();
        };
        _pause.Click += (_, _) => TogglePause();
        _openLast.Click += (_, _) => { if (_lastRecording is not null) OpenNoteOf(_lastRecording, _lastNote); };
        _nameLast.Click += (_, _) => { if (_lastRecording is not null) ShowNaming(_lastRecording); };
        _menu.Items.AddRange([
            _status,
            new ToolStripSeparator(),
            _startStop,
            _next,
            _discard,
            _import,
            _pause,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Recordings…", null, (_, _) => ShowRecordings()) { Font = new Font(_menu.Font, FontStyle.Bold) },
            _openLast,
            _nameLast,
            new ToolStripMenuItem("Open notes folder", null, (_, _) => OpenFolder(_settings.NotesFolder)),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Settings…", null, (_, _) => ShowSettings()),
            new ToolStripMenuItem("Reload settings", null, (_, _) => ReloadSettings()),
            new ToolStripMenuItem("Open log folder", null, (_, _) => OpenFolder(AppPaths.LogFolder)),
            new ToolStripMenuItem("Help", null, (_, _) => HelpLink.Open()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Quit", null, (_, _) => Quit()),
        ]);

        _tray = new NotifyIcon { ContextMenuStrip = _menu, Icon = TrayIcons.Idle, Text = "Lemur", Visible = true };
        _tray.BalloonTipClicked += (_, _) => { var action = _balloonAction; _balloonAction = null; action?.Invoke(); };
        _tray.DoubleClick += (_, _) => ShowRecordings();

        SystemEvents.SessionEnding += OnSessionEnding;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
        _ = Task.Run(() => _audio.Endpoints()); // reads every device's names once (~1.3 s on Paul's PC), off the UI thread
        _ = Task.Run(() => _queue.RunAsync(_shutdown.Token));
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showRequested, (_, _) => _ui.Post(_ => ShowRecordings(), null), null, -1, false);
        _quitRequested.Reset(); // left set by an earlier quit if a naming window kept it open
        _quitWait = ThreadPool.RegisterWaitForSingleObject(_quitRequested, (_, _) => _ui.Post(_ =>
        {
            Log.Info("asked to quit (installer)");
            Quit();
        }, null), null, -1, true);

        var problems = settings.Problems();
        if (problems.Count > 0)
            Notify("Lemur needs setup",
                "Calls will be recorded, but notes can't be made until settings are filled in. Click to open settings.",
                ToolTipIcon.Warning, () => ShowSettings("Account"));
        RefreshUi();
    }

    void OnTick()
    {
        if (_pausedUntil is { } until && DateTimeOffset.Now >= until) SetPaused(false);
        if (++_importTick % 15 == 0) ScanImportFolder();

        if (_session is { Manual: true })
        {
            RefreshUi(); // manual recordings ignore call detection
            return;
        }

        DetectorEvent? ev;
        try
        {
            var mic = _mic.Read();
            bool anyInUse = mic.Any(m => m.InUse);
            // Window titles: which app's call a browser's mic use is, and during a call, whether it moved on to the next meeting.
            var windows = anyInUse ? _windows.Read() : [];
            // Live check that the browser really is capturing (a crash can leave the registry saying so forever).
            var capturing = anyInUse ? _audio.ActiveCaptureProcesses() : null;
            ev = _detector.Tick(DateTimeOffset.Now, mic, windows, capturing);
        }
        catch (Exception ex)
        {
            Log.Error("call detection failed", ex);
            return;
        }

        switch (ev)
        {
            case CallStarted started when _session is null:
                StartRecording(started.Source, MeetingTitle.FromWindowTitle(started.WindowTitle), manual: false,
                    PickDevices(started.Source));
                break;
            case CallEnded when _session is { Manual: false }:
                StopRecording(userInitiated: false);
                break;
            case NextCall next when _session is { Manual: false } current:
                Log.Info($"{current.Folder.Name}: the call moved straight on to the next meeting; starting a new recording");
                StopRecording(userInitiated: false, continuing: true);
                StartRecording(next.Source, MeetingTitle.FromWindowTitle(next.WindowTitle), manual: false, PickDevices(next.Source));
                break;
            case null when _session is { } s && ++_followTick % 5 == 0:
                FollowCallDevices(s);
                break;
        }
        RefreshUi();
    }

    int _followTick;

    /// <summary>The apps a manual recording follows (<see cref="CallDevicePicker.ManualRule"/>).</summary>
    WatchRule? _manualRule;

    static readonly string Self = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "Lemur";

    readonly ImportFolder _imports = new();
    Task? _importScan;
    int _importTick;
    string? _importProblemLogged;

    /// <summary>
    /// Recordings put in the import folder (settings.import.folder) become meetings waiting for notes. On a worker
    /// thread, one pass at a time: decoding an hour of audio takes a few seconds, and the folder may be on a network.
    /// </summary>
    void ScanImportFolder()
    {
        if (Ui.Blank(_settings.Import.Folder) is not { } folder || _importScan is { IsCompleted: false }) return;
        var root = _settings.ResolvedRecordingsFolder;
        if (RecordingImport.FolderProblem(folder, _settings.NotesFolder, root) is { } problem)
        {
            if (_importProblemLogged != folder) Log.Warn($"import folder not watched: {problem}");
            _importProblemLogged = folder;
            return;
        }
        var ct = _shutdown.Token;
        _importScan = Task.Run(() =>
        {
            try
            {
                var results = _imports.Scan(folder, root, ct);
                if (results.Count > 0) _ui.Post(_ => OnImported(folder, results), null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Log.Error("import folder pass failed", ex);
            }
        }, ct);
    }

    void OnImported(string folder, List<ImportFolder.Result> results)
    {
        if (results.Any(r => r is { Recording: not null, AlreadyImported: false })) _queue.Kick();
        _recordings?.RefreshRows();
        var failed = results.Where(r => r.Problem is not null).ToList();
        if (failed.Count == 0) return;
        var where = failed.All(r => r.MovedTo is not null) ? $" {(failed.Count == 1 ? "It's" : "They're")} in the \"{ImportFolder.FailedName}\" folder." : "";
        var text = failed.Count == 1
            ? $"{failed[0].FileName}: {failed[0].Problem}{where}"
            : $"{failed.Count} files couldn't be made into recordings.{where}";
        NotifyAfterCall("Couldn't import a recording", text, ToolTipIcon.Warning,
            () => OpenFolder(Path.Combine(folder, ImportFolder.FailedName)), Held.NotImported);
    }

    /// <summary>The devices the call app is really using, or the defaults if following is off or unknown.</summary>
    DevicePick PickDevices(string source)
    {
        var rule = _settings.Detection.Sources.FirstOrDefault(r => r.Name == source);
        if (rule is null || !_settings.Audio.FollowCallDevices) return DevicePick.Defaults;
        try
        {
            var pick = CallDevicePicker.Pick(rule, _audio.ActiveSessions(), _audio.Endpoints());
            Log.Info($"devices for {source}: mic='{pick.MicName ?? "default"}' output='{pick.OutputName ?? "default"}' ({pick.Reason})");
            return pick;
        }
        catch (Exception ex)
        {
            Log.Warn($"could not work out the call's devices; using defaults: {ex.Message}");
            return DevicePick.Defaults;
        }
    }

    /// <summary>Mid-call headset switch: move the recording with the call. A manual recording follows the app it started
    /// with (Paul's Zoom call of 2026-10-21 stayed on Stereo Mix after he set Zoom to his Jabra).</summary>
    void FollowCallDevices(RecordingSession session)
    {
        if (!_settings.Audio.FollowCallDevices) return;
        var sessions = _audio.ActiveSessions();
        var rule = session.Manual
            ? _manualRule = CallDevicePicker.ManualRule(_manualRule, sessions, Self)
            : _settings.Detection.Sources.FirstOrDefault(r => r.Name == session.Source);
        if (rule is null) return;
        var pick = CallDevicePicker.Pick(rule, sessions, _audio.Endpoints());
        if (pick.MicId is null) return; // e.g. the call app briefly closed its mic stream
        if (pick.MicId != session.MicDeviceId || (pick.OutputId is not null && pick.OutputId != session.OutputDeviceId))
            session.Follow(pick);
    }

    /// <summary>
    /// A manual recording has no rule saying which app the call is in, so it follows whichever app has a microphone
    /// open (Zoom, Meet, GoTo…) onto that mic and its headset; with none, the Windows defaults.
    /// </summary>
    DevicePick PickManualDevices()
    {
        _manualRule = null;
        if (!_settings.Audio.FollowCallDevices) return DevicePick.Defaults;
        try
        {
            var sessions = _audio.ActiveSessions();
            if ((_manualRule = CallDevicePicker.ManualRule(null, sessions, Self)) is not { } rule) return DevicePick.Defaults;
            var pick = CallDevicePicker.Pick(rule, sessions, _audio.Endpoints());
            Log.Info($"devices for a manual recording: mic='{pick.MicName ?? "default"}' output='{pick.OutputName ?? "default"}' " +
                     $"({pick.Reason}; {string.Join(", ", rule.AudioProcesses)} using a mic)");
            return pick;
        }
        catch (Exception ex)
        {
            Log.Warn($"could not work out which devices are in use; using defaults: {ex.Message}");
            return DevicePick.Defaults;
        }
    }

    /// <summary>"Start a new recording (next meeting)": ends this recording and begins another at once, keeping the call watch going.</summary>
    void NextMeeting()
    {
        if (_session is not { } current) return;
        string? title = null;
        DevicePick? devices;
        if (current.Manual)
        {
            devices = PickManualDevices();
        }
        else
        {
            devices = PickDevices(current.Source);
            var rule = _settings.Detection.Sources.FirstOrDefault(r => r.Name == current.Source);
            if (rule is not null && CallDetector.TryConfirm(rule, _windows.Read(), out var window)) title = MeetingTitle.FromWindowTitle(window);
        }
        Log.Info($"{current.Folder.Name}: split by the user (next meeting)");
        StopRecording(userInitiated: true, continuing: true);
        StartRecording(current.Source, title, current.Manual, devices);
    }

    void StartRecording(string source, string? title, bool manual, DevicePick? devices = null)
    {
        try
        {
            _session = RecordingSession.Start(_settings, source, title, manual, devices);
            var name = _session.Folder.Name;
            Notify(manual ? "Recording" : $"Recording your call ({source})",
                (string.IsNullOrWhiteSpace(title) ? "" : $"{title}. ") +
                "Let everyone know you're recording. Right-click the tray icon to stop.",
                ToolTipIcon.Info, () => ShowRecordings(name));
        }
        catch (Exception ex)
        {
            Log.Error("could not start recording", ex);
            Notify("Couldn't start recording", ex.Message, ToolTipIcon.Error, () => OpenFolder(AppPaths.LogFolder));
            if (!manual) _detector.Suppress();
        }
        RefreshUi();
    }

    /// <param name="continuing">A new recording follows at once (next meeting): leave call detection as it is.</param>
    void StopRecording(bool userInitiated, bool continuing = false)
    {
        var session = _session;
        if (session is null) return;
        _session = null;
        if (!continuing && userInitiated && !session.Manual) _detector.Suppress(); // don't restart during this same call
        if (!continuing && session.Manual) _detector.Reset();
        double minimum = _settings.Detection.MinAutoRecordingSeconds;
        Task.Run(() =>
        {
            try
            {
                if (!session.Manual && !userInitiated && session.Elapsed.TotalSeconds < minimum)
                {
                    session.Discard($"shorter than {minimum:0}s");
                    _ui.Post(_ => Notify("Recording not kept",
                        $"It was under {minimum:0} seconds. To keep a short call, stop the recording yourself from the tray menu.",
                        ToolTipIcon.Info, null), null);
                    return;
                }
                session.Stop();
                _queue.Kick();
            }
            catch (Exception ex)
            {
                Log.Error("stopping the recording failed", ex);
                _ui.Post(_ => Notify("Recording may be incomplete", ex.Message, ToolTipIcon.Warning, () => OpenFolder(AppPaths.LogFolder)), null);
            }
        });
        RefreshUi();
    }

    void Discard()
    {
        var session = _session;
        if (session is null) return;
        _session = null;
        if (!session.Manual) _detector.Suppress();
        Task.Run(() => session.Discard("discarded by user"));
        Notify("Recording discarded", "Nothing from this recording was kept.", ToolTipIcon.Info, null);
        RefreshUi();
    }

    void TogglePause() => SetPaused(_pausedUntil is null);

    void SetPaused(bool paused)
    {
        _pausedUntil = paused ? DateTimeOffset.Now.AddHours(1) : null;
        _detector.Paused = paused;
        RefreshUi();
    }

    void OnStarting(RecordingFolder rec, ProcessingState state)
    {
        if (state.Attempts > 0) return; // retries stay quiet: the first failure already said it would retry
        if (_session is not null) return; // in a call: "notes ready" will say it when the call is over
        string title, length, guess;
        try
        {
            var meta = rec.LoadMeta();
            title = meta.Title;
            length = $" ({Length(meta.DurationSeconds)})";
            guess = $"Should take {RecordingList.Roughly(StepEstimates.FromFolder(_settings.ResolvedRecordingsFolder).Total(meta.DurationSeconds))}";
        }
        catch { title = ""; length = ""; guess = "This can take a few minutes"; }
        Notify("Making meeting notes",
            $"{(string.IsNullOrWhiteSpace(title) ? "Your recording" : title)}{length}. {guess}; click to follow along.",
            ToolTipIcon.Info, () => ShowRecordings(rec.Name));
    }

    void OnProcessed(RecordingFolder rec, ProcessingState state)
    {
        if (state.Stage == Stage.Done && state.NotePath is not null)
        {
            _lastNote = state.NotePath;
            _lastRecording = rec;
            string title;
            try { title = rec.LoadMeta().Title; } catch { title = ""; }
            var what = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(state.NotePath) : title;

            // Voices nobody recognised: the toast invites naming them (design 7.1) — only when speaker
            // names are on, so people who haven't opted in aren't nagged.
            int unknown = 0;
            if (_settings.Speakers.Enabled)
            {
                try
                {
                    unknown = rec.Read<IdentifiedResult>(rec.IdentifiedFile)?.Speakers
                        .Count(s => !s.IsMe && s.Status is SpeakerStatus.Unknown or SpeakerStatus.Maybe or SpeakerStatus.Introduced) ?? 0;
                }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { }
            }
            if (unknown > 0)
                NotifyAfterCall("Meeting notes ready",
                    $"{what}. {unknown} {(unknown == 1 ? "voice wasn't" : "voices weren't")} recognised — click to name them.",
                    ToolTipIcon.Info, () => ShowNaming(rec), Held.Ready);
            else
                NotifyAfterCall("Meeting notes ready", what, ToolTipIcon.Info, () => OpenNoteOf(rec, state.NotePath), Held.Ready);
        }
        else if (state.LastError is not null && state.Attempts == 1)
        {
            NotifyAfterCall("Couldn't make meeting notes yet",
                $"Will retry automatically. {Shorten(state.LastError, 150)}",
                ToolTipIcon.Warning, () => ShowRecordings(rec.Name), Held.Failed);
        }
        RefreshUi();
    }

    /// <summary>A notes toast: now, or held until the call being recorded ends.</summary>
    void NotifyAfterCall(string title, string text, ToolTipIcon icon, Action? onClick, Held kind)
    {
        if (_session is null) Notify(title, text, icon, onClick);
        else _held.Add((title, text, icon, onClick, kind));
    }

    /// <summary>The call is over: what happened to the earlier meetings meanwhile, in one toast.</summary>
    void ShowHeld()
    {
        if (_held.Count == 0 || _session is not null) return;
        if (_held.Count == 1)
        {
            var (title, text, icon, onClick, _) = _held[0];
            Notify(title, text, icon, onClick);
        }
        else
        {
            int ready = _held.Count(h => h.Kind == Held.Ready), failed = _held.Count(h => h.Kind == Held.Failed);
            int notImported = _held.Count(h => h.Kind == Held.NotImported);
            var text = (ready > 0 ? $"Notes are ready for {ready} {(ready == 1 ? "meeting" : "meetings")}. " : "") +
                       (failed > 0 ? $"{failed} couldn't be made yet and will be retried. " : "") +
                       (notImported > 0 ? $"{notImported} {(notImported == 1 ? "recording" : "recordings")} couldn't be imported. " : "") +
                       "Click to see them.";
            Notify("While you were in a call", text, failed > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info, () => ShowRecordings());
        }
        _held.Clear();
    }

    RecordingsForm? _recordings;

    /// <summary>The Recordings window (double-click the tray icon); one at a time.</summary>
    /// <param name="select">The recording to show (a toast about it was clicked); null keeps the selection.</param>
    void ShowRecordings(string? select = null)
    {
        if (_recordings is not { IsDisposed: false })
        {
            _recordings = new RecordingsForm(() => _settings,
                () => _session is { } s ? new LiveRecording(s.Folder.Path, s.Title, s.Source, s.Elapsed, s.KeepRecording) : null,
                _queue, ShowNaming, path => Open(Readable(path)), () => ShowSettings(), SummaryModel, RememberWord, KeepLive);
            _recordings.FormClosed += (_, _) => _recordings = null;
            _recordings.Show();
        }
        if (_recordings.WindowState == FormWindowState.Minimized) _recordings.WindowState = FormWindowState.Normal;
        if (select is not null) _recordings.SelectRecording(select);
        _recordings.Activate();
    }

    /// <summary>Keep recording, ticked in the Recordings window for the call being recorded: it goes into meta.json when it stops.
    /// False if that recording isn't the one being made any more.</summary>
    bool KeepLive(string path, bool keep)
    {
        if (_session is not { } s || !string.Equals(s.Folder.Path, path, StringComparison.OrdinalIgnoreCase)) return false;
        s.KeepRecording = keep;
        return true;
    }

    SettingsForm? _settingsForm;

    /// <summary>The settings window; saving applies at once, like "Reload settings".</summary>
    void ShowSettings(string? tab = null)
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }
        // Changed since this copy was read (llmmr vocab add, a hand edit): read it again, so Save keeps those changes.
        if (SettingsStamp() != _settingsStamp)
        {
            try { Apply(SettingsStore.LoadOrCreate(), quiet: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        }
        using var form = _settingsForm = new SettingsForm(_settings, _secrets);
        if (tab is not null) form.SelectTab(tab);
        if (form.ShowDialog() == DialogResult.OK && form.Saved is { } saved) Apply(saved, quiet: true);
        _settingsForm = null;
    }

    /// <summary>"Fix a word…" with "fix it in every new meeting": the correction joins the vocabulary in settings.json.
    /// False (with a notification saying why) if it couldn't.</summary>
    bool RememberWord(string term, string heard)
    {
        try
        {
            // From the file, in case llmmr or a hand edit changed it since this copy was read.
            var settings = SettingsStore.LoadOrCreate();
            Transcripts.Vocabulary.Add(settings.Vocabulary, term, heard);
            if (Transcripts.Vocabulary.Problems(settings.Vocabulary).FirstOrDefault() is { } problem) throw new ArgumentException(problem);
            SettingsStore.Save(settings, AppPaths.SettingsFile);
            _settingsStamp = SettingsStamp();
            // Only the vocabulary changes here: applying the whole file would also reset call detection, and with it a
            // call the user stopped recording would start recording again.
            var updated = Ui.Copy(_settings);
            updated.Vocabulary = settings.Vocabulary;
            _settings = updated;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        {
            Log.Warn($"could not add a word to the vocabulary: {ex.GetType().Name}");
            Notify("Couldn't remember that word", $"Add it in Settings → Vocabulary. ({ex.Message})", ToolTipIcon.Warning, () => ShowSettings("Vocabulary"));
            return false;
        }
    }

    /// <summary>When settings.json last changed, as this copy knows it: a newer file (llmmr, a hand edit) is read again
    /// before the Settings window opens, so saving it doesn't throw those changes away.</summary>
    DateTime _settingsStamp;

    static DateTime SettingsStamp()
    {
        try { return File.GetLastWriteTimeUtc(AppPaths.SettingsFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return default; }
    }

    void ReloadSettings()
    {
        try
        {
            Apply(SettingsStore.LoadOrCreate(), quiet: false);
        }
        catch (Exception ex)
        {
            Notify("Settings not reloaded", ex.Message, ToolTipIcon.Error, () => Open(AppPaths.SettingsFile));
        }
        RefreshUi();
    }

    void Apply(AppSettings settings, bool quiet)
    {
        var before = _settings;
        _settings = settings;
        _settingsStamp = SettingsStamp();
        // A new detector forgets "you stopped recording this call": only when how calls are detected changed.
        if (_session is null && Json(before.Detection) != Json(settings.Detection))
            _detector = new CallDetector(_settings.Detection) { Paused = _pausedUntil is not null };
        var problems = _settings.Problems();
        if (!quiet || problems.Count > 0)
            Notify(quiet ? "Settings saved" : "Settings reloaded", problems.Count == 0 ? "All set." : string.Join("; ", problems),
                problems.Count == 0 ? ToolTipIcon.Info : ToolTipIcon.Warning, null);
        _queue.Kick();
        RefreshUi();
    }

    static string Json(DetectionSettings d) => System.Text.Json.JsonSerializer.Serialize(d, Config.Json.Options);

    void RefreshUi()
    {
        bool needsSetup = _settings.Problems().Count > 0;
        if (_session is { } s)
        {
            var elapsed = s.Elapsed;
            _status.Text = $"Recording {(int)elapsed.TotalMinutes}:{elapsed.Seconds:00} — {s.Source}";
            _tray.Icon = TrayIcons.Recording;
        }
        else if (_processing)
        {
            _status.Text = "Making meeting notes…";
            _tray.Icon = TrayIcons.Processing;
        }
        else if (_pausedUntil is { } until)
        {
            _status.Text = $"Auto-record paused until {until.ToLocalTime():t}";
            _tray.Icon = TrayIcons.Paused;
        }
        else
        {
            _status.Text = _detector.Phase == DetectorPhase.Suppressed
                ? "Not recording this call (you stopped it)"
                : "Watching for calls";
            _tray.Icon = needsSetup ? TrayIcons.Attention : TrayIcons.Idle;
        }
        if (_queue.Waiting > 0 && _session is null && !_processing) _status.Text += $" · {_queue.Waiting} waiting";
        if (needsSetup) _status.Text += " · needs setup";
        _tray.Text = Shorten("Lemur — " + _status.Text, 120);
        _startStop.Text = _session is null ? "Start recording now" : "Stop recording";
        _discard.Visible = _session is not null;
        _next.Visible = _session is not null;
        ShowHeld();
        _pause.Text = _pausedUntil is null ? "Pause auto-record for 1 hour" : "Resume auto-record";
        _openLast.Enabled = _lastNote is not null;
        _nameLast.Enabled = _lastRecording is not null;
    }

    void Notify(string title, string text, ToolTipIcon icon, Action? onClick)
    {
        _balloonAction = onClick;
        _tray.ShowBalloonTip(5000, title, string.IsNullOrWhiteSpace(text) ? " " : text, icon);
    }

    void OnSessionEnding(object? sender, SessionEndingEventArgs e) => FinishRecordingNow("Windows is signing out");

    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) FinishRecordingNow("the PC is going to sleep");
    }

    /// <summary>Synchronous stop, for when the process or the devices are about to go away.</summary>
    void FinishRecordingNow(string reason)
    {
        var session = _session;
        if (session is null) return;
        _session = null;
        _detector.Reset();
        try
        {
            Log.Info($"stopping recording: {reason}");
            session.Stop();
        }
        catch (Exception ex)
        {
            Log.Error("stopping recording failed", ex);
        }
    }

    void Quit()
    {
        FinishRecordingNow("quit");
        _shutdown.Cancel();
        _timer.Stop();
        SystemEvents.SessionEnding -= OnSessionEnding;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _tray.Visible = false;
        _tray.Dispose();
        _showWait.Unregister(null);
        _showRequested.Dispose();
        _quitWait.Unregister(null);
        _quitRequested.Dispose();
        ExitThread();
    }

    static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"could not open {path}: {ex.Message}"); }
    }

    NamingForm? _naming;

    /// <summary>One naming window at a time; a second request brings it forward.</summary>
    void ShowNaming(RecordingFolder rec)
    {
        if (_naming is { IsDisposed: false })
        {
            _naming.Activate();
            return;
        }
        try
        {
            _naming = new NamingForm(_settings, _secrets, rec);
            _naming.FormClosed += (_, _) => _naming = null;
            _naming.Show();
            _naming.Activate();
        }
        catch (Exception ex)
        {
            Log.Error("naming window failed", ex);
            Notify("Couldn't open the naming window", ex.Message, ToolTipIcon.Warning, () => OpenFolder(AppPaths.LogFolder));
        }
    }

    /// <summary>A meeting's note as it is now: renaming the meeting moves it, so the path is read again when asked.</summary>
    static void OpenNoteOf(RecordingFolder rec, string? fallback)
    {
        string? note;
        try { note = rec.LoadState().NotePath ?? fallback; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { note = fallback; }
        if (note is not null) Open(Readable(note));
    }

    /// <summary>The summary model (settings.summary), for titles and descriptions; null when it isn't set up.</summary>
    IChatModel? SummaryModel() => ChatModel.Create(_settings, _secrets, out _);

    /// <summary>The note's web page, or the Markdown for notes written before there were pages.</summary>
    static string Readable(string notePath) =>
        File.Exists(NoteWriter.HtmlPath(notePath)) ? NoteWriter.HtmlPath(notePath) : notePath;

    static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Open(path);
    }

    static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    static string Length(double seconds) => seconds < 60 ? $"{seconds:0} s" : $"{Math.Round(seconds / 60):0} min";
}
