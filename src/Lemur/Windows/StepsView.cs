using System.Drawing.Drawing2D;
using Lemur.Pipeline;

namespace Lemur.App.Windows;

/// <summary>The little round icons of the Recordings window: a green check, a spinning ring, a red dot, an orange "!", a grey circle.</summary>
static class StepIcons
{
    public static readonly Color Green = Color.FromArgb(34, 139, 74);
    public static readonly Color Blue = Color.FromArgb(11, 98, 196);
    public static readonly Color Orange = Color.FromArgb(214, 110, 0);
    public static readonly Color Red = Color.FromArgb(200, 32, 32);
    public static readonly Color Grey = Color.FromArgb(160, 160, 160);
    static readonly Color BlueTrack = Color.FromArgb(205, 222, 244);

    /// <param name="angle">Where the working ring's arc is, in degrees (it turns while something is running).</param>
    public static void Draw(Graphics g, RectangleF r, StepState state, float angle = -90)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float w = r.Width;
        switch (state)
        {
            case StepState.Done:
                using (var fill = new SolidBrush(Green)) g.FillEllipse(fill, r);
                using (var pen = new Pen(Color.White, w * 0.13f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawLines(pen, [P(r, 0.28f, 0.52f), P(r, 0.44f, 0.68f), P(r, 0.73f, 0.36f)]);
                break;
            case StepState.Working:
                var ring = Inset(r, w * 0.09f);
                using (var track = new Pen(BlueTrack, w * 0.16f)) g.DrawEllipse(track, ring);
                using (var arc = new Pen(Blue, w * 0.16f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(arc, ring, angle, 110);
                break;
            case StepState.Failed:
                using (var fill = new SolidBrush(Orange)) g.FillEllipse(fill, r);
                using (var pen = new Pen(Color.White, w * 0.13f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(pen, P(r, 0.5f, 0.25f), P(r, 0.5f, 0.55f));
                using (var dot = new SolidBrush(Color.White)) g.FillEllipse(dot, Centre(r, 0.5f, 0.74f, w * 0.075f));
                break;
            case StepState.Recording:
                using (var halo = new SolidBrush(Color.FromArgb(60, Red))) g.FillEllipse(halo, r);
                using (var fill = new SolidBrush(Red)) g.FillEllipse(fill, Inset(r, w * 0.2f));
                break;
            default:
                using (var pen = new Pen(Grey, Math.Max(1f, w * 0.09f))) g.DrawEllipse(pen, Inset(r, w * 0.07f));
                break;
        }
    }

    /// <summary>A waiting meeting in the list: a grey clock.</summary>
    public static void DrawClock(Graphics g, RectangleF r)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float w = r.Width;
        using var pen = new Pen(Grey, Math.Max(1f, w * 0.09f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawEllipse(pen, Inset(r, w * 0.07f));
        g.DrawLines(pen, [P(r, 0.5f, 0.27f), P(r, 0.5f, 0.52f), P(r, 0.68f, 0.62f)]);
    }

    /// <summary>Icons for the list's rows, one per <see cref="RowKind"/>, in that order.</summary>
    public static ImageList ForRows(int size)
    {
        var list = new ImageList { ImageSize = new Size(size, size), ColorDepth = ColorDepth.Depth32Bit };
        foreach (var kind in Enum.GetValues<RowKind>())
        {
            var bitmap = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bitmap))
            {
                var r = new RectangleF(1.5f, 1.5f, size - 3, size - 3);
                switch (kind)
                {
                    case RowKind.Recording: Draw(g, r, StepState.Recording); break;
                    case RowKind.Working: Draw(g, r, StepState.Working, -60); break;
                    case RowKind.Waiting: DrawClock(g, r); break;
                    case RowKind.Retrying or RowKind.Interrupted: Draw(g, r, StepState.Failed); break;
                    default: Draw(g, r, StepState.Done); break;
                }
            }
            list.Images.Add(bitmap);
        }
        return list;
    }

    static PointF P(RectangleF r, float x, float y) => new(r.X + r.Width * x, r.Y + r.Height * y);
    static RectangleF Inset(RectangleF r, float by) => RectangleF.Inflate(r, -by, -by);
    static RectangleF Centre(RectangleF r, float x, float y, float radius) => new(r.X + r.Width * x - radius, r.Y + r.Height * y - radius, radius * 2, radius * 2);
}

/// <summary>
/// A selected recording's steps as a small timeline: an icon per step joined by a line (green up to where it has got),
/// the step's name, how long it took or should take, and a line underneath saying when the notes should be ready.
/// </summary>
sealed class StepsView : Control
{
    readonly System.Windows.Forms.Timer _spin = new() { Interval = 50 };
    IReadOnlyList<StepLine> _lines = [];
    string? _footer, _basis, _empty;
    float _angle = -90;

    public StepsView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = SystemColors.Window;
        _spin.Tick += (_, _) =>
        {
            _angle = (_angle + 18) % 360;
            Invalidate();
        };
    }

    /// <param name="footer">When the notes should be ready (or how long they took).</param>
    /// <param name="basis">Where the estimates come from, in small print.</param>
    /// <param name="empty">Shown instead when there are no steps.</param>
    public void SetSteps(IReadOnlyList<StepLine> lines, string? footer, string? basis, string? empty)
    {
        if (lines.SequenceEqual(_lines) && footer == _footer && basis == _basis && empty == _empty) return;
        _lines = lines;
        _footer = footer;
        _basis = basis;
        _empty = empty;
        _spin.Enabled = lines.Any(l => l.State == StepState.Working);
        Invalidate();
    }

    float DpiScale => DeviceDpi / 96f;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        float s = DpiScale;
        float pad = 12 * s, icon = 18 * s, rowHeight = Math.Max(28 * s, Font.Height * 1.8f);
        float textX = pad + icon + 10 * s;

        if (_lines.Count == 0)
        {
            if (_empty is not null)
                TextRenderer.DrawText(g, _empty, Font, new Rectangle((int)pad, (int)pad, Width - (int)(2 * pad), Height - (int)(2 * pad)),
                    SystemColors.GrayText, TextFormatFlags.WordBreak);
            return;
        }

        using var bold = new Font(Font, FontStyle.Bold);
        int timeWidth = (int)(110 * s);
        int timeX = Math.Max((int)textX + 140, (int)(Width * 0.52f));
        int noteX = timeX + timeWidth;

        // The line joining the icons: green as far as the last finished step.
        for (int i = 0; i < _lines.Count - 1; i++)
        {
            float x = pad + icon / 2, y1 = pad + i * rowHeight + rowHeight / 2 + icon / 2, y2 = pad + (i + 1) * rowHeight + rowHeight / 2 - icon / 2;
            bool reached = _lines[i].State == StepState.Done && _lines[i + 1].State != StepState.Next;
            using var pen = new Pen(reached ? StepIcons.Green : Color.FromArgb(215, 215, 215), 2 * s);
            g.DrawLine(pen, x, y1 + 2 * s, x, y2 - 2 * s);
        }

        for (int i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            float top = pad + i * rowHeight;
            StepIcons.Draw(g, new RectangleF(pad, top + (rowHeight - icon) / 2, icon, icon), line.State, _angle);

            var (nameColour, timeColour) = line.State switch
            {
                StepState.Working => (StepIcons.Blue, StepIcons.Blue),
                StepState.Recording => (StepIcons.Red, StepIcons.Red),
                StepState.Failed => (StepIcons.Orange, SystemColors.GrayText),
                StepState.Next => (SystemColors.GrayText, SystemColors.GrayText),
                _ => (SystemColors.WindowText, SystemColors.WindowText),
            };
            var font = line.State is StepState.Working or StepState.Recording ? bold : Font;
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            int y = (int)top, h = (int)rowHeight;
            TextRenderer.DrawText(g, line.Name, font, new Rectangle((int)textX, y, timeX - (int)textX - 8, h), nameColour, flags);
            TextRenderer.DrawText(g, line.Time, font, new Rectangle(timeX, y, timeWidth, h), timeColour, flags);
            if (line.Note is not null)
                TextRenderer.DrawText(g, line.Note, Font, new Rectangle(noteX, y, Math.Max(0, Width - noteX - (int)pad), h), SystemColors.GrayText, flags);
        }

        int footerTop = (int)(pad + _lines.Count * rowHeight + 6 * s);
        if (_footer is not null)
        {
            var size = TextRenderer.MeasureText(g, _footer, bold, new Size(Width - (int)(2 * pad), int.MaxValue), TextFormatFlags.WordBreak);
            TextRenderer.DrawText(g, _footer, bold, new Rectangle((int)pad, footerTop, Width - (int)(2 * pad), size.Height), SystemColors.WindowText,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            footerTop += size.Height + (int)(2 * s);
        }
        if (_basis is not null)
            TextRenderer.DrawText(g, _basis, Font, new Rectangle((int)pad, footerTop, Width - (int)(2 * pad), Height - footerTop), SystemColors.GrayText,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        _spin.Enabled = Visible && _lines.Any(l => l.State == StepState.Working);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _spin.Dispose();
        base.Dispose(disposing);
    }
}
