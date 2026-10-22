using System.Media;
using Lemur.Backends;
using Lemur.Config;
using Lemur.Pipeline;
using Lemur.Speakers;
using Lemur.Transcripts;

namespace Lemur.App.Windows;

/// <summary>
/// "Who's speaking?" (design 7.1): one row per voice in a meeting — listen, read a few of their lines,
/// type a name, choose whether to remember the voice. Opens from the notes toast, the tray menu, or the
/// "Name speakers" link in a meeting's page. Never pops up by itself.
/// </summary>
sealed class NamingForm : Form
{
    // Same order and colours as the HTML page, so "the purple one" means the same person in both.
    static readonly Color[] Palette =
    [
        ColorTranslator.FromHtml("#8250df"), ColorTranslator.FromHtml("#bf3989"), ColorTranslator.FromHtml("#1a7f37"),
        ColorTranslator.FromHtml("#bc4c00"), ColorTranslator.FromHtml("#0e7c86"), ColorTranslator.FromHtml("#9a6700"),
    ];

    const string NotAPersonChoice = "Not a person (video, TV)";

    readonly AppSettings _settings;
    readonly ISecretStore _secrets;
    readonly RecordingFolder _rec;
    readonly IdentifiedResult _identified;
    readonly List<Row> _rows = [];
    readonly string[] _known;
    readonly Button _save = new() { Text = "Save", AutoSize = true };
    readonly Label _status = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    SoundPlayer? _player;
    bool _listing;

    sealed record Row(IdentifiedSpeaker Speaker, ComboBox Name, CheckBox Remember, CheckBox Agreed, string Original);

    public NamingForm(AppSettings settings, ISecretStore secrets, RecordingFolder rec)
    {
        _settings = settings;
        _secrets = secrets;
        _rec = rec;
        _identified = SpeakerNamer.Load(rec);
        var meta = rec.LoadMeta();
        var labelled = rec.Read<List<LabelledSegment>>(rec.LabelledFile) ?? [];
        bool audio = File.Exists(rec.MicWav);
        bool prints = File.Exists(rec.VoiceprintsFile);
        var people = new PeopleStore(settings.Speakers.ResolvedPeopleFolder);
        _known = Directory.Exists(people.Folder) ? people.People().Where(p => !p.IsMe).Select(p => p.Name).ToArray() : [];

        Text = "Who's speaking? — Lemur";
        Icon = Ui.AppIcon;
        Font = SystemFonts.MessageBoxFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(860, 420);
        Size = new Size(980, 560);

        var title = string.IsNullOrWhiteSpace(meta.Title) ? "Meeting" : meta.Title;
        var header = new Label
        {
            AutoSize = true,
            Text = $"{title} — {meta.StartedAt.ToLocalTime():dddd d MMMM, HH:mm}",
            Font = new Font(Font.FontFamily, Font.Size * 1.3f, FontStyle.Bold),
        };
        var help = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(900, 0),
            ForeColor = SystemColors.GrayText,
            Text = (audio ? "Press ▶ to hear a voice (press again for the next clip). " : "This meeting's audio has been deleted, so voices can't be played or remembered — but you can still name them. ") +
                   "Type a name, or pick one from the list: everyone you've named before, and every name typed here, since one person " +
                   "can show up as two voices. The notes are updated when you save.",
        };

        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 6, Dock = DockStyle.Top, Padding = new Padding(0, 6, 0, 6) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));         // voice
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));         // play
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));     // what they said
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));    // name
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));         // remember / agreed
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        string[] headings = ["Voice", "", "What they said", "Name", "Remember voice", ""];
        for (int c = 0; c < headings.Length; c++)
            grid.Controls.Add(new Label { Text = headings[c], AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 3, 12, 6) }, c, 0);

        var voices = _identified.Speakers.Where(s => !s.IsMe).ToList();
        for (int i = 0; i < voices.Count; i++)
            AddRow(grid, i + 1, voices[i], Palette[i % Palette.Length], labelled, audio, prints);
        RefreshLists(typedIn: null);
        if (voices.Count == 0)
            grid.Controls.Add(new Label { Text = "Only you spoke in this meeting.", AutoSize = true }, 0, 1);

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(16, 0, 16, 0) };
        scroll.Controls.Add(grid);

        // Shown without a modal loop (beside the Recordings window, or alone from a note page's link), so setting
        // DialogResult doesn't close it: Save and Cancel close it themselves.
        var cancel = new Button { Text = "Cancel", AutoSize = true };
        cancel.Click += (_, _) => Close();
        var openNotes = new LinkLabel { Text = "Open the notes", AutoSize = true, Margin = new Padding(3, 9, 12, 3) };
        openNotes.LinkClicked += (_, _) => OpenNotes();
        _save.Click += async (_, _) => await SaveAsync();
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(12) };
        var helpLink = new LinkLabel { Text = "Help", AutoSize = true, Margin = new Padding(3, 9, 12, 3) };
        helpLink.LinkClicked += (_, _) => HelpLink.Open(section: "naming", owner: this);
        buttons.Controls.AddRange([cancel, _save, openNotes, helpLink, _status]);

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(16, 14, 16, 4), WrapContents = false };
        top.Controls.AddRange([header, help]);

        Controls.Add(scroll);
        Controls.Add(buttons);
        Controls.Add(top);
        AcceptButton = _save;
        CancelButton = cancel;
        FormClosed += (_, _) => _player?.Dispose();
    }

    /// <param name="row">The grid row: every control gets its own cell, so one left hidden ("They agreed" for a voice
    /// that can't be remembered) can't shift the rest of the row, and every row after it, along a column.</param>
    void AddRow(TableLayoutPanel grid, int row, IdentifiedSpeaker s, Color colour, List<LabelledSegment> labelled, bool audio, bool prints)
    {
        var voice = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(3, 3, 12, 10) };
        var chip = new Label { Text = "  " + s.Label, AutoSize = true, Font = new Font(Font, FontStyle.Bold), ForeColor = colour };
        voice.Controls.Add(chip);
        voice.Controls.Add(new Label { Text = TalkTime(s.TalkSeconds), AutoSize = true, ForeColor = SystemColors.GrayText });
        voice.Controls.Add(new Label { Text = StatusText(s), AutoSize = true, MaximumSize = new Size(230, 0) });
        voice.Paint += (_, e) => e.Graphics.FillRectangle(new SolidBrush(colour), 0, 3, 4, voice.Height - 6);

        int next = 0;
        var play = new Button { Text = "▶", Width = 36, Enabled = audio && s.Clips.Count > 0, Margin = new Padding(3, 3, 12, 3) };
        new ToolTip().SetToolTip(play, play.Enabled ? "Play a clip; press again for the next" : "No audio to play");
        play.Click += (_, _) =>
        {
            var clip = s.Clips[next++ % s.Clips.Count];
            Play(s.Track == Track.Others ? _rec.OthersWav : _rec.MicWav, clip);
        };

        var lines = labelled.Where(x => x.Speaker == s.Label && !x.IsMe)
            .OrderByDescending(x => x.End - x.Start).Take(3)
            .OrderBy(x => x.Start)
            .Select(x => $"[{TranscriptFormat.Clock(x.Start)}] {Shorten(x.Text, 140)}");
        var said = new Label { Text = string.Join(Environment.NewLine, lines), AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(3, 3, 12, 10) };

        var name = new ComboBox { Width = 200, DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
        name.Items.AddRange(Names());
        name.Text = s.Status switch
        {
            SpeakerStatus.Named or SpeakerStatus.Introduced or SpeakerStatus.Confirmed or SpeakerStatus.Maybe => s.Name ?? "",
            SpeakerStatus.Me => $"Me ({_settings.DisplayName})",
            SpeakerStatus.NotAPerson => NotAPersonChoice,
            _ => s.Hints.FirstOrDefault()?.Name ?? "",
        };
        var original = name.Text;

        bool canRemember = prints && s.Status != SpeakerStatus.TooLittle && _settings.Speakers.Enabled;
        var remember = new CheckBox { Text = "Remember", AutoSize = true, Enabled = canRemember, Checked = canRemember && s.Status is SpeakerStatus.Unknown or SpeakerStatus.Introduced };
        if (!canRemember)
            new ToolTip().SetToolTip(remember, !_settings.Speakers.Enabled ? "Speaker names are off (Settings → People)"
                : s.Status == SpeakerStatus.TooLittle ? "Too little clear audio to make a voiceprint"
                : "No voiceprint for this meeting (audio deleted)");
        var agreed = new CheckBox { Text = "They agreed", AutoSize = true, Visible = _settings.Speakers.AskConsent && canRemember };
        new ToolTip().SetToolTip(agreed, "Records that you say they agreed to their voice being remembered. It isn't their written consent where the law asks for that.");
        void Sync()
        {
            bool person = !string.IsNullOrWhiteSpace(name.Text) && name.Text != NotAPersonChoice;
            remember.Enabled = canRemember && person;
            agreed.Enabled = remember.Enabled && remember.Checked && !IsMe(name.Text);
        }
        name.TextChanged += (_, _) =>
        {
            Sync();
            RefreshLists(typedIn: name);
        };
        remember.CheckedChanged += (_, _) => Sync();
        Sync();

        Control[] cells = [voice, play, said, name, remember, agreed];
        for (int c = 0; c < cells.Length; c++) grid.Controls.Add(cells[c], c, row);
        _rows.Add(new Row(s, name, remember, agreed, original));
    }

    /// <summary>Everyone in the people folder and every name typed in this window, then you and "not a person".</summary>
    string[] Names()
    {
        var names = new List<string>(_known);
        foreach (var r in _rows)
        {
            var text = r.Name.Text.Trim();
            if (text.Length > 0 && text != NotAPersonChoice && !IsMe(text) && !names.Contains(text, StringComparer.OrdinalIgnoreCase))
                names.Add(text);
        }
        names.Sort(StringComparer.CurrentCultureIgnoreCase);
        return [.. names, $"Me ({_settings.DisplayName})", NotAPersonChoice];
    }

    /// <summary>
    /// Puts a name typed in one row in every other row's list, so the same person heard again as another voice can be picked
    /// rather than typed again. The box being typed in keeps its list while its suggestions are showing; it catches up when
    /// another row changes.
    /// </summary>
    void RefreshLists(ComboBox? typedIn)
    {
        if (_listing) return;
        _listing = true;
        try
        {
            var names = Names();
            foreach (var r in _rows)
                if (r.Name != typedIn) SetItems(r.Name, names);
        }
        finally { _listing = false; }
    }

    /// <summary>
    /// Changes a box's list item by item: Items.Clear() on a box that suggests from its list makes WinForms create the
    /// box's window again, which took 0.2–0.4 s a key typed with four other rows.
    /// </summary>
    static void SetItems(ComboBox box, string[] names)
    {
        var items = box.Items;
        if (items.Cast<string>().SequenceEqual(names)) return;
        var text = box.Text;
        box.BeginUpdate();
        try
        {
            // Both lists are in the same order, so what's left after taking out the old names is in order too.
            for (int i = items.Count - 1; i >= 0; i--)
                if (items[i] is not string old || !names.Contains(old)) items.RemoveAt(i);
            for (int i = 0; i < names.Length; i++)
                if (i >= items.Count || !Equals(items[i], names[i])) items.Insert(i, names[i]);
            if (!items.Cast<string>().SequenceEqual(names))
            {
                items.Clear();
                items.AddRange(names);
            }
            // The selected item found by its text again: before the box has a window, an insert doesn't move the selected
            // index along, and the window, once made, shows whichever name then sits there (Paul's meeting of 2026-10-21:
            // a voice he'd named reopened as "Sam" for "Sam Okafor", and Save stored it, the text having "changed").
            int at = box.FindStringExact(text);
            if (box.SelectedIndex != at) box.SelectedIndex = at;
            if (box.Text != text) box.Text = text;
        }
        finally { box.EndUpdate(); }
    }

    async Task SaveAsync()
    {
        var changes = new List<NameChange>();
        foreach (var r in _rows)
        {
            var text = r.Name.Text.Trim();
            // Only what the user touched: a prefilled "Dana?" stays a guess unless they change it or tick Remember.
            bool changed = !string.Equals(text, r.Original, StringComparison.Ordinal);
            bool remember = r.Remember.Enabled && r.Remember.Checked;
            if (!changed && !remember) continue;
            if (text.Length == 0 && !changed) continue;
            var label = Naming.DisplayLabel(r.Speaker);
            if (text == NotAPersonChoice || text.Length == 0)
                changes.Add(new NameChange(label, null, NotAPerson: true, Key: r.Speaker.Key));
            else
                changes.Add(new NameChange(label, IsMe(text) ? _settings.DisplayName : text, remember, Consent: r.Agreed.Visible && r.Agreed.Checked, Key: r.Speaker.Key));
        }
        if (changes.Count == 0)
        {
            DialogResult = DialogResult.OK;
            Close();
            return;
        }

        _save.Enabled = false;
        _status.Text = "Saving…";
        try
        {
            var namer = new SpeakerNamer(_settings, () => BackendFactory.Create(_settings, _secrets));
            var outcome = await namer.ApplyAsync(_rec, changes, CancellationToken.None);
            var notes = outcome.Messages.Where(m => !m.StartsWith("Note updated")).Distinct().ToList();
            if (notes.Count > 0) MessageBox.Show(this, string.Join(Environment.NewLine, notes), "Lemur", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _status.Text = "";
            _save.Enabled = true;
            MessageBox.Show(this, $"Couldn't save the names:\n{ex.Message}", "Lemur", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void Play(string track, ClipSpan clip)
    {
        try
        {
            _player?.Stop();
            _player?.Dispose();
            _player = new SoundPlayer(new MemoryStream(Wav.Encode(Wav.Read(track, clip.Start, clip.End))));
            _player.Play();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
        {
            _status.Text = "Couldn't play that clip.";
        }
    }

    void OpenNotes()
    {
        var path = _rec.LoadState().NotePath;
        if (path is null) return;
        var page = NoteWriter.HtmlPath(path);
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(File.Exists(page) ? page : path) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException) { _status.Text = "Couldn't open the notes."; }
    }

    bool IsMe(string text) =>
        text.Equals($"Me ({_settings.DisplayName})", StringComparison.OrdinalIgnoreCase) ||
        text.Equals(_settings.DisplayName, StringComparison.OrdinalIgnoreCase);

    static string StatusText(IdentifiedSpeaker s) => s.Status switch
    {
        SpeakerStatus.Named => $"Recognised as {s.Name}",
        SpeakerStatus.Introduced => s.Introduction is { } i ? $"Introduced as {i.Name} ({TranscriptFormat.Clock(i.Time)})" : $"Introduced as {s.Name}",
        SpeakerStatus.Maybe => $"Maybe {s.Name}?",
        SpeakerStatus.Confirmed => $"Named by you: {s.Name}",
        SpeakerStatus.Me => "You",
        SpeakerStatus.NotAPerson => "Not a person",
        SpeakerStatus.TooLittle => "Too little audio to recognise",
        SpeakerStatus.Conflict => "Sounds like two people",
        _ => s.Hints.FirstOrDefault() is { } h ? $"Unknown — someone said \"{Shorten(h.Quote, 40)}\"" : "Unknown",
    };

    static string TalkTime(double seconds) => seconds < 60 ? $"{seconds:0} s of talk" : $"{seconds / 60:0} min of talk";
    static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
