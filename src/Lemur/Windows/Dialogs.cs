namespace Lemur.App.Windows;

/// <summary>A small fixed-width dialog: content on top, OK and Cancel at the bottom right; its height fits the content.</summary>
class SmallDialog : Form
{
    readonly TableLayoutPanel _root;
    readonly FlowLayoutPanel _left;

    public SmallDialog(string title, string okText, int width = 460)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = Ui.BaseFont;
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(width, 200);
        Padding = new Padding(14, 12, 14, 12);

        Ok = new Button { Text = okText, DialogResult = DialogResult.OK, AutoSize = true, UseVisualStyleBackColor = true };
        Cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, UseVisualStyleBackColor = true };
        _left = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0), WrapContents = false };
        var right = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0), WrapContents = false };
        right.Controls.Add(Ok);
        right.Controls.Add(Cancel);

        var buttons = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, Anchor = Ui.Wide, Margin = new Padding(0, 12, 0, 0) };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.Controls.Add(_left, 0, 0);
        buttons.Controls.Add(right, 1, 0);

        Body.Dock = DockStyle.None;
        Body.Anchor = Ui.Wide | AnchorStyles.Top;
        _root = new TableLayoutPanel { ColumnCount = 1, RowCount = 2, Dock = DockStyle.Fill, Margin = new Padding(0) };
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _root.Controls.Add(Body, 0, 0);
        _root.Controls.Add(buttons, 0, 1);
        Controls.Add(_root);

        AcceptButton = Ok;
        CancelButton = Cancel;
        ResumeLayout(false);
    }

    protected VStack Body { get; } = new();
    protected Button Ok { get; }
    protected Button Cancel { get; }

    protected void AddLeftButton(string text, Action onClick) => _left.Controls.Add(Ui.Button(text, onClick));

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        int inner = ClientSize.Width - Padding.Horizontal;
        int height = _root.GetPreferredSize(new Size(inner, 0)).Height;
        ClientSize = new Size(ClientSize.Width, height + Padding.Vertical);
    }
}

/// <summary>Asks for a key. The value goes to the caller once and is never shown or logged.</summary>
sealed class KeyDialog : SmallDialog
{
    readonly TextBox _key = new() { UseSystemPasswordChar = true, Anchor = Ui.Wide };
    bool _remove;

    public KeyDialog(string what, bool saved, string? help = null)
        : base($"{char.ToUpperInvariant(what[0])}{what[1..]} key", "Save")
    {
        Body.Add(Ui.Text(saved ? $"Paste a new {what} key to replace the saved one." : $"Paste your {what} key."));
        Body.Add(_key);
        Body.Add(Ui.Muted("It's kept in Windows Credential Manager on this PC, never in settings.json or in a setup file."
                          + (string.IsNullOrWhiteSpace(help) ? "" : " " + help)));
        if (saved) AddLeftButton("Remove saved key", () => { _remove = true; DialogResult = DialogResult.OK; });
        Ok.Enabled = false;
        _key.TextChanged += (_, _) => Ok.Enabled = _key.Text.Trim().Length > 0;
    }

    public bool Remove => _remove;
    public string Value => _key.Text.Trim();
}

/// <summary>One or more text fields; OK needs the first one filled in.</summary>
sealed class PromptDialog : SmallDialog
{
    readonly TextBox[] _boxes;

    public PromptDialog(string title, string text, params (string Label, string Initial, string? Help)[] fields) : base(title, "OK")
    {
        Body.Add(Ui.Text(text));
        var grid = new Grid();
        _boxes = fields.Select(f =>
        {
            var box = Ui.Box();
            box.Text = f.Initial;
            grid.Row(f.Label.Length > 0 ? f.Label : null, box, f.Help);
            return box;
        }).ToArray();
        Body.Add(grid);
        Ok.Enabled = _boxes[0].Text.Trim().Length > 0;
        _boxes[0].TextChanged += (_, _) => Ok.Enabled = _boxes[0].Text.Trim().Length > 0;
    }

    public string Value(int index = 0) => _boxes[index].Text.Trim();

    public static string? Ask(Control owner, string title, string text, string initial)
    {
        using var dialog = new PromptDialog(title, text, ("", initial, null));
        return dialog.ShowDialog(owner.FindForm()) == DialogResult.OK ? dialog.Value() : null;
    }
}

/// <summary>Pick one from a list.</summary>
sealed class PickDialog : SmallDialog
{
    readonly ListBox _list = new() { Anchor = Ui.Wide, Height = 150, IntegralHeight = false };

    public PickDialog(string title, string text, IEnumerable<string> items, string okText) : base(title, okText)
    {
        Body.Add(Ui.Text(text));
        _list.Items.AddRange(items.Cast<object>().ToArray());
        Body.Add(_list);
        Ok.Enabled = false;
        _list.SelectedIndexChanged += (_, _) => Ok.Enabled = _list.SelectedIndex >= 0;
        _list.DoubleClick += (_, _) => { if (_list.SelectedIndex >= 0) DialogResult = DialogResult.OK; };
    }

    public int Selected => _list.SelectedIndex;
}

/// <summary>"This will change: …" with the lines in a read-only box.</summary>
sealed class ListDialog : SmallDialog
{
    public ListDialog(string title, string intro, IReadOnlyList<string> lines, string okText, string? note = null) : base(title, okText, 540)
    {
        Body.Add(Ui.Text(intro));
        Body.Add(new TextBox
        {
            Multiline = true, ReadOnly = true, TabStop = false, ScrollBars = ScrollBars.Vertical, WordWrap = true,
            BackColor = SystemColors.Window, Anchor = Ui.Wide, Height = 22 + 16 * Math.Clamp(lines.Count, 2, 10),
            Text = string.Join(Environment.NewLine, lines),
        });
        if (note is not null) Body.Add(Ui.Muted(note));
    }
}
