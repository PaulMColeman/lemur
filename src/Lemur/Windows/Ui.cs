using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Lemur.Config;
using Lemur.Logging;

namespace Lemur.App.Windows;

/// <summary>Fonts, colours, small control factories and shell calls shared by the settings window and the wizard.</summary>
static class Ui
{
    public const string Caption = "Lemur";

    /// <summary>The lemur, for windows (the tray icon stays a coloured dot that shows what the recorder is doing).</summary>
    public static readonly Icon AppIcon = LoadAppIcon();

    static Icon LoadAppIcon()
    {
        using var stream = typeof(Ui).Assembly.GetManifestResourceStream("Lemur.ico");
        return stream is null ? TrayIcons.Idle : new Icon(stream);
    }

    public static readonly Font BaseFont = SystemFonts.MessageBoxFont ?? Control.DefaultFont;
    public static readonly Font HeadingFont = new(BaseFont.FontFamily, BaseFont.SizeInPoints + 1f, FontStyle.Bold, GraphicsUnit.Point);
    public static readonly Font BoldFont = new(BaseFont, FontStyle.Bold);
    public static readonly Font TitleFont = new(BaseFont.FontFamily, BaseFont.SizeInPoints + 5f, FontStyle.Regular, GraphicsUnit.Point);
    public static readonly Color Good = Color.FromArgb(0, 128, 0);
    public static readonly Color Bad = Color.FromArgb(192, 32, 32);

    public const AnchorStyles Wide = AnchorStyles.Left | AnchorStyles.Right;

    public static AppSettings Copy(AppSettings s) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s, Json.Options), Json.Options)!;

    public static string ToJson(AppSettings s) => JsonSerializer.Serialize(s, Json.Options);

    // ---- controls ----

    public static Label Text(string text) => new() { Text = text, AutoSize = true, Anchor = Wide };

    /// <summary>Grey explanatory text; wraps when it sits in a stretching table column.</summary>
    public static Label Muted(string text) => new()
    {
        Text = text, AutoSize = true, Anchor = Wide, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 8),
    };

    public static Label Bold(string text) => new() { Text = text, AutoSize = true, Anchor = Wide, Font = BoldFont };

    /// <summary>A text box: fixed width when given one, otherwise it stretches.</summary>
    public static TextBox Box(int width = 0, string? placeholder = null)
    {
        var box = new TextBox { PlaceholderText = placeholder ?? "" };
        if (width > 0)
        {
            box.Width = width;
            box.Anchor = AnchorStyles.Left;
        }
        else box.Anchor = Wide;
        return box;
    }

    public static NumericUpDown Number(int min, int max, int width = 72) => new()
    {
        Minimum = min, Maximum = max, Width = width, Anchor = AnchorStyles.Left, TextAlign = HorizontalAlignment.Right,
    };

    public static CheckBox Check(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 4, 3, 4) };

    public static RadioButton Radio(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 4, 12, 4) };

    public static ComboBox Combo() => new() { DropDownStyle = ComboBoxStyle.DropDown, Anchor = Wide };

    public static Button Button(string text, Action onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, UseVisualStyleBackColor = true, Padding = new Padding(6, 0, 6, 0) };
        b.Click += (_, _) => onClick();
        return b;
    }

    public static LinkLabel Link(string text, Action onClick)
    {
        var l = new LinkLabel { Text = text, AutoSize = true, Anchor = AnchorStyles.Left };
        l.LinkClicked += (_, _) => onClick();
        return l;
    }

    /// <summary>Controls side by side on one line, vertically centred.</summary>
    public static TableLayoutPanel Line(params Control[] items)
    {
        var t = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Left, Margin = new Padding(0),
            RowCount = 1, ColumnCount = items.Length,
        };
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        for (int i = 0; i < items.Length; i++)
        {
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.Controls.Add(items[i], i, 0);
        }
        return t;
    }

    /// <summary>The first control stretches; the rest (usually buttons) keep their size on its right.</summary>
    public static TableLayoutPanel Stretch(Control main, params Control[] after)
    {
        var t = new TableLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = Wide, Margin = new Padding(0),
            RowCount = 1, ColumnCount = 1 + after.Length,
        };
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        main.Anchor = Wide;
        t.Controls.Add(main, 0, 0);
        for (int i = 0; i < after.Length; i++)
        {
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.Controls.Add(after[i], i + 1, 0);
        }
        return t;
    }

    /// <summary>A shaded box that stands out from the page (setup-file hint, recording warning).</summary>
    public static VStack Callout(Color back, params Control[] items)
    {
        var box = new VStack { Dock = DockStyle.None, Anchor = Wide, BackColor = back, Padding = new Padding(10, 8, 10, 6), Margin = new Padding(0, 0, 0, 12) };
        foreach (var c in items) box.Add(c);
        return box;
    }

    // ---- messages ----

    public static void Info(Control? owner, string text) =>
        MessageBox.Show(owner?.FindForm(), text, Caption, MessageBoxButtons.OK, MessageBoxIcon.Information);

    public static void Warn(Control? owner, string text) =>
        MessageBox.Show(owner?.FindForm(), text, Caption, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    public static void Error(Control? owner, string text) =>
        MessageBox.Show(owner?.FindForm(), text, Caption, MessageBoxButtons.OK, MessageBoxIcon.Error);

    /// <summary>Yes/No. Destructive questions default to No.</summary>
    public static bool Confirm(Control? owner, string text, bool warning = false) =>
        MessageBox.Show(owner?.FindForm(), text, Caption, MessageBoxButtons.YesNo,
            warning ? MessageBoxIcon.Warning : MessageBoxIcon.Question,
            warning ? MessageBoxDefaultButton.Button2 : MessageBoxDefaultButton.Button1) == DialogResult.Yes;

    // ---- shell and files ----

    /// <summary>Opens a file or folder with its default app; text files fall back to Notepad.</summary>
    public static void Shell(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            if (File.Exists(path))
            {
                try
                {
                    Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
                    return;
                }
                catch (Win32Exception) { /* fall through to the log */ }
            }
            Log.Warn($"could not open {path}: {ex.Message}");
        }
    }

    public static void OpenFolder(string path)
    {
        try { Directory.CreateDirectory(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { /* Shell reports it */ }
        Shell(path);
    }

    public static string? PickFolder(Control owner, string description, string? start)
    {
        using var dlg = new FolderBrowserDialog { Description = description, UseDescriptionForTitle = true, ShowNewFolderButton = true };
        if (!string.IsNullOrWhiteSpace(start) && Directory.Exists(start)) dlg.InitialDirectory = start;
        return dlg.ShowDialog(owner.FindForm()) == DialogResult.OK ? dlg.SelectedPath : null;
    }

    /// <summary>The OneDrive folder (work first, then personal), when this PC has one.</summary>
    public static string? OneDrive()
    {
        foreach (var name in new[] { "OneDriveCommercial", "OneDrive" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } path && Directory.Exists(path)) return path;
        return null;
    }

    public static string? OneDrivePeopleFolder() => OneDrive() is { } od ? Path.Combine(od, "Lemur", "people") : null;

    public static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);

    /// <summary>A full path such as C:\Users\you\Notes (not relative, no bad characters).</summary>
    public static bool ValidPath(string path)
    {
        try
        {
            return path.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.IsPathFullyQualified(path) && Path.GetFullPath(path).Length > 0;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static string People(int n) => n == 1 ? "1 person" : $"{n} people";
}
