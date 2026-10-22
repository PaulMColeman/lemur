using Lemur.Config;

namespace Lemur.App.Windows;

/// <summary>
/// The first-run wizard (design 7.4): Welcome → Account (setup file first) → Recording → Notes → People → Done.
/// The steps are the settings window's own pages. Test connection must pass before Finish, or the user
/// chooses "Finish anyway": the recorder records without a backend, it just can't make notes yet.
/// </summary>
public static class SetupWizard
{
    /// <summary>Whether the last run ended with "Start when I sign in" ticked (on by default). The caller sets up autostart.</summary>
    public static bool StartAtSignIn { get; private set; } = true;

    /// <summary>Shows the wizard. Returns the settings it saved, or null when the user left it.</summary>
    /// <param name="settingsPath">Where Finish writes; default %APPDATA%\Lemur\settings.json.</param>
    public static AppSettings? Run(AppSettings current, ISecretStore secrets, string? settingsPath = null)
    {
        using var form = new SetupWizardForm(current, secrets, settingsPath ?? AppPaths.SettingsFile);
        bool finished = form.ShowDialog() == DialogResult.OK;
        StartAtSignIn = form.StartAtSignIn;
        return finished ? form.Saved : null;
    }
}

sealed class SetupWizardForm : Form
{
    readonly PageHost _host;
    readonly List<SettingsPage> _steps;
    readonly DoneStep _done;
    readonly Label _stepNo = new() { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText };
    readonly Label _title = new() { AutoSize = true, Anchor = AnchorStyles.Left, Font = Ui.TitleFont, Margin = new Padding(0, 2, 3, 2) };
    readonly Label _intro = new() { AutoSize = true, Anchor = Ui.Wide, ForeColor = SystemColors.GrayText };
    readonly Button _back, _next, _cancel;
    int _index = -1;
    bool _finished;

    public SetupWizardForm(AppSettings current, ISecretStore secrets, string settingsPath)
    {
        _host = new PageHost(current, secrets, wizard: true, settingsPath);

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = Ui.BaseFont;
        Text = "Set up Lemur";
        Icon = Ui.AppIcon;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(760, 600);
        MinimumSize = new Size(720, 560);
        MaximizeBox = false;

        _done = new DoneStep(_host);
        _steps = [new WelcomeStep(_host), new AccountPage(_host), new RecordingPage(_host), new NotesPage(_host), new PeoplePage(_host), _done];
        _host.Pages.AddRange(_steps);

        // Every step is in the window from the start (hidden) so DPI scaling reaches them all.
        var content = new Panel { Dock = DockStyle.Fill };
        foreach (var step in _steps)
        {
            step.Visible = false;
            content.Controls.Add(step);
        }

        var header = new VStack { BackColor = SystemColors.Window, Padding = new Padding(20, 12, 20, 12) };
        header.Add(_stepNo);
        header.Add(_title);
        header.Add(_intro);
        var rule = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 1, BackColor = SystemColors.ControlDark };

        _back = new Button { Text = "< Back", AutoSize = true, UseVisualStyleBackColor = true };
        _next = new Button { Text = "Next >", AutoSize = true, UseVisualStyleBackColor = true };
        _cancel = new Button { Text = "Cancel", AutoSize = true, UseVisualStyleBackColor = true, DialogResult = DialogResult.Cancel };
        _back.Click += (_, _) => { if (_index > 0) GoTo(_index - 1); };
        _next.Click += (_, _) => Next();
        var right = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0), WrapContents = false };
        right.Controls.Add(_back);
        right.Controls.Add(_next);
        var bar = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, RowCount = 1, Padding = new Padding(10, 8, 10, 10) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var left = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0), WrapContents = false };
        left.Controls.Add(_cancel);
        var help = Ui.Link("Help", () => HelpLink.Open(section: "setup", owner: this));
        help.Margin = new Padding(12, 9, 3, 3);
        left.Controls.Add(help);
        bar.Controls.Add(left, 0, 0);
        bar.Controls.Add(right, 1, 0);

        // Docking runs from the last control added to the first: bar, header, rule, then content fills the rest.
        Controls.Add(content);
        Controls.Add(rule);
        Controls.Add(header);
        Controls.Add(bar);
        AcceptButton = _next;
        CancelButton = _cancel;
        ResumeLayout(false);
        PerformLayout();

        _host.Reload();
        _done.StateChanged += UpdateButtons;
        GoTo(0);
    }

    public AppSettings? Saved { get; private set; }

    public bool StartAtSignIn => _done.StartAtSignIn;

    internal int StepCount => _steps.Count;

    internal DoneStep Done => _done;

    internal void GoTo(int index)
    {
        if (_index == _steps.Count - 1 && index != _index) _done.CancelTest();
        for (int i = 0; i < _steps.Count; i++) _steps[i].Visible = i == index;
        _index = index;
        var step = _steps[index];
        _stepNo.Text = $"Step {index + 1} of {_steps.Count}";
        _title.Text = step.Title;
        _intro.Text = step.Intro;
        _intro.Visible = step.Intro.Length > 0;
        UpdateButtons();
        step.Entered();
        step.SelectNextControl(step, true, true, true, true);
    }

    void UpdateButtons()
    {
        bool last = _index == _steps.Count - 1;
        _back.Enabled = _index > 0;
        _next.Text = !last ? "Next >" : _done.State == TestState.Failed ? "Finish anyway" : "Finish";
        _next.Enabled = !last || _done.State != TestState.Running;
    }

    void Next()
    {
        if (_index == _steps.Count - 1)
        {
            Finish();
            return;
        }
        var errors = new List<string>();
        _steps[_index].Check(errors);
        if (errors.Count > 0)
        {
            Ui.Warn(this, "Please fix this first:\n\n• " + string.Join("\n• ", errors));
            return;
        }
        GoTo(_index + 1);
    }

    void Finish()
    {
        if (_done.State == TestState.Running) return;
        if (_done.State != TestState.Passed && !Ui.Confirm(this,
                "The connection test didn't pass.\n\nLemur will still record your calls, but it can't make notes until the " +
                "connection works. You can fix it later in Settings.\n\nFinish anyway?", warning: true))
            return;

        var settings = _host.Flush();
        var errors = new List<string>();
        if (_host.Check(errors) is { } page)
        {
            GoTo(_steps.IndexOf(page));
            Ui.Warn(this, "Please fix this first:\n\n• " + string.Join("\n• ", errors));
            return;
        }
        if (!NotesPage.ConfirmMp3s(this, null, settings)) // recordings already there, from a copy set up before
        {
            GoTo(_steps.FindIndex(s => s is NotesPage));
            return;
        }
        try
        {
            SettingsStore.Save(settings, _host.SettingsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Error(this, $"Settings couldn't be saved: {ex.Message}");
            return;
        }
        Saved = settings;
        _finished = true;
        DialogResult = DialogResult.OK;
        if (!Modal) Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_finished && _index > 0 && e.CloseReason is CloseReason.UserClosing or CloseReason.None
            && !Ui.Confirm(this, "Leave setup? Your settings won't be saved. (Keys you saved stay in Credential Manager.)"))
            e.Cancel = true;
        base.OnFormClosing(e);
    }
}
