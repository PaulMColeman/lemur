using Lemur.Pipeline;

namespace Lemur.App.Windows;

/// <summary>
/// "Import a recording": a recording made somewhere else (a phone, a recorder, a video) about to become a meeting. Shows
/// the title and start time Lemur worked out from the file, to change before it is imported: a phone's file may carry
/// no recording time, and its name may be "New Recording 12".
/// </summary>
sealed class ImportDialog : Form
{
    readonly TextBox _title = new() { Anchor = Ui.Wide, MaxLength = MeetingAbout.MaxTypedTitle, Width = 460 };
    readonly DateTimePicker _started = new()
    {
        Format = DateTimePickerFormat.Custom, CustomFormat = "dddd d MMMM yyyy   HH:mm", Width = 260, Anchor = AnchorStyles.Left,
    };
    readonly DateTimeOffset _guessed;

    /// <summary>The title typed ("" for none: its summary will suggest one), once the dialog closes with OK.</summary>
    public string ChosenTitle { get; private set; } = "";

    /// <summary>When the meeting began, as shown or changed.</summary>
    public DateTimeOffset StartedAt { get; private set; }

    public ImportDialog(string path, ImportGuess guess)
    {
        _guessed = guess.StartedAt;
        Text = "Import a recording — Lemur";
        Icon = Ui.AppIcon;
        Font = SystemFonts.MessageBoxFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(16, 14, 16, 8), Dock = DockStyle.Fill };
        grid.Controls.Add(new Label { AutoSize = true, Font = Ui.HeadingFont, Margin = new Padding(3, 0, 3, 2), Text = "Make notes from a recording" });
        grid.Controls.Add(Wrapped(Ui.Muted($"{Path.GetFileName(path)} · {Length(guess.Seconds)}")));

        grid.Controls.Add(Ui.Bold("Title"));
        _title.Text = guess.Title;
        grid.Controls.Add(_title);
        grid.Controls.Add(Wrapped(Ui.Muted("Leave it blank and its notes will suggest one.")));

        grid.Controls.Add(Ui.Bold("Started"));
        _started.Value = guess.StartedAt.LocalDateTime;
        grid.Controls.Add(_started);
        grid.Controls.Add(Wrapped(Ui.Muted(guess.StartedFrom switch
        {
            StartSource.Recording => "From the recording itself.",
            StartSource.FileName => "From the file's name.",
            _ => "The file doesn't say when it was recorded, so this is when it was last saved, less its length. Check it: " +
                 "the date decides where the meeting is listed and what its note is called.",
        })));

        var how = Wrapped(Ui.Muted("It's made into notes like an in-person meeting: everyone is on one track, so you are recognised " +
                                   "by your voice rather than by your microphone."));
        how.Margin = new Padding(3, 10, 3, 8);
        grid.Controls.Add(how);

        var ok = new Button { Text = "Import", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Anchor = AnchorStyles.Right, Margin = new Padding(0, 4, 0, 0) };
        buttons.Controls.AddRange([cancel, ok]);
        grid.Controls.Add(buttons);
        Controls.Add(grid);

        AcceptButton = ok;
        CancelButton = cancel;
        ok.Click += (_, _) =>
        {
            ChosenTitle = MeetingAbout.TypedTitle(_title.Text) ?? "";
            // Untouched, the guess keeps its seconds (the picker shows minutes); changed, it's the time as typed.
            StartedAt = Math.Abs((_started.Value - guess.StartedAt.LocalDateTime).TotalSeconds) < 1 ? _guessed : new DateTimeOffset(_started.Value);
        };
        Shown += (_, _) =>
        {
            _title.Focus();
            _title.SelectAll();
        };
    }

    static Label Wrapped(Label label)
    {
        label.MaximumSize = new Size(480, 0);
        return label;
    }

    static string Length(double seconds) =>
        seconds >= 3600 ? $"{(int)(seconds / 3600)} h {(int)Math.Round(seconds % 3600 / 60):00} min"
        : seconds >= 60 ? $"{Math.Round(seconds / 60):0} min"
        : $"{seconds:0} s";
}
