using Lemur.Logging;
using Lemur.Pipeline;
using Lemur.Speakers;

namespace Lemur.App.Windows;

/// <summary>
/// "Rename meeting": a new title for a meeting and its note. Offers the title the summary model suggested; for a
/// meeting summarized before there were suggestions, "Suggest a title" asks the model from the meeting's summary.
/// </summary>
sealed class RenameDialog : Form
{
    readonly RecordingFolder _rec;
    readonly RecordingMeta _meta;
    readonly Func<IChatModel?> _chat;
    readonly TextBox _title = new() { Anchor = Ui.Wide, MaxLength = MeetingAbout.MaxTypedTitle };
    readonly Label _files = Ui.Muted("");
    readonly Label _status = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Anchor = AnchorStyles.Left, MaximumSize = new Size(480, 0), Margin = new Padding(3, 2, 3, 0) };
    readonly LinkLabel _use = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 3, 3, 3) };
    readonly Button _suggest;
    readonly Button _ok = new() { Text = "Rename", AutoSize = true, DialogResult = DialogResult.OK };
    readonly string? _note;
    CancellationTokenSource? _asking;

    /// <summary>The title chosen, once the dialog closes with OK.</summary>
    public string? ChosenTitle { get; private set; }

    /// <param name="chat">The summary model, for "Suggest a title"; returns null when there isn't one.</param>
    public RenameDialog(RecordingFolder rec, RecordingMeta meta, string? notePath, Func<IChatModel?> chat)
    {
        _rec = rec;
        _meta = meta;
        _chat = chat;
        _note = notePath is not null && File.Exists(notePath) ? notePath : null;

        Text = "Rename meeting — Lemur";
        Icon = Ui.AppIcon;
        Font = SystemFonts.MessageBoxFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var when = meta.StartedAt.ToLocalTime();
        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(16, 14, 16, 8), Dock = DockStyle.Fill };
        grid.Controls.Add(new Label
        {
            AutoSize = true, Font = Ui.HeadingFont, Margin = new Padding(3, 0, 3, 2),
            Text = MeetingAbout.NeedsTitle(meta) ? "Give this meeting a title" : "Rename this meeting",
        });
        grid.Controls.Add(Ui.Muted($"{when:dddd d MMMM yyyy, HH:mm} · {meta.Source}"));
        _title.Text = MeetingAbout.NeedsTitle(meta) ? meta.SuggestedTitle ?? "" : meta.Title;
        _title.Width = 460;
        _title.TextChanged += (_, _) => Changed();
        grid.Controls.Add(_title);

        // The model's suggestion, when it made one and it isn't the title already.
        _use.LinkClicked += (_, _) =>
        {
            if (_use.Tag is string s) _title.Text = s;
            _title.Focus();
            _title.SelectAll();
        };
        _suggest = Ui.Button("Suggest a title", () => _ = SuggestAsync());
        grid.Controls.Add(Ui.Line(_use, _suggest));
        grid.Controls.Add(_status); // a line of its own, so the dialog doesn't change width while it asks
        ShowSuggestion(meta.SuggestedTitle);
        _suggest.Visible = File.Exists(rec.SummaryFile);

        _files.MaximumSize = new Size(480, 0);
        _files.Margin = new Padding(3, 4, 3, 8);
        grid.Controls.Add(_files);

        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Anchor = AnchorStyles.Right, Margin = new Padding(0, 4, 0, 0) };
        buttons.Controls.AddRange([cancel, _ok]);
        grid.Controls.Add(buttons);
        Controls.Add(grid);

        AcceptButton = _ok;
        CancelButton = cancel;
        _ok.Click += (_, _) => ChosenTitle = MeetingAbout.TypedTitle(_title.Text);
        Shown += (_, _) =>
        {
            _title.Focus();
            _title.SelectAll();
        };
        FormClosed += (_, _) => _asking?.Cancel();
        Changed();
    }

    void Changed()
    {
        var title = MeetingAbout.TypedTitle(_title.Text);
        _ok.Enabled = title is not null;
        _files.Text = _note is null
            ? "Its notes will be named after it when they're made."
            : title is null ? "" : $"The note is renamed to match: \"{Path.GetFileNameWithoutExtension(NoteWriter.FileName(_meta.StartedAt, title))}\" " +
                                   "(the .md, .html and .mp3 files).";
        if (_use.Tag is string s) _use.Enabled = !string.Equals(s, title, StringComparison.Ordinal);
    }

    void ShowSuggestion(string? suggestion)
    {
        bool show = suggestion is not null && !string.Equals(suggestion, _meta.Title, StringComparison.Ordinal);
        _use.Tag = suggestion;
        _use.Visible = show;
        if (show)
        {
            _use.Text = $"Use \"{suggestion}\"";
            _use.LinkArea = new LinkArea(0, _use.Text.Length);
        }
    }

    async Task SuggestAsync()
    {
        _asking?.Cancel();
        _asking = new CancellationTokenSource(MeetingDescriber.Patience);
        _suggest.Enabled = false;
        _status.ForeColor = SystemColors.GrayText;
        _status.Text = "Asking your summary model…";
        try
        {
            using var chat = _chat();
            if (chat is null)
            {
                _status.Text = "No summary model is set up (Settings → Account).";
                return;
            }
            var summary = await File.ReadAllTextAsync(_rec.SummaryFile, _asking.Token);
            var (title, description) = await MeetingDescriber.AskAsync(chat, summary, _asking.Token);
            if (IsDisposed) return;
            if (title is null)
            {
                _status.Text = "It didn't suggest a title. Try again, or type one.";
                return;
            }
            _rec.UpdateMeta(m =>
            {
                m.SuggestedTitle = title;
                if (string.IsNullOrWhiteSpace(m.Description) && description is not null) m.Description = description;
            });
            ShowSuggestion(title);
            _title.Text = title;
            _title.Focus();
            _title.SelectAll();
            _status.Text = "";
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            Log.Warn($"{_rec.Name}: suggesting a title failed: {ex.Message}");
            if (IsDisposed) return;
            _status.ForeColor = Ui.Bad;
            _status.Text = ex is OperationCanceledException ? "It took too long. Try again later, or type one." : "Couldn't ask: " + Shorten(ex.Message, 80);
        }
        finally
        {
            if (!IsDisposed) _suggest.Enabled = true;
        }
    }

    static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
