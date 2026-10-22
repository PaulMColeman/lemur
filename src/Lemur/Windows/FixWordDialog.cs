using Lemur.Config;
using Lemur.Pipeline;
using Lemur.Transcripts;

namespace Lemur.App.Windows;

/// <summary>
/// "Fix a word": what the transcription wrote, what it should say, whether to fix it in the meetings already made
/// (it counts them as you type) and whether to remember it for new ones (the vocabulary).
/// </summary>
sealed class FixWordDialog : Form
{
    readonly Func<Vocabulary, IReadOnlyList<RecordingRow>> _find;
    readonly TextBox _heard = new() { Width = 300, Anchor = AnchorStyles.Left };
    readonly ComboBox _right = new() { Width = 300, Anchor = AnchorStyles.Left, DropDownStyle = ComboBoxStyle.DropDown,
        AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
    readonly CheckBox _existing = Ui.Check("");
    readonly Label _which = Ui.Muted("");
    readonly CheckBox _remember = Ui.Check("Fix it in every new meeting too (adds it to your vocabulary)");
    readonly Button _ok = new() { Text = "Fix", AutoSize = true, DialogResult = DialogResult.OK };
    readonly System.Windows.Forms.Timer _typing = new() { Interval = 300 };
    IReadOnlyList<RecordingRow> _found = [];

    /// <param name="terms">Words already in the vocabulary, offered as the right spelling.</param>
    /// <param name="find">The finished meetings that have a misheard word in them.</param>
    public FixWordDialog(string? heard, IEnumerable<string> terms, Func<Vocabulary, IReadOnlyList<RecordingRow>> find)
    {
        _find = find;
        Text = "Fix a word — Lemur";
        Icon = Ui.AppIcon;
        Font = SystemFonts.MessageBoxFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        _right.Items.AddRange([.. terms.Order(StringComparer.OrdinalIgnoreCase)]);
        _heard.Text = heard ?? "";
        _remember.Checked = true;
        _existing.Checked = true;
        _which.MaximumSize = new Size(470, 0);
        _which.Margin = new Padding(24, 0, 3, 8);

        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(16, 14, 16, 8), Dock = DockStyle.Fill };
        grid.Controls.Add(new Label { AutoSize = true, Font = Ui.HeadingFont, Margin = new Padding(3, 0, 3, 2), Text = "Fix a misheard word" });
        var muted = Ui.Muted("When the transcription writes a word wrong (\"Cooper Netties\" for Kubernetes), fix it everywhere: transcripts, " +
                             "summaries, titles and notes. Notes you edited keep your edits; only the word changes.");
        muted.MaximumSize = new Size(480, 0);
        grid.Controls.Add(muted);
        var fields = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 4, 0, 4) };
        fields.Controls.Add(new Label { Text = "It wrote", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 3, 12, 3) }, 0, 0);
        fields.Controls.Add(_heard, 1, 0);
        fields.Controls.Add(new Label { Text = "It should say", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 3, 12, 3) }, 0, 1);
        fields.Controls.Add(_right, 1, 1);
        grid.Controls.Add(fields);
        grid.Controls.Add(_existing);
        grid.Controls.Add(_which);
        grid.Controls.Add(_remember);

        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Anchor = AnchorStyles.Right, Margin = new Padding(0, 8, 0, 0) };
        buttons.Controls.AddRange([cancel, _ok]);
        grid.Controls.Add(buttons);
        Controls.Add(grid);

        AcceptButton = _ok;
        CancelButton = cancel;
        _heard.TextChanged += (_, _) =>
        {
            _typing.Stop();
            _typing.Start();
            Changed();
        };
        _right.TextChanged += (_, _) =>
        {
            _typing.Stop();
            _typing.Start();
            Changed();
        };
        _existing.CheckedChanged += (_, _) => Changed();
        _remember.CheckedChanged += (_, _) => Changed();
        _typing.Tick += (_, _) =>
        {
            _typing.Stop();
            Count();
        };
        // Clicked straight after typing: count again first, so the meetings fixed are the ones for what's typed now.
        _ok.Click += (_, _) =>
        {
            _typing.Stop();
            Count();
            if (!_ok.Enabled) DialogResult = DialogResult.None;
        };
        Shown += (_, _) =>
        {
            Count();
            (_heard.TextLength == 0 ? (Control)_heard : _right).Focus();
        };
        FormClosed += (_, _) => _typing.Dispose();
    }

    /// <summary>What it wrote, as typed.</summary>
    public string Heard => _heard.Text.Trim();

    /// <summary>What it should say, as typed.</summary>
    public string Spelling => _right.Text.Trim();

    /// <summary>Remember it for new meetings.</summary>
    public bool Remember => _remember.Checked;

    /// <summary>The meetings to fix now (empty when "fix the meetings already made" is off).</summary>
    public IReadOnlyList<RecordingRow> Meetings => _existing.Checked ? _found : [];

    /// <summary>The correction as a vocabulary of one word; empty while either box is.</summary>
    public Vocabulary Correction => Heard.Length == 0 || Spelling.Length == 0 ? Vocabulary.Empty
        : new Vocabulary([new VocabularyEntry { Term = Spelling, HeardAs = [Heard] }]);

    void Count()
    {
        // Before the right spelling is typed: every meeting with what it wrote.
        _found = Heard.Length == 0 ? [] : _find(Spelling.Length > 0 ? Correction : Vocabulary.Finder(Heard));
        Changed();
    }

    void Changed()
    {
        int n = _found.Count;
        _existing.Text = n == 0 ? "Fix it in the meetings already made (none has it)"
            : $"Fix it in the {(n == 1 ? "meeting" : $"{n} meetings")} already made that {(n == 1 ? "has" : "have")} it";
        _existing.Enabled = n > 0;
        _which.Text = n == 0 ? "" : string.Join("\n", _found.Take(4).Select(r => $"{r.StartedAt?.ToLocalTime():d MMM HH:mm}  {r.Title}")) +
                                    (n > 4 ? $"\n…and {n - 4} more" : "");
        _which.Visible = n > 0;
        bool same = string.Equals(Heard, Spelling, StringComparison.Ordinal);
        _ok.Enabled = Heard.Length > 0 && Spelling.Length > 0 && !same && (_remember.Checked || (_existing.Checked && n > 0));
    }
}
