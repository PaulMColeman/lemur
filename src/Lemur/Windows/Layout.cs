namespace Lemur.App.Windows;

/// <summary>Rows top to bottom, each as wide as the stack. Wrapping labels wrap to that width.</summary>
sealed class VStack : TableLayoutPanel
{
    public VStack()
    {
        ColumnCount = 1;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Dock = DockStyle.Top;
        Margin = new Padding(0);
    }

    public T Add<T>(T control) where T : Control
    {
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(control, 0, RowCount++);
        return control;
    }
}

/// <summary>
/// A settings sheet: short labels on the left, fields on the right, grey help under each field.
/// Nothing spans both columns — a spanning control makes TableLayoutPanel widen the label column — so wide
/// content goes in the field column, or in its own <see cref="VStack"/> row outside the grid.
/// </summary>
sealed class Grid : TableLayoutPanel
{
    List<Control>? _group;

    public Grid()
    {
        ColumnCount = 2;
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Anchor = Ui.Wide | AnchorStyles.Top;
        Margin = new Padding(0);
    }

    /// <summary>Every control added until <see cref="EndGroup"/> also goes into <paramref name="group"/> (to hide or disable together).</summary>
    public void BeginGroup(List<Control> group) => _group = group;

    public void EndGroup() => _group = null;

    /// <summary>A bold section title in the label column, with an optional note beside it.</summary>
    public void Heading(string text, Control? note = null)
    {
        int r = NewRow();
        int top = r == 0 ? 0 : 14;
        Put(new Label { Text = text, AutoSize = true, Font = Ui.HeadingFont, Anchor = AnchorStyles.Left, Margin = new Padding(0, top, 16, 6) }, 0, r);
        if (note is not null)
        {
            note.Margin = new Padding(3, top, 3, 6);
            note.Anchor = Ui.Wide;
            Put(note, 1, r);
        }
    }

    /// <summary>A labelled field (label may be null), with grey help under it. <paramref name="top"/> aligns the label with a tall field's top.</summary>
    public T Row<T>(string? label, T field, string? help = null, bool top = false) where T : Control
    {
        int r = NewRow();
        if (label is not null)
            Put(new Label
            {
                Text = label, AutoSize = true,
                Anchor = top ? AnchorStyles.Left | AnchorStyles.Top : AnchorStyles.Left,
                Margin = new Padding(12, top ? 6 : 3, 12, 3),
            }, 0, r);
        Put(field, 1, r);
        if (help is not null) Help(help);
        return field;
    }

    public Label Help(string text)
    {
        var label = Ui.Muted(text);
        Put(label, 1, NewRow());
        return label;
    }

    int NewRow()
    {
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return RowCount++;
    }

    void Put(Control c, int column, int row)
    {
        Controls.Add(c, column, row);
        _group?.Add(c);
    }
}
