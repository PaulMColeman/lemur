using System.Diagnostics;
using Lemur.Config;
using Lemur.App.Windows;
using Lemur.Logging;
using Lemur.Pipeline;

namespace Lemur.App;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // The installer, before it replaces or removes the app: ask the running copy to finish up and exit.
        if (args.Contains("--quit"))
        {
            QuitRunningCopy();
            return;
        }
        // The installer, removing the app: take away what the app itself added (start at sign-in).
        if (args.Contains("--unregister"))
        {
            Autostart.Set(false);
            return;
        }

        // The Start menu's "Lemur Help" (the installer's shortcut runs "Lemur.exe --help"): the manual, and nothing else.
        // Before this, the shortcut started Lemur, or showed the Recordings window of the copy already running.
        if (args.Contains("--help"))
        {
            StartLinkCopy();
            HelpLink.Open();
            return;
        }

        // A "Name speakers" link from a meeting's page: just the naming window, beside the running tray app.
        if (NameLink.RecordingIn(args) is { } recording)
        {
            ShowNamingWindow(recording);
            return;
        }
        // A link to a meeting's note (a task board's card, the meeting file): its page in the browser, at the moment.
        if (NoteLink.In(args) is { } note)
        {
            StartLinkCopy();
            NoteLink.Open(note.Recording, note.Seconds);
            return;
        }

        // One recorder per Windows user session (a LEMUR_HOME copy for demos and tests has its own).
        using var instance = new Mutex(true, @"Local\Lemur.Tray" + AppPaths.InstanceSuffix, out bool first);
        if (!first)
        {
            // Started again (Start menu, desktop shortcut): the running one shows its Recordings window.
            if (EventWaitHandle.TryOpenExisting(TrayApp.ShowRecordingsEvent, out var show))
                using (show) show.Set();
            else
                MessageBox.Show("Lemur is already running — look for its icon in the system tray.", "Lemur");
            return;
        }

        // Before 1.0 Lemur was Teams Recorder: its folders and keys move over once, before anything creates Lemur's own.
        List<string> upgraded = [];
        if (LegacyMigration.Needed)
        {
            if (LegacyMigration.LegacyAppRunning)
            {
                MessageBox.Show("Teams Recorder, the earlier version of Lemur, is still running.\n\nRight-click its icon in the system tray, " +
                                "choose Quit, then start Lemur again. Lemur will bring over its settings, recordings and keys.", "Lemur",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            upgraded = LegacyMigration.Run();
        }

        Log.Init(AppPaths.LogFolder);
        foreach (var line in upgraded) Log.Info($"upgrade from Teams Recorder: {line}");
        bool firstRun = !File.Exists(AppPaths.SettingsFile);
        AppSettings settings;
        try
        {
            settings = SettingsStore.LoadOrCreate();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Settings could not be read:\n{ex.Message}\n\n{AppPaths.SettingsFile}", "Lemur",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) => Log.Error("UI thread exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("unhandled exception", e.ExceptionObject as Exception);

        if (firstRun && SetupWizard.Run(settings, new WindowsCredentialStore()) is { } chosen)
        {
            settings = chosen;
            Autostart.Set(SetupWizard.StartAtSignIn);
        }

        if (AppPaths.Home is null)
        {
            NameLink.Register();
            Autostart.MoveLegacy();
        }
        Log.Info($"Lemur {Application.ProductVersion} starting (settings: {AppPaths.SettingsFile})");
        Application.Run(new TrayApp(settings));
        Log.Info("Lemur stopped");
    }

    /// <summary>Signals the tray app to quit (finishing any recording), then waits up to 20 s for it to go.</summary>
    static void QuitRunningCopy()
    {
        if (!EventWaitHandle.TryOpenExisting(TrayApp.QuitEvent, out var quit)) return; // not running
        using (quit) quit.Set();
        foreach (var other in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "Lemur"))
        {
            using (other)
            {
                if (other.Id != Environment.ProcessId) other.WaitForExit(20_000);
            }
        }
    }

    /// <summary>A copy started by a link, beside the tray app: its log, and windows that look like the tray app's.</summary>
    static void StartLinkCopy()
    {
        Log.Init(AppPaths.LogFolder);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
    }

    static void ShowNamingWindow(string recording)
    {
        StartLinkCopy();
        try
        {
            var settings = SettingsStore.LoadOrCreate();
            var rec = RecordingFolder.ByName(settings.ResolvedRecordingsFolder, recording);
            if (rec is null)
            {
                MessageBox.Show($"There's no recording called '{recording}' on this PC.", "Lemur", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using var form = new NamingForm(settings, new WindowsCredentialStore(), rec);
            // The installer's --quit closes this window too; otherwise this copy would hold the files it replaces.
            using var quit = new EventWaitHandle(false, EventResetMode.ManualReset, TrayApp.QuitEvent);
            var wait = ThreadPool.RegisterWaitForSingleObject(quit, (_, _) =>
            {
                try { form.BeginInvoke(new Action(form.Close)); }
                catch (InvalidOperationException) { /* not shown yet, or already gone */ }
            }, null, -1, true);
            Application.Run(form);
            wait.Unregister(null);
        }
        catch (Exception ex)
        {
            Log.Error("naming window failed", ex);
            MessageBox.Show($"Couldn't open the naming window:\n{ex.Message}", "Lemur", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
