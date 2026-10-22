using System.Runtime.InteropServices;
using Lemur.Pipeline;

namespace Lemur.App.Windows;

/// <summary>
/// The Recordings window's list, drawn by hand so each row can show two lines: the meeting's title, and under it what
/// the meeting was about (or, while notes are being made, when they should be ready). Rows carry a
/// <see cref="RecordingRow"/> in their Tag, and sit under collapsible headings (Today, Last week, September 2026),
/// which Windows draws. With nothing to show, it says why (<see cref="EmptyText"/>).
/// </summary>
sealed class MeetingListView : ListView
{
    /// <summary>The column holding the title and description; the others are one line, centred.</summary>
    public const int TitleColumn = 1;

    readonly ImageList _icons;
    Font? _titleFont;
    string? _empty;

    public MeetingListView()
    {
        DoubleBuffered = true;
        OwnerDraw = true;
        View = View.Details;
        FullRowSelect = true;
        HideSelection = false;
        MultiSelect = false;
        HeaderStyle = ColumnHeaderStyle.Nonclickable;
        ShowItemToolTips = true;
        ShowGroups = true;
        _icons = StepIcons.ForRows(Scale(16));
    }

    /// <summary>Said in the middle of the list when it has no rows ("No meetings match …").</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? EmptyText
    {
        get => _empty;
        set
        {
            if (_empty == value) return;
            _empty = value;
            Invalidate();
        }
    }

    int Scale(int px) => (int)Math.Round(px * DeviceDpi / 96f);

    Font TitleFont => _titleFont ??= SemiBold(Font);

    static Font SemiBold(Font f)
    {
        var semibold = new Font("Segoe UI Semibold", f.SizeInPoints, FontStyle.Regular, GraphicsUnit.Point);
        if (semibold.Name == "Segoe UI Semibold") return semibold;
        semibold.Dispose();
        return new Font(f, FontStyle.Bold);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RowHeight();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        _titleFont?.Dispose();
        _titleFont = null;
        RowHeight();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        RowHeight();
    }

    /// <summary>A list view takes its row height from its small image list: an image list of the right height sets it.</summary>
    void RowHeight()
    {
        var old = SmallImageList;
        SmallImageList = new ImageList { ImageSize = new Size(1, Math.Min(256, 2 * Font.Height + Scale(14))) };
        old?.Dispose();
    }

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e) => e.DrawDefault = true;

    protected override void OnDrawItem(DrawListViewItemEventArgs e) { /* each cell draws itself in OnDrawSubItem */ }

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        if (e.Item?.Tag is not RecordingRow row || e.SubItem is null) return;
        var g = e.Graphics;
        bool selected = e.Item.Selected;
        bool contrast = SystemInformation.HighContrast;
        Color back = !selected ? BackColor
            : contrast ? SystemColors.Highlight
            : Focused ? Color.FromArgb(204, 232, 255) : Color.FromArgb(222, 222, 222);
        Color text = selected && contrast ? SystemColors.HighlightText : ForeColor;
        Color muted = selected && contrast ? SystemColors.HighlightText : SystemColors.GrayText;
        using (var brush = new SolidBrush(back)) g.FillRectangle(brush, e.Bounds);

        var bounds = Rectangle.FromLTRB(e.Bounds.Left + Scale(6), e.Bounds.Top, e.Bounds.Right - Scale(6), e.Bounds.Bottom);
        const TextFormatFlags oneLine = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
        switch (e.ColumnIndex)
        {
            case 0:
            {
                var size = _icons.ImageSize; // Draw, not Images[i]: that makes a new bitmap on every paint
                _icons.Draw(g, bounds.Left, bounds.Top + (bounds.Height - size.Height) / 2, (int)row.Kind);
                var rest = Rectangle.FromLTRB(bounds.Left + size.Width + Scale(8), bounds.Top, bounds.Right, bounds.Bottom);
                TextRenderer.DrawText(g, e.SubItem.Text, Font, rest, text, oneLine | TextFormatFlags.VerticalCenter);
                break;
            }
            case TitleColumn:
            {
                int line = Font.Height, gap = Scale(2);
                int top = bounds.Top + (bounds.Height - 2 * line - gap) / 2;
                var under = Subtitle(row);
                if (under is null) top = bounds.Top + (bounds.Height - line) / 2;
                TextRenderer.DrawText(g, e.SubItem.Text, TitleFont, new Rectangle(bounds.Left, top, bounds.Width, line + gap),
                    row.Untitled && !(selected && contrast) ? muted : text, oneLine);
                if (under is not null)
                    TextRenderer.DrawText(g, under, Font, new Rectangle(bounds.Left, top + line + gap, bounds.Width, line + gap), muted, oneLine);
                break;
            }
            default:
            {
                var colour = e.ColumnIndex == 3 && !(selected && contrast) ? e.Item.ForeColor : text;
                TextRenderer.DrawText(g, e.SubItem.Text, Font, bounds, colour, oneLine | TextFormatFlags.VerticalCenter);
                break;
            }
        }
    }

    /// <summary>The row's second line: what the meeting was about; until that is known, what is happening to it.</summary>
    public static string? Subtitle(RecordingRow row) =>
        !string.IsNullOrWhiteSpace(row.Description) ? row.Description
        : row.Kind is RowKind.Recording or RowKind.Working or RowKind.Waiting or RowKind.Retrying ? row.Expected
        : null;

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        const int WM_KEYUP = 0x0101, VK_LEFT = 0x25, VK_RIGHT = 0x27;
        if (m.Msg == WM_KEYUP && (int)m.WParam is VK_LEFT or VK_RIGHT && ShowGroups) TakeHeadingsFromWindows();
        const int WM_PAINT = 0x000F;
        if (m.Msg != WM_PAINT || Items.Count > 0 || string.IsNullOrEmpty(_empty)) return;
        using var g = CreateGraphics();
        int header = Font.Height + Scale(10);
        var area = new Rectangle(Scale(16), header + Scale(24), Math.Max(0, ClientSize.Width - Scale(32)), Math.Max(0, ClientSize.Height - header - Scale(40)));
        TextRenderer.DrawText(g, _empty, Font, area, SystemColors.GrayText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
    }

    /// <summary>
    /// Windows opens and closes headings itself (a click on the chevron, the keyboard on a focused heading), and WinForms
    /// notices only some of it: a click only where the mouse pointer is, the keys only for headings holding the selected
    /// meeting. A heading it didn't notice would spring back the next time its text changes. So each heading's state is
    /// read back from Windows (after those keys, and at each refresh), and a change raises
    /// <see cref="ListView.GroupCollapsedStateChanged"/>.
    /// </summary>
    public void TakeHeadingsFromWindows()
    {
        if (!IsHandleCreated || GroupId is null) return;
        for (int i = 0; i < Groups.Count; i++)
        {
            var group = Groups[i];
            if (group.CollapsedState == ListViewGroupCollapsedState.Default || GroupId.GetValue(group) is not int id) continue;
            // As WinForms asks it (ListViewGroup.GetNativeCollapsedState): by the id it gave the group in Windows.
            var state = (uint)SendMessage(Handle, LVM_GETGROUPSTATE, id, (IntPtr)(LVGS_COLLAPSIBLE | LVGS_COLLAPSED));
            if ((state & LVGS_COLLAPSIBLE) == 0) continue; // not in Windows' list yet
            var native = (state & LVGS_COLLAPSED) != 0 ? ListViewGroupCollapsedState.Collapsed : ListViewGroupCollapsedState.Expanded;
            if (native == group.CollapsedState) continue;
            group.CollapsedState = native;
            OnGroupCollapsedStateChanged(new ListViewGroupEventArgs(i));
        }
    }

    /// <summary>
    /// The id a group has in Windows. WinForms keeps it to itself; without it (a later .NET), headings aren't read back.
    /// (2026-10-15: reading it back by place with LVM_GETGROUPINFOBYINDEX gave every heading the first one's state.)
    /// </summary>
    static readonly System.Reflection.PropertyInfo? GroupId =
        typeof(ListViewGroup).GetProperty("ID", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) is { PropertyType: var t } p && t == typeof(int) ? p : null;

    const int LVM_GETGROUPSTATE = 0x1000 + 92;
    const uint LVGS_COLLAPSED = 0x1, LVGS_COLLAPSIBLE = 0x8;

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, IntPtr lParam);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icons.Dispose();
            _titleFont?.Dispose();
            SmallImageList?.Dispose();
        }
        base.Dispose(disposing);
    }
}
