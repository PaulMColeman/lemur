using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Lemur.App;

/// <summary>Plain coloured dots: no image assets to ship, readable at 16 px.</summary>
static class TrayIcons
{
    public static readonly Icon Idle = Dot(Color.FromArgb(96, 125, 139), Color.FromArgb(55, 71, 79));
    public static readonly Icon Recording = Dot(Color.FromArgb(229, 57, 53), Color.FromArgb(183, 28, 28));
    public static readonly Icon Processing = Dot(Color.FromArgb(251, 140, 0), Color.FromArgb(230, 81, 0));
    public static readonly Icon Attention = Dot(Color.FromArgb(253, 216, 53), Color.FromArgb(245, 127, 23));
    public static readonly Icon Paused = Ring(Color.FromArgb(144, 164, 174));

    static Icon Dot(Color fill, Color border) => Draw(g =>
    {
        using var brush = new SolidBrush(fill);
        g.FillEllipse(brush, 4, 4, 24, 24);
        using var pen = new Pen(border, 3);
        g.DrawEllipse(pen, 4, 4, 24, 24);
    });

    static Icon Ring(Color colour) => Draw(g =>
    {
        using var pen = new Pen(colour, 4);
        g.DrawEllipse(pen, 5, 5, 22, 22);
    });

    static Icon Draw(Action<Graphics> paint)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            paint(g);
        }
        IntPtr handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}
