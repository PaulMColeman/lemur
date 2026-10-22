using Lemur.Config;

namespace Lemur.App.Windows;

/// <summary>The wizard's first step: what the recorder does, and the reminder to tell people.</summary>
sealed class WelcomeStep : SettingsPage
{
    public WelcomeStep(PageHost host) : base(host, "Welcome to Lemur", "Setting it up takes a couple of minutes.")
    {
        Body.Add(Paragraph(
            "Lemur sits in the system tray and records your calls by itself (Teams, Google Meet, Zoom, GoTo Meeting): your microphone and the call's sound, " +
            "as two tracks. When the call ends, it sends the recording to your transcription server and writes a note with a summary, " +
            "decisions and action items into your notes folder. You can record in-person meetings too, from the tray menu."));
        Body.Add(Ui.Callout(SystemColors.Info,
            Ui.Bold("Let everyone know you're recording."),
            Ui.Text("Recording people without telling them can be against the law where you or they are. " +
                    "Each time a recording starts, Lemur reminds you to tell everyone.")));
        Body.Add(Paragraph("You'll need your server's address, or a setup file from whoever runs it, and your own key."));
    }

    static Label Paragraph(string text)
    {
        var label = Ui.Text(text);
        label.Margin = new Padding(3, 0, 3, 12);
        return label;
    }

    public override void LoadFrom(AppSettings s) { }

    public override void SaveTo(AppSettings s) { }
}

enum TestState { NotRun, Running, Passed, Failed }

/// <summary>The last step: test the servers that are switched on, and "Start when I sign in".</summary>
sealed class DoneStep : SettingsPage
{
    readonly CheckList _results = new();
    readonly Button _again;
    readonly Label _warning;
    readonly CheckBox _startAtSignIn = Ui.Check("Start Lemur when I sign in to Windows");
    CancellationTokenSource? _testing;
    TestState _state;

    public DoneStep(PageHost host) : base(host, "Ready", "Last, a check that your servers answer.")
    {
        _startAtSignIn.Checked = true;
        _again = Ui.Button("Test again", () => _ = RunTest());
        _warning = new Label
        {
            AutoSize = true, Anchor = Ui.Wide, ForeColor = Ui.Bad, Visible = false, Margin = new Padding(3, 4, 3, 8),
            Text = "The connection test didn't pass. Lemur still records your calls, but can't make notes until it works. " +
                   "Go Back to fix it, or finish anyway and fix it later in Settings.",
        };

        var g = new Grid();
        g.Heading("Connection");
        g.Row(null, _results);
        g.Row(null, _again);
        g.Row(null, _warning);
        g.Heading("Starting");
        g.Row(null, _startAtSignIn,
            "Lemur lives in the system tray, by the clock. Right-click its icon to record, pause or open Settings.");
        Body.Add(g);
    }

    public TestState State => _state;

    public bool StartAtSignIn => _startAtSignIn.Checked;

    public event Action? StateChanged;

    public override void LoadFrom(AppSettings s) { }

    public override void SaveTo(AppSettings s) { }

    public override void Entered() => _ = RunTest();

    public void CancelTest()
    {
        _testing?.Cancel();
        _results.Clear();
        _warning.Visible = false;
        _again.Enabled = true;
        Set(TestState.NotRun);
    }

    async Task RunTest()
    {
        _testing?.Cancel();
        var cts = _testing = new CancellationTokenSource();
        var snapshot = Ui.Copy(Host.Flush());
        _warning.Visible = false;
        _again.Enabled = false;
        _results.Running();
        Set(TestState.Running);
        try
        {
            var results = await CheckList.RunAsync(snapshot, Host.Secrets, cts.Token);
            if (IsDisposed || cts.IsCancellationRequested) return;
            _results.ShowResults(results);
            bool ok = results.All(r => r.Ok);
            _warning.Visible = !ok;
            Set(ok ? TestState.Passed : TestState.Failed);
        }
        catch (OperationCanceledException)
        {
            // left the step, or tested again
        }
        finally
        {
            if (!IsDisposed && _testing == cts) _again.Enabled = true;
        }
    }

    void Set(TestState state)
    {
        _state = state;
        StateChanged?.Invoke();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _testing?.Cancel();
        base.Dispose(disposing);
    }
}
