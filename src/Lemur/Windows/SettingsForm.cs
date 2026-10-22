using Lemur.Config;
using Lemur.Pipeline;
using Lemur.Transcripts;

namespace Lemur.App.Windows;

/// <summary>
/// The settings window (design 7.2). It edits a copy: <b>Save</b> checks it, writes settings.json and sets
/// <see cref="Saved"/> (DialogResult.OK); <b>Cancel</b> throws the copy away. Keys and people are the exception —
/// they're saved the moment they change, because they live in Credential Manager and the people folder.
/// </summary>
public sealed class SettingsForm : Form
{
    readonly PageHost _host;
    readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    readonly string _baseline;
    readonly AppSettings _before;
    readonly List<VocabularyEntry> _vocabularyBefore;

    /// <param name="settingsPath">Where Save writes; default %APPDATA%\Lemur\settings.json.</param>
    public SettingsForm(AppSettings settings, ISecretStore secrets, string? settingsPath = null)
    {
        _host = new PageHost(settings, secrets, wizard: false, settingsPath ?? AppPaths.SettingsFile);
        _before = Ui.Copy(settings);
        _vocabularyBefore = Ui.Copy(settings).Vocabulary;

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = Ui.BaseFont;
        Text = "Lemur settings";
        Icon = Ui.AppIcon;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(800, 640);
        MinimumSize = new Size(720, 560);
        MinimizeBox = false;

        _host.Pages.AddRange([new AccountPage(_host), new RecordingPage(_host), new NotesPage(_host), new VocabularyPage(_host), new PeoplePage(_host),
            new AboutPage(_host)]);
        foreach (var page in _host.Pages)
        {
            // A plain white page: the themed tab background makes child panels paint grey.
            var tab = new TabPage(page.Title) { UseVisualStyleBackColor = false, BackColor = SystemColors.Window, Padding = new Padding(0) };
            tab.Controls.Add(page);
            _tabs.TabPages.Add(tab);
        }
        _tabs.SelectedIndexChanged += (_, _) => Current?.Entered();

        var save = new Button { Text = "Save", AutoSize = true, UseVisualStyleBackColor = true };
        save.Click += (_, _) => SaveAndClose();
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel, UseVisualStyleBackColor = true };
        var buttons = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0), WrapContents = false };
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        var bar = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, RowCount = 1, Padding = new Padding(10, 8, 10, 10) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var links = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0), WrapContents = false };
        links.Controls.Add(Ui.Link("Help", () => HelpLink.Open(section: "settings-" + (Current?.Title.ToLowerInvariant() ?? "account"), owner: this)));
        links.Controls.Add(Ui.Link("Open settings file", () => Ui.Shell(_host.SettingsPath)));
        bar.Controls.Add(links, 0, 0);
        bar.Controls.Add(buttons, 1, 0);

        Controls.Add(_tabs);
        Controls.Add(bar);
        AcceptButton = save;
        CancelButton = cancel;
        ResumeLayout(false);
        PerformLayout();

        // After scaling, so sizes the pages work out from fonts aren't scaled twice.
        _host.Reload();
        _baseline = Ui.ToJson(_host.Flush());
    }

    /// <summary>The settings as saved, once Save succeeded (DialogResult.OK); null otherwise.</summary>
    public AppSettings? Saved { get; private set; }

    /// <summary>Opens on a tab by its title: Account, Recording, Notes, Vocabulary, People or About.</summary>
    public void SelectTab(string title)
    {
        int i = _host.Pages.FindIndex(p => p.Title.Equals(title, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) _tabs.SelectedIndex = i;
    }

    internal IReadOnlyList<SettingsPage> Pages => _host.Pages;

    SettingsPage? Current => _tabs.SelectedIndex >= 0 ? _host.Pages[_tabs.SelectedIndex] : null;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Current?.Entered();
    }

    void SaveAndClose()
    {
        var edited = _host.Flush();
        var errors = new List<string>();
        if (_host.Check(errors) is { } page)
        {
            _tabs.SelectedIndex = _host.Pages.IndexOf(page);
            Ui.Warn(this, "Please fix this first:\n\n• " + string.Join("\n• ", errors));
            return;
        }
        var problems = edited.Problems();
        if (problems.Count > 0 && !Ui.Confirm(this,
                "Calls will still be recorded, but notes can't be made until this is filled in:\n\n• " +
                string.Join("\n• ", problems) + "\n\nSave anyway?", warning: true))
            return;
        if (!NotesPage.ConfirmMp3s(this, _before, edited))
        {
            if (_host.Pages.OfType<NotesPage>().FirstOrDefault() is { } notes) _tabs.SelectedIndex = _host.Pages.IndexOf(notes);
            return;
        }
        try
        {
            SettingsStore.Save(edited, _host.SettingsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Error(this, $"Settings couldn't be saved: {ex.Message}");
            return;
        }
        if (_host.Pages.OfType<RecordingPage>().FirstOrDefault() is { } recording && recording.StartAtSignIn != Autostart.IsOn)
            Autostart.Set(recording.StartAtSignIn);
        OfferFixes(edited);
        Saved = edited;
        DialogResult = DialogResult.OK;
        if (!Modal) Close();
    }

    /// <summary>Words added to the vocabulary: offers to fix them in the meetings already made that have them misheard.</summary>
    void OfferFixes(AppSettings saved)
    {
        var added = Vocabulary.Added(_vocabularyBefore, saved.Vocabulary);
        if (added.Count == 0) return;
        var vocabulary = new Vocabulary(added);
        List<RecordingFolder> meetings;
        using (new WaitCursor(this))
            meetings = [.. RecordingFolder.All(saved.ResolvedRecordingsFolder).Where(r => MeetingFixes.Needs(r, vocabulary))];
        if (meetings.Count == 0) return;
        var what = added.Count == 1 && added[0].HeardAs.Count == 1 ? $"\"{added[0].HeardAs[0]}\" is" : "Words you added are";
        if (!Ui.Confirm(this, $"{what} misheard in {meetings.Count} {(meetings.Count == 1 ? "meeting" : "meetings")} already made. " +
                              "Fix them in those notes too?")) return;
        (int done, int failed) result;
        using (new WaitCursor(this))
            result = MeetingFixes.ApplyAll(saved.ResolvedRecordingsFolder, vocabulary, saved.DisplayName, meetings, saved.MeetingFile.IncludeTranscript);
        if (result.failed > 0)
            Ui.Warn(this, $"Fixed in {result.done} {(result.done == 1 ? "meeting" : "meetings")}. {result.failed} couldn't be changed: " +
                          "a note may be open in another program. Fix a word… in the Recordings window tries again.");
    }

    /// <summary>The hourglass while a few hundred files are read.</summary>
    sealed class WaitCursor : IDisposable
    {
        readonly Form _form;

        public WaitCursor(Form form)
        {
            _form = form;
            _form.UseWaitCursor = true;
            Cursor.Current = Cursors.WaitCursor;
        }

        public void Dispose()
        {
            _form.UseWaitCursor = false;
            Cursor.Current = Cursors.Default;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (DialogResult != DialogResult.OK && e.CloseReason is CloseReason.UserClosing or CloseReason.None
            && Ui.ToJson(_host.Flush()) != _baseline
            && !Ui.Confirm(this, "Discard your changes to settings?"))
            e.Cancel = true;
        base.OnFormClosing(e);
    }
}
