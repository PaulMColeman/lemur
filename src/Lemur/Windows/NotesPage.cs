using Lemur.Config;
using Lemur.Pipeline;

namespace Lemur.App.Windows;

/// <summary>Where notes go, what the meeting file holds, the summary prompt, and how long audio is kept, the notes' MP3s with
/// it or not (design 7.2, Notes).</summary>
sealed class NotesPage : SettingsPage
{
    const string PromptFileName = "summary-prompt.txt";

    readonly TextBox _notes = Ui.Box();
    readonly CheckBox _transcript = Ui.Check("Include the transcript");
    readonly RadioButton _builtIn = Ui.Radio("Built-in");
    readonly RadioButton _custom = Ui.Radio("Custom file:");
    readonly TextBox _prompt = Ui.Box(placeholder: "a .txt or .md file");
    readonly Button _browsePrompt;
    readonly NumericUpDown _days = Ui.Number(0, 3650);
    readonly CheckBox _forever = Ui.Check("forever");
    readonly CheckBox _deleteMp3 = Ui.Check("Delete each note's MP3 too");
    readonly TextBox _recordings = Ui.Box(placeholder: AppPaths.DefaultRecordingsFolder);

    public NotesPage(PageHost host) : base(host, "Notes", "Where your meeting notes go, and how long audio is kept.")
    {
        _browsePrompt = Ui.Button("Browse…", BrowsePrompt);

        var g = new Grid();
        g.Heading("Notes");
        g.Row("Notes folder", Ui.Stretch(_notes, Ui.Button("Browse…", () => Browse(_notes, "Choose the folder for meeting notes"))),
            "Each meeting becomes a Markdown note and a web page here. A OneDrive folder lets you read them on any device.");
        g.Row("Meeting file", _transcript,
            "Each note also gets a .json for programs, like an agent that picks up your action items. Untick this when " +
            "what reads it may send it to a model on the internet: the file then has no transcript. The note still has it.");

        g.Heading("Summary");
        // Both radio buttons must share one parent to work as a pair, so the prompt choice is its own little table.
        var prompt = new TableLayoutPanel { ColumnCount = 3, RowCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = Ui.Wide, Margin = new Padding(0) };
        prompt.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        prompt.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        prompt.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (int i = 0; i < 3; i++) prompt.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        prompt.Controls.Add(_builtIn, 0, 0);
        prompt.Controls.Add(_custom, 0, 1);
        prompt.Controls.Add(_prompt, 1, 1);
        prompt.Controls.Add(_browsePrompt, 2, 1);
        prompt.Controls.Add(Ui.Button("Edit…", EditPrompt), 1, 2);
        g.Row("Prompt", prompt, "The instructions the summary model gets. \"{name}\" in your file becomes your name.", top: true);

        g.Heading("Audio");
        g.Row("Keep audio for", Ui.Line(_days, Ui.Text("days"), _forever),
            "The recordings Lemur works from are deleted after this, except meetings marked Keep recording.");
        g.Row(null, _deleteMp3,
            "Off, each note keeps its MP3 until you delete the note. On, the MP3 goes with the recordings: the note and its " +
            "transcript stay, and the page hides its player. Meetings marked Keep recording keep theirs.");
        g.Row("Recordings", Ui.Stretch(_recordings, Ui.Button("Browse…", () => Browse(_recordings, "Choose the folder for recordings"))),
            "Where audio waits until it's turned into notes. Blank = the default shown in grey.");
        Body.Add(g);

        _custom.CheckedChanged += (_, _) => UpdateEnabled();
        _forever.CheckedChanged += (_, _) => UpdateEnabled();
    }

    public override void LoadFrom(AppSettings s)
    {
        _notes.Text = s.NotesFolder;
        _transcript.Checked = s.MeetingFile.IncludeTranscript;
        (string.IsNullOrWhiteSpace(s.Summary.PromptFile) ? _builtIn : _custom).Checked = true;
        _prompt.Text = s.Summary.PromptFile ?? "";
        _forever.Checked = s.KeepAudioDays < 0;
        _days.Value = Math.Clamp(s.KeepAudioDays < 0 ? 14 : s.KeepAudioDays, 0, 3650);
        _deleteMp3.Checked = s.NoteMp3.DeleteWithAudio;
        _recordings.Text = s.RecordingsFolder ?? "";
        UpdateEnabled();
    }

    public override void SaveTo(AppSettings s)
    {
        s.NotesFolder = _notes.Text.Trim();
        s.MeetingFile.IncludeTranscript = _transcript.Checked;
        s.Summary.PromptFile = _custom.Checked ? Ui.Blank(_prompt.Text) : null;
        s.KeepAudioDays = _forever.Checked ? -1 : (int)_days.Value;
        s.NoteMp3.DeleteWithAudio = _deleteMp3.Checked;
        s.RecordingsFolder = Ui.Blank(_recordings.Text);
    }

    /// <summary>
    /// Saving "Delete each note's MP3 too" on, or fewer days while it's on, deletes the MP3s of meetings already past the days
    /// at the next clean-up: they are counted and the user asked first. False: don't save, so the setting stays as it was.
    /// </summary>
    /// <param name="before">The settings as saved; null for a first setup.</param>
    public static bool ConfirmMp3s(Control owner, AppSettings? before, AppSettings edited)
    {
        if (!AudioCleanUp.MoreMp3sGo(before, edited)) return true;
        int days = edited.KeepAudioDays, n;
        var cursor = Cursor.Current;
        Cursor.Current = Cursors.WaitCursor; // a few hundred small files
        try
        {
            n = AudioCleanUp.Mp3sToDelete(edited.ResolvedRecordingsFolder, days, DateTimeOffset.Now);
        }
        finally
        {
            Cursor.Current = cursor;
        }
        if (n == 0) return true;
        var (meetings, mp3s, stay, them) = n == 1
            ? ("1 meeting is", "its note's MP3", "Its note and transcript stay; the page loses its player.", "it")
            : ($"{n} meetings are", "their notes' MP3s", "The notes and transcripts stay; the pages lose their players.", "them");
        return Ui.Confirm(owner, $"{meetings} more than {(days == 1 ? "1 day" : $"{days} days")} old, so {mp3s} will be deleted, to the " +
                                 $"Recycle Bin, within a few minutes of saving. {stay} Meetings marked Keep recording keep theirs." +
                                 $"\n\nDelete {them}?", warning: true);
    }

    public override void Check(List<string> errors)
    {
        var notes = _notes.Text.Trim();
        if (notes.Length == 0) errors.Add("Notes: choose a notes folder.");
        else if (!Ui.ValidPath(notes)) errors.Add("Notes: the notes folder must be a full path, like C:\\Users\\you\\Documents\\Meeting Notes.");
        if (_custom.Checked)
        {
            var file = _prompt.Text.Trim();
            if (file.Length == 0) errors.Add("Notes: choose the custom prompt file, or pick Built-in.");
            else if (!File.Exists(file)) errors.Add($"Notes: the prompt file {file} doesn't exist.");
        }
        if (Ui.Blank(_recordings.Text) is { } recordings && !Ui.ValidPath(recordings))
            errors.Add("Notes: the recordings folder must be a full path, or blank.");
    }

    void UpdateEnabled()
    {
        _prompt.Enabled = _custom.Checked;
        _browsePrompt.Enabled = _custom.Checked;
        _days.Enabled = !_forever.Checked;
        _deleteMp3.Enabled = !_forever.Checked; // nothing is deleted, so there is nothing to delete with it
    }

    void Browse(TextBox box, string description)
    {
        var start = Ui.Blank(box.Text) ?? Ui.Blank(box.PlaceholderText);
        if (Ui.PickFolder(this, description, start) is { } folder) box.Text = folder;
    }

    void BrowsePrompt()
    {
        using var dialog = new OpenFileDialog { Title = "Choose a summary prompt", Filter = "Text files (*.txt;*.md)|*.txt;*.md|All files (*.*)|*.*" };
        if (Ui.Blank(_prompt.Text) is { } current && Path.GetDirectoryName(current) is { } dir && Directory.Exists(dir)) dialog.InitialDirectory = dir;
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        _prompt.Text = dialog.FileName;
        _custom.Checked = true;
    }

    /// <summary>Opens the custom prompt; for the built-in one, offers an editable copy and switches to it.</summary>
    void EditPrompt()
    {
        if (_builtIn.Checked)
        {
            var copy = Path.Combine(Host.ConfigFolder, PromptFileName);
            var question = File.Exists(copy)
                ? $"The built-in prompt can't be changed. Use your own copy from before ({copy}) instead, and open it?"
                : $"The built-in prompt can't be changed. Make your own copy of it to edit, and use that from now on?\n\nIt's saved as {copy}.";
            if (!Ui.Confirm(this, question) || !WriteDefault(copy)) return;
            _prompt.Text = copy;
            _custom.Checked = true;
            Ui.Shell(copy);
            return;
        }

        var file = _prompt.Text.Trim();
        if (file.Length == 0)
        {
            BrowsePrompt();
            return;
        }
        if (!File.Exists(file) && (!Ui.Confirm(this, $"{file} doesn't exist yet. Create it from the built-in prompt?") || !WriteDefault(file))) return;
        Ui.Shell(file);
    }

    /// <summary>Writes the built-in prompt to <paramref name="path"/> unless a file is already there.</summary>
    bool WriteDefault(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                File.WriteAllText(path, SummaryPrompt.Default + Environment.NewLine);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Ui.Error(this, $"The prompt file couldn't be created: {ex.Message}");
            return false;
        }
    }
}
