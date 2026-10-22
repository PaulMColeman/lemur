using Lemur.Config;
using Lemur.Logging;
using Lemur.Pipeline;
using Lemur.Speakers;

namespace Lemur.App.Windows;

/// <summary>
/// Every recording and what is happening to it (double-click the tray icon). Find a meeting by anything said in it (the
/// search box) and when it was (the date filter); each row shows its title and, under it, what it was about. Rows sit
/// under collapsible headings — Today, Yesterday, Earlier this week, Last week, then a month each (<see cref="RowGroups"/>)
/// — so months of meetings stay easy to get through; only the last two days start open. The selected one's steps show
/// as a timeline (StepsView); beside them, what it was about, who was there, where the search found it, and where its
/// files are, with "Keep recording" under them. From here: open the note, rename the meeting, name speakers, show the
/// files, retry now, make notes from an unfinished recording, keep its recording past the clean-up, delete, or import a
/// recording made elsewhere (Import…, or drop the file on the window).
/// Refreshes itself every two seconds while open.
/// </summary>
sealed class RecordingsForm : Form
{
    readonly Func<AppSettings> _settings;
    readonly Func<LiveRecording?> _live;
    readonly ProcessingQueue _queue;
    readonly Action<RecordingFolder> _name;
    readonly Action<string> _openNote;
    readonly Action _showSettings;
    readonly Func<IChatModel?> _chat;
    readonly Func<string, string, bool> _remember;
    readonly Func<string, bool, bool> _keepLive;
    readonly RecordingList _list = new();
    readonly MeetingSearch _search = new();
    readonly MeetingListView _view = new() { Dock = DockStyle.Fill };
    readonly TextBox _query = new() { PlaceholderText = "Search titles, notes and transcripts", Anchor = Ui.Wide, Margin = new Padding(0, 3, 8, 3) };
    readonly ComboBox _dates = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, Anchor = AnchorStyles.Left };
    readonly DateTimePicker _from = new() { Format = DateTimePickerFormat.Short, Width = 112, Anchor = AnchorStyles.Left, Visible = false };
    readonly DateTimePicker _to = new() { Format = DateTimePickerFormat.Short, Width = 112, Anchor = AnchorStyles.Left, Visible = false };
    readonly Label _and = new() { Text = "to", AutoSize = true, Anchor = AnchorStyles.Left, Visible = false };
    readonly Label _count = new() { AutoSize = true, Anchor = AnchorStyles.Right, ForeColor = SystemColors.GrayText, Margin = new Padding(12, 3, 3, 3) };
    readonly RichTextBox _detail = new()
    {
        ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control, ScrollBars = RichTextBoxScrollBars.Vertical,
        Dock = DockStyle.Fill, TabStop = false, DetectUrls = true, Margin = new Padding(8, 0, 0, 0),
    };
    readonly StepsView _steps = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0) };
    // Ticked by its Click (AutoCheck off), so unticking can ask first and a refresh setting it isn't taken for the user.
    readonly CheckBox _keep = new() { Text = "Keep recording", AutoSize = true, AutoCheck = false, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 0, 0) };
    readonly Label _keepNote = new() { AutoSize = true, Anchor = Ui.Wide, ForeColor = SystemColors.GrayText, Margin = new Padding(18, 0, 0, 0) };
    readonly Button _openNoteButton, _rename, _nameButton, _files, _retry, _recover, _delete;
    readonly ContextMenuStrip _menu = new();
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    readonly System.Windows.Forms.Timer _typing = new() { Interval = 250 };
    List<RecordingRow> _all = [];
    List<RecordingRow> _shown = [];
    SearchQuery _q = SearchQuery.None;
    string _detailShown = "";

    /// <summary>Headings the user opened (false) or closed (true), by <see cref="RowGroup.Key"/>, for as long as Lemur runs.</summary>
    static readonly Dictionary<string, bool> HeadingsClosed = new(StringComparer.Ordinal);

    /// <summary>The same while the search or the dates filter the list: what they find shows open, until the filter changes.</summary>
    readonly Dictionary<string, bool> _closedFiltering = new(StringComparer.Ordinal);

    /// <summary>The search and dates in force (null: none), to tell when they change.</summary>
    string? _filter;

    /// <summary>The list is being filled: headings opening and closing now aren't the user's doing.</summary>
    bool _building;

    /// <summary>Imports running from this window.</summary>
    int _importing;

    static readonly (string Name, float Weight)[] Columns = [("When", 1.25f), ("Meeting", 4.4f), ("Length", 0.65f), ("Status", 2.3f)];

    /// <param name="live">The recording being made now, if any (read on the UI thread).</param>
    /// <param name="name">Opens the naming window for a meeting.</param>
    /// <param name="openNote">Opens a note (its page if there is one).</param>
    /// <param name="showSettings">Opens the settings window.</param>
    /// <param name="chat">The summary model, for "Suggest a title" when renaming.</param>
    /// <param name="remember">Adds a correction (right spelling, what was written) to the vocabulary in settings; false if it couldn't.</param>
    /// <param name="keepLive">Ticks or unticks Keep recording for the recording being made now, by its folder; false if
    /// that isn't the one being made any more.</param>
    public RecordingsForm(Func<AppSettings> settings, Func<LiveRecording?> live, ProcessingQueue queue,
        Action<RecordingFolder> name, Action<string> openNote, Action showSettings, Func<IChatModel?> chat, Func<string, string, bool> remember,
        Func<string, bool, bool> keepLive)
    {
        _showSettings = showSettings;
        _settings = settings;
        _live = live;
        _queue = queue;
        _name = name;
        _openNote = openNote;
        _chat = chat;
        _remember = remember;
        _keepLive = keepLive;

        Text = "Recordings — Lemur (LLM Meeting Recorder)";
        Icon = Ui.AppIcon;
        Font = SystemFonts.MessageBoxFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(920, 560);
        Size = new Size(1100, 760);
        ShowInTaskbar = true;
        KeyPreview = true;

        foreach (var (column, _) in Columns) _view.Columns.Add(column);
        _view.ClientSizeChanged += (_, _) => SizeColumns();
        _view.SelectedIndexChanged += (_, _) => ShowSelected();
        // A double-click on a heading opens or closes it (and comes here too): only one on a meeting opens its note.
        _view.DoubleClick += (_, _) => { if (_view.HitTest(_view.PointToClient(Cursor.Position)).Item is { Selected: true }) OpenNote(); };
        _view.ContextMenuStrip = _menu;
        _view.GroupCollapsedStateChanged += (_, e) => HeadingToggled(e.GroupIndex);

        _openNoteButton = Ui.Button("Open note", OpenNote);
        _rename = Ui.Button("Rename…", Rename);
        _nameButton = Ui.Button("Name speakers…", () => { if (Selected is { } r) _name(new RecordingFolder(r.Path)); });
        _files = Ui.Button("Show files", () => { if (Selected is { } r && Directory.Exists(r.Path)) Ui.OpenFolder(r.Path); });
        _retry = Ui.Button("Retry now", RetryNow);
        _recover = Ui.Button("Make notes anyway", Recover);
        _delete = Ui.Button("Delete…", Delete);
        var import = Ui.Button("Import…", () => Import());
        new ToolTip().SetToolTip(import, "Make notes from a recording made somewhere else: your phone, a recorder, a video. You can also drop the file on this window.");
        var notesFolder = Ui.Button("Open notes folder", () => Ui.OpenFolder(_settings().NotesFolder));
        var settingsButton = Ui.Button("Settings…", () => _showSettings());
        var help = Ui.Button("Help", () => HelpLink.Open(section: "recordings", owner: this));
        _keep.Click += (_, _) => ToggleKeep();
        new ToolTip().SetToolTip(_keep, "Keep this meeting's audio, and its note's MP3, however old it gets: the clean-up in Settings → Notes leaves it alone.");
        BuildMenu();

        // A recording dropped on the window is imported.
        AllowDrop = _view.AllowDrop = true;
        foreach (Control target in new Control[] { this, _view })
        {
            target.DragEnter += (_, e) => e.Effect = Dropped(e.Data).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
            target.DragDrop += (_, e) =>
            {
                var files = Dropped(e.Data);
                if (files.Length > 0) BeginInvoke(() => Import(files)); // not while Explorer waits for the drop to finish
            };
        }

        // Search and dates, over the list.
        foreach (var range in DateRanges.All) _dates.Items.Add(DateRanges.Name(range));
        _dates.SelectedIndex = 0;
        _dates.SelectedIndexChanged += (_, _) =>
        {
            bool between = Range == DateRange.Between;
            if (between && !_from.Visible)
            {
                _from.Value = DateTime.Today.AddDays(-30);
                _to.Value = DateTime.Today;
            }
            _from.Visible = _and.Visible = _to.Visible = between;
            ShowRows();
        };
        _from.ValueChanged += (_, _) => ShowRows();
        _to.ValueChanged += (_, _) => ShowRows();
        _query.TextChanged += (_, _) =>
        {
            _typing.Stop();
            _typing.Start();
        };
        _query.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape && _query.TextLength > 0)
            {
                _query.Clear();
                e.SuppressKeyPress = e.Handled = true;
            }
            else if (e.KeyCode is Keys.Enter or Keys.Down && _view.Items.Count > 0)
            {
                Search();
                _view.Focus();
                e.SuppressKeyPress = e.Handled = true;
            }
        };
        _typing.Tick += (_, _) => Search();
        var fixWord = Ui.Button("Fix a word…", FixWord);
        fixWord.Margin = new Padding(10, 1, 0, 1);
        new ToolTip().SetToolTip(fixWord, "A word the transcription keeps getting wrong: fix it in the notes, and in every meeting from now on.");
        var find = new TableLayoutPanel { ColumnCount = 7, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 6) };
        find.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        for (int i = 0; i < 6; i++) find.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        find.Controls.Add(_query, 0, 0);
        find.Controls.Add(_dates, 1, 0);
        find.Controls.Add(_from, 2, 0);
        find.Controls.Add(_and, 3, 0);
        find.Controls.Add(_to, 4, 0);
        find.Controls.Add(_count, 5, 0);
        find.Controls.Add(fixWord, 6, 0);

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
        buttons.Controls.AddRange([_openNoteButton, _rename, _nameButton, _files, _retry, _recover, _delete]);
        var right = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Right, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
        right.Controls.AddRange([import, notesFolder, settingsButton, help]);
        var bottom = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(buttons, 0, 0);
        bottom.Controls.Add(right, 1, 0);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 260F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(find, 0, 0);
        layout.Controls.Add(_view, 0, 1);
        var lower = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = new Padding(0, 8, 0, 0) };
        lower.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56F));
        lower.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44F));
        lower.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        lower.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        lower.Controls.Add(_steps, 0, 0);
        lower.SetRowSpan(_steps, 2);
        lower.Controls.Add(_detail, 1, 0);
        // Under the meeting's details: Keep recording, and what it means for this meeting.
        var keep = new VStack { Dock = DockStyle.Fill, Margin = new Padding(8, 0, 0, 0) };
        keep.Add(_keep);
        keep.Add(_keepNote);
        lower.Controls.Add(keep, 1, 1);
        layout.Controls.Add(lower, 0, 2);
        layout.Controls.Add(bottom, 0, 3);
        Controls.Add(layout);

        _detail.LinkClicked += (_, e) => { if (e.LinkText is { } link && Ui.IsHttpUrl(link)) Ui.Shell(link); };
        _timer.Tick += (_, _) => RefreshRows();
        Shown += (_, _) =>
        {
            RefreshRows();
            _timer.Start();
            _view.Focus();
        };
        FormClosed += (_, _) =>
        {
            _timer.Stop();
            _typing.Stop();
        };
    }

    RecordingRow? Selected => _view.SelectedItems.Count == 1 ? _view.SelectedItems[0].Tag as RecordingRow : null;

    DateRange Range => DateRanges.All[Math.Max(0, _dates.SelectedIndex)];

    void SizeColumns()
    {
        int width = _view.ClientSize.Width;
        float total = Columns.Sum(c => c.Weight);
        for (int i = 0; i < Columns.Length; i++) _view.Columns[i].Width = (int)(width * Columns[i].Weight / total);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        bool inList = _view.Focused;
        switch (e.KeyData)
        {
            case Keys.Control | Keys.F:
                _query.Focus();
                _query.SelectAll();
                break;
            case Keys.F2:
                Rename();
                break;
            case Keys.F5:
                RefreshRows();
                break;
            case Keys.Enter when inList:
                OpenNote();
                break;
            case Keys.Delete when inList:
                Delete();
                break;
            case Keys.Left when inList && _view.SelectedItems.Count == 1 && _view.SelectedItems[0].Group is { } heading:
                heading.CollapsedState = ListViewGroupCollapsedState.Collapsed; // as in Explorer: Left closes the heading
                HeadingToggled(_view.Groups.IndexOf(heading));
                break;
            default:
                return;
        }
        e.Handled = e.SuppressKeyPress = true;
    }

    void Search()
    {
        _typing.Stop();
        var q = SearchQuery.Parse(_query.Text);
        if (q.Terms.SequenceEqual(_q.Terms)) return;
        _q = q;
        ShowRows();
    }

    /// <summary>Re-reads every recording; rows whose text changed are updated in place so the selection and scroll stay put.</summary>
    public void RefreshRows()
    {
        _view.TakeHeadingsFromWindows(); // before the list is filled again from what it remembers
        try
        {
            var s = _settings();
            _all = _list.Build(s.ResolvedRecordingsFolder, _live(), _queue.Current, DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            Log.Warn($"recordings window: could not read recordings: {ex.Message}");
            return;
        }
        ShowRows();
    }

    /// <summary>The rows the search and the dates let through, under their headings.</summary>
    void ShowRows()
    {
        var days = DateRanges.Days(Range, DateTime.Today, _from.Value, _to.Value);
        var shown = _all.Where(r => DateRanges.Contains(days, r.StartedAt) && _search.Matches(r, _q)).ToList();
        bool filtered = !_q.IsEmpty || days is not null;
        var filter = filtered ? string.Join("\u001f", _q.Terms) + "\u001f" + days : null;
        if (filter != _filter)
        {
            _filter = filter;
            _closedFiltering.Clear(); // a new search shows everything it finds
        }
        var groups = RowGroups.Of(shown, DateTime.Today);
        var rows = groups.SelectMany(g => g.Rows.Select(row => (Row: row, Group: g))).ToList();
        var selected = Selected?.Name;
        _view.BeginUpdate();
        _building = true;
        try
        {
            bool same = _view.Items.Count == rows.Count &&
                        groups.Select(g => g.Key).SequenceEqual(_view.Groups.Cast<ListViewGroup>().Select(g => g.Name)) &&
                        rows.Select((x, i) => (_view.Items[i].Tag as RecordingRow)?.Name == x.Row.Name && _view.Items[i].Group?.Name == x.Group.Key).All(b => b);
            if (same)
            {
                for (int g = 0; g < groups.Count; g++)
                    if (_view.Groups[g].Header != Heading(groups[g])) _view.Groups[g].Header = Heading(groups[g]);
                for (int i = 0; i < rows.Count; i++)
                {
                    var (row, group) = rows[i];
                    var item = _view.Items[i];
                    bool changed = item.Tag is not RecordingRow old || Look(old, group.Kind) != Look(row, group.Kind);
                    item.Tag = row;
                    if (!changed) continue;
                    var texts = Texts(row, group.Kind);
                    for (int c = 0; c < texts.Length; c++)
                        if (item.SubItems[c].Text != texts[c]) item.SubItems[c].Text = texts[c];
                    item.ForeColor = Colour(row);
                    item.ToolTipText = Tip(row);
                    _view.RedrawItems(i, i, invalidateOnly: true);
                }
            }
            else
            {
                _view.Items.Clear();
                _view.Groups.Clear();
                var headings = new Dictionary<string, ListViewGroup>(StringComparer.Ordinal);
                foreach (var group in groups)
                    _view.Groups.Add(headings[group.Key] = new ListViewGroup(group.Key, Heading(group))
                    {
                        CollapsedState = IsClosed(group) ? ListViewGroupCollapsedState.Collapsed : ListViewGroupCollapsedState.Expanded,
                    });
                foreach (var (row, group) in rows)
                {
                    var heading = headings[group.Key];
                    _view.Items.Add(new ListViewItem(Texts(row, group.Kind), heading)
                    {
                        Tag = row, ForeColor = Colour(row), ToolTipText = Tip(row),
                        Selected = row.Name == selected && heading.CollapsedState != ListViewGroupCollapsedState.Collapsed,
                    });
                }
                if (_view.SelectedItems.Count == 0 && _view.Items.Cast<ListViewItem>().FirstOrDefault(Showing) is { } first) first.Selected = true;
                _view.SelectedItems.Cast<ListViewItem>().FirstOrDefault()?.EnsureVisible();
            }
        }
        finally
        {
            _building = false;
            _view.EndUpdate();
        }
        _shown = [.. rows.Select(x => x.Row)];
        _count.Text = (_importing > 0 ? "Importing… · " : "") + (filtered ? $"{shown.Count} of {Meetings(_all.Count)}" : Meetings(_all.Count));
        _view.EmptyText = shown.Count > 0 ? null
            : _all.Count == 0 ? "No recordings yet.\nLemur records your calls by itself. For anything else, right-click its tray icon and choose Start recording now."
            : !_q.IsEmpty ? $"No meetings match \"{_query.Text.Trim()}\"{(days is null ? "" : $" ({DateRanges.Name(Range).ToLowerInvariant()})")}."
            : "No meetings in these dates.";
        ShowSelected();
    }

    static string Meetings(int n) => n == 1 ? "1 meeting" : $"{n} meetings";

    /// <summary>A heading's text: "Last week   ·   6 meetings · 3 h 40 min".</summary>
    static string Heading(RowGroup g) => $"{g.Title}   ·   {g.Summary}";

    /// <summary>Whether a heading shows closed: as the user left it, or else only the last two days open — all of it
    /// open while searching, so nothing found is hidden.</summary>
    bool IsClosed(RowGroup g)
    {
        var states = _filter is null ? HeadingsClosed : _closedFiltering;
        return states.TryGetValue(g.Key, out var closed) ? closed : _filter is null && !g.OpenAtFirst;
    }

    static bool Showing(ListViewItem item) => item.Group?.CollapsedState != ListViewGroupCollapsedState.Collapsed;

    /// <summary>The user opened or closed a heading: remembered, and a meeting it hid is no longer the one selected.</summary>
    void HeadingToggled(int index)
    {
        if (_building || index < 0 || index >= _view.Groups.Count) return;
        var group = _view.Groups[index];
        bool closed = group.CollapsedState == ListViewGroupCollapsedState.Collapsed;
        if (group.Name is { } key) (_filter is null ? HeadingsClosed : _closedFiltering)[key] = closed;
        if (closed && _view.SelectedItems.Count == 1 && _view.SelectedItems[0].Group == group)
        {
            _view.SelectedItems[0].Selected = false;
            ShowSelected();
        }
    }

    /// <summary>Opens or closes every heading (the list's right-click menu).</summary>
    void SetAll(bool closed)
    {
        _building = true;
        try
        {
            foreach (ListViewGroup group in _view.Groups)
            {
                group.CollapsedState = closed ? ListViewGroupCollapsedState.Collapsed : ListViewGroupCollapsedState.Expanded;
                if (group.Name is { } key) (_filter is null ? HeadingsClosed : _closedFiltering)[key] = closed;
            }
        }
        finally
        {
            _building = false;
        }
        if (closed && _view.SelectedItems.Count > 0) _view.SelectedItems[0].Selected = false;
        ShowSelected();
    }

    /// <summary>Everything a row shows: when it changes, the row is drawn again.</summary>
    static string Look(RecordingRow r, GroupKind kind) =>
        string.Join("\u001f", Texts(r, kind)) + $"\u001f{r.Kind}\u001f{r.Untitled}\u001f{MeetingListView.Subtitle(r)}";

    static string[] Texts(RecordingRow r, GroupKind kind) =>
    [
        When(r.StartedAt, kind),
        r.Title,
        r.Seconds > 0 ? RecordingList.Clock(TimeSpan.FromSeconds(Math.Round(r.Seconds))) : "",
        r.Status,
    ];

    static string Tip(RecordingRow r) => MeetingListView.Subtitle(r) is { } under ? $"{r.Title}\n{under}" : r.Title;

    static Color Colour(RecordingRow r) => r.Kind switch
    {
        RowKind.Recording => Color.FromArgb(192, 32, 32),
        RowKind.Retrying or RowKind.Interrupted => Color.FromArgb(166, 77, 0),
        RowKind.Done => SystemColors.WindowText,
        _ => Color.FromArgb(11, 98, 196),
    };

    /// <summary>When a meeting began, as much of it as its heading doesn't already say.</summary>
    static string When(DateTimeOffset? at, GroupKind kind)
    {
        if (at is null) return "";
        var local = at.Value.ToLocalTime();
        return kind switch
        {
            GroupKind.Today or GroupKind.Yesterday => $"{local:HH:mm}",
            GroupKind.ThisWeek or GroupKind.LastWeek => $"{local:ddd HH:mm}",
            _ => $"{local:ddd d MMM HH:mm}",
        };
    }

    void ShowSelected()
    {
        var r = Selected;
        ShowDetail(r);
        bool estimating = r is not null && r.Steps.Any(l => l.State is StepState.Next or StepState.Working or StepState.Failed);
        int meetings = _list.Estimates.Meetings;
        var basis = !estimating ? null
            : meetings == 0 ? "Times to come are rough guesses from the first test meetings; they adjust to yours as meetings finish."
            : $"Times to come are rough guesses, from {meetings} of your {(meetings == 1 ? "meeting" : "meetings")} so far.";
        _steps.SetSteps(r?.Steps ?? [], r?.Expected, basis,
            r is { Kind: RowKind.Interrupted } ? "This recording was never finished, so it has no steps yet. \"Make notes anyway\" starts them."
            : r is null ? "Select a recording to see its steps." : null);
        _openNoteButton.Enabled = r is { HasNote: true };
        _rename.Enabled = CanRename(r);
        _nameButton.Enabled = r is { Kind: RowKind.Done } && File.Exists(new RecordingFolder(r.Path).IdentifiedFile);
        _files.Enabled = r is not null;
        _retry.Visible = r is not { Kind: RowKind.Interrupted };
        _retry.Enabled = r is { CanRetry: true };
        _recover.Visible = r is { Kind: RowKind.Interrupted };
        _delete.Enabled = r is { Kind: not (RowKind.Recording or RowKind.Working) };
        _keep.Enabled = r is { CanKeep: true };
        _keep.Checked = r is { Keep: true };
        var note = r is null or { Kind: RowKind.Interrupted } ? "" : KeepNote(r);
        if (_keepNote.Text != note) _keepNote.Text = note;
    }

    /// <summary>What Keep recording does for this meeting, as it is now: under the box.</summary>
    string KeepNote(RecordingRow r)
    {
        if (!r.CanKeep) return "Nothing left to keep: its audio is gone, and its note has no MP3.";
        var s = _settings();
        bool mp3s = s.NoteMp3.DeleteWithAudio;
        if (!r.HasAudio)
            return r.Keep ? "Its working audio had been deleted already, so its note's MP3 is what's kept."
                : mp3s ? "Its working audio has been deleted. Ticking this keeps its note's MP3."
                : "Its working audio has been deleted. Ticking this keeps its note's MP3 if MP3s are deleted later.";
        if (s.KeepAudioDays < 0) return "Settings keep all audio forever; this keeps it if that changes.";
        return r.Keep ? (mp3s ? "Its audio and its note's MP3 stay, however old it gets." : "Its audio stays, however old it gets.")
            : $"Its audio{(mp3s ? " and its note's MP3 are" : " is")} deleted {Days(s.KeepAudioDays)} after its notes are made.";
    }

    static string Days(int n) => n == 1 ? "1 day" : $"{n} days";

    /// <summary>
    /// Ticks or unticks Keep recording for the selected meeting, even one still being recorded or made into notes. Unticking
    /// one already past the days asks first: what it still has goes at the next clean-up.
    /// </summary>
    void ToggleKeep()
    {
        if (Selected is not { CanKeep: true } r) return;
        bool keep = !r.Keep;
        if (!keep && Goes(r) is { } what &&
            !Ui.Confirm(this, $"\"{r.Title}\" is more than {Days(_settings().KeepAudioDays)} old, so {what} will be deleted at the next " +
                              "clean-up, within a few minutes.\n\nStop keeping it?", warning: true))
            return;
        var rec = new RecordingFolder(r.Path);
        try
        {
            // Being recorded: it has no meta.json yet, so the recording holds the tick until it stops.
            if (r.Kind != RowKind.Recording || !_keepLive(r.Path, keep))
            {
                if (!File.Exists(rec.MetaFile))
                {
                    Ui.Info(this, "This recording is stopping right now. Try again in a moment.");
                    return;
                }
                rec.UpdateMeta(m => m.KeepRecording = keep);
            }
            Log.Info($"{r.Name}: {(keep ? "kept" : "no longer kept")} (Keep recording, in the Recordings window)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            Ui.Error(this, $"That couldn't be changed. {ex.Message}");
        }
        RefreshRows();
    }

    /// <summary>What the next clean-up deletes of a meeting no longer kept: null when nothing, or not yet.</summary>
    string? Goes(RecordingRow r)
    {
        var s = _settings();
        if (r.Kind != RowKind.Done || !AudioCleanUp.PastDays(r.CompletedAt, s.KeepAudioDays, DateTimeOffset.Now)) return null;
        bool mp3 = s.NoteMp3.DeleteWithAudio && r.HasMp3;
        return r.HasAudio && mp3 ? "its audio and its note's MP3" : r.HasAudio ? "its audio" : mp3 ? "its note's MP3" : null;
    }

    /// <summary>Not while it is being recorded (it has no meta.json yet) or its note is being written this moment.</summary>
    bool CanRename(RecordingRow? r) =>
        r is { Kind: not (RowKind.Recording or RowKind.Interrupted) } &&
        !(r.Kind == RowKind.Working && _queue.Current is { Stage: Stage.Summarized });

    /// <summary>
    /// Beside the steps: the meeting's title, when and where, what it was about, who was there, the lines the search
    /// found, then the details of its state (the note's path, the transcript's page, the last error).
    /// </summary>
    void ShowDetail(RecordingRow? r)
    {
        var parts = new List<(string Text, Font? Font, Color? Colour)>();
        if (r is null)
        {
            parts.Add(("Select a meeting to see what it was about.", null, SystemColors.GrayText));
        }
        else
        {
            parts.Add((r.Title + "\n", Ui.HeadingFont, r.Untitled ? SystemColors.GrayText : null));
            var when = r.StartedAt is { } at ? at.ToLocalTime().ToString("dddd d MMMM yyyy, HH:mm") : null;
            var length = r.Seconds >= 60 ? $"{Math.Round(r.Seconds / 60):0} min" : r.Seconds > 0 ? $"{r.Seconds:0} s" : null;
            parts.Add((string.Join(" · ", new[] { when, length, r.Source }.Where(s => !string.IsNullOrEmpty(s))) + "\n", null, SystemColors.GrayText));
            if (r.Untitled && r.Kind != RowKind.Interrupted)
            {
                var from = r.Source == RecordingImport.Source ? "No title from its file" : "No title from the call app";
                parts.Add((r.Kind == RowKind.Done
                    ? $"{from}. Rename… gives it one (the notes suggest a title).\n"
                    : $"{from}: its notes will suggest one. You can also rename it.\n", null, SystemColors.GrayText));
            }
            if (!string.IsNullOrWhiteSpace(r.Description) && r.Kind != RowKind.Interrupted) parts.Add(("\n" + r.Description + "\n", null, null));
            if (r.People.Count > 0) parts.Add(("\nWith " + string.Join(", ", r.People) + "\n", null, null));
            if (!_q.IsEmpty)
            {
                var lines = _search.Lines(r, _q);
                if (lines.Count > 0)
                {
                    int total = _search.Count(r, _q);
                    parts.Add(("\nFound in the notes" + (total > lines.Count ? $" ({total} places; the first {lines.Count}):" : ":") + "\n", Ui.BoldFont, null));
                    foreach (var line in lines) parts.Add(("• " + line + "\n", null, null));
                }
            }
            parts.Add(("\n" + r.Detail.ReplaceLineEndings("\n") + "\n", null, SystemColors.GrayText));
        }
        var key = string.Concat(parts.Select(p => p.Text + "\u001f" + p.Font?.Name + p.Font?.Style + p.Colour));
        if (key == _detailShown) return;
        _detailShown = key;
        _detail.Clear();
        foreach (var (text, font, colour) in parts)
        {
            _detail.SelectionStart = _detail.TextLength;
            _detail.SelectionLength = 0;
            _detail.SelectionFont = font ?? _detail.Font;
            _detail.SelectionColor = colour ?? SystemColors.ControlText;
            _detail.AppendText(text);
        }
        _detail.SelectionStart = 0;
        _detail.ScrollToCaret();
    }

    /// <summary>Selects a recording by its folder name (a toast about it was clicked); a search hiding it is cleared.</summary>
    public void SelectRecording(string name)
    {
        RefreshRows();
        if (!_shown.Any(r => r.Name == name) && _all.Any(r => r.Name == name))
        {
            _query.Clear();
            _q = SearchQuery.None;
            _dates.SelectedIndex = 0; // shows the rows again
            ShowRows();
        }
        foreach (ListViewItem item in _view.Items)
        {
            if (item.Tag is not RecordingRow row || row.Name != name) continue;
            if (item.Group is { CollapsedState: ListViewGroupCollapsedState.Collapsed } heading)
            {
                heading.CollapsedState = ListViewGroupCollapsedState.Expanded; // the user asked to see it
                if (heading.Name is { } key) (_filter is null ? HeadingsClosed : _closedFiltering)[key] = false;
            }
            item.Selected = true;
            item.Focused = true;
            item.EnsureVisible();
            break;
        }
        ShowSelected();
    }

    void BuildMenu()
    {
        ToolStripMenuItem Item(string text, Action action, Keys keys = Keys.None) =>
            new(text, null, (_, _) => action()) { ShortcutKeys = keys, ShowShortcutKeys = keys != Keys.None };
        var open = Item("Open note", OpenNote);
        open.Font = new Font(_menu.Font, FontStyle.Bold);
        var rename = Item("Rename…", Rename, Keys.F2);
        var name = Item("Name speakers…", () => _nameButton.PerformClick());
        var files = Item("Show files", () => _files.PerformClick());
        var retry = Item("Retry now", RetryNow);
        var recover = Item("Make notes anyway", Recover);
        var delete = Item("Delete…", Delete, Keys.Delete);
        var keep = Item("Keep recording", ToggleKeep);
        var fixWord = Item("Fix a word…", FixWord);
        var headings = new ToolStripSeparator();
        var expand = Item("Expand all", () => SetAll(closed: false));
        var collapse = Item("Collapse all", () => SetAll(closed: true));
        ToolStripItem[] meeting = [open, rename, name, files, fixWord, new ToolStripSeparator(), retry, recover, new ToolStripSeparator(), keep, delete];
        _menu.Items.AddRange([.. meeting, headings, expand, collapse]);
        _menu.Opening += (_, e) =>
        {
            if (_view.Groups.Count == 0)
            {
                e.Cancel = true;
                return;
            }
            // Over a heading or empty space, only the headings' commands.
            bool any = Selected is not null;
            foreach (var item in meeting) item.Visible = any;
            headings.Visible = any;
            if (!any) return;
            ShowSelected();
            open.Enabled = _openNoteButton.Enabled;
            rename.Enabled = _rename.Enabled;
            name.Enabled = _nameButton.Enabled;
            files.Enabled = _files.Enabled;
            retry.Visible = _retry.Visible;
            retry.Enabled = _retry.Enabled;
            recover.Visible = _recover.Visible;
            keep.Enabled = _keep.Enabled;
            keep.Checked = _keep.Checked;
            delete.Enabled = _delete.Enabled;
        };
    }

    void OpenNote()
    {
        if (Selected is { HasNote: true } r) _openNote(r.NotePath!);
    }

    void Rename()
    {
        if (Selected is not { } r || !CanRename(r)) return;
        var rec = new RecordingFolder(r.Path);
        RecordingMeta meta;
        try
        {
            meta = rec.LoadMeta();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            Ui.Error(this, $"This meeting can't be renamed: {ex.Message}");
            return;
        }
        using var dialog = new RenameDialog(rec, meta, r.NotePath, _chat);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.ChosenTitle is not { } title) return;
        try
        {
            MeetingRename.Apply(rec, title);
            Log.Info($"{r.Name}: renamed in the Recordings window");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException
                                       or System.Text.Json.JsonException)
        {
            Log.Warn($"{r.Name}: could not rename: {ex.Message}");
            Ui.Error(this, $"It couldn't be renamed. {ex.Message}");
        }
        RefreshRows();
    }

    /// <summary>
    /// A word the transcription got wrong: fixed in the finished meetings that have it, and remembered for new ones. What
    /// was searched for fills it in, since that's how a misheard word is usually found.
    /// </summary>
    void FixWord()
    {
        var settings = _settings();
        var searched = _query.Text.Trim().Trim('"').Trim();
        var heard = searched.Length > 0 ? searched : null; // a misheard phrase is often two words ("Cooper Netties")
        using var dialog = new FixWordDialog(heard, settings.Vocabulary.Select(e => e.Term),
            vocabulary => [.. _all.Where(r => r.Kind == RowKind.Done && _search.Mentions(r, vocabulary))]);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        // Before any meeting changes: a word the vocabulary would refuse left meetings fixed and new ones not (2026-10-14).
        if (dialog.Remember && Transcripts.Vocabulary.WhyNot(settings.Vocabulary, dialog.Spelling, dialog.Heard) is { } problem)
        {
            Ui.Error(this, $"That can't go in your vocabulary: {problem} Nothing was changed.");
            return;
        }

        var meetings = dialog.Meetings;
        (int Done, int Failed) result = (0, 0);
        if (meetings.Count > 0)
        {
            UseWaitCursor = true;
            try
            {
                result = MeetingFixes.ApplyAll(settings.ResolvedRecordingsFolder, dialog.Correction, settings.DisplayName,
                    meetings.Select(r => new RecordingFolder(r.Path)), settings.MeetingFile.IncludeTranscript);
            }
            finally
            {
                UseWaitCursor = false;
            }
            Log.Info($"vocabulary: a word fixed in {result.Done} meetings from the Recordings window{(result.Failed > 0 ? $", {result.Failed} couldn't be" : "")}");
        }
        bool remembered = dialog.Remember && _remember(dialog.Spelling, dialog.Heard);

        // Show where it's fixed: a search for what it wrote now finds what it says.
        if (heard is not null && string.Equals(heard, dialog.Heard, StringComparison.OrdinalIgnoreCase)) _query.Text = dialog.Spelling;
        RefreshRows();
        var done = result.Done == 0 ? "" : $"Fixed in {result.Done} {(result.Done == 1 ? "meeting" : "meetings")}. ";
        var failed = result.Failed == 0 ? "" : $"{result.Failed} couldn't be changed: a note may be open in another program. ";
        var newOnes = remembered ? $"New meetings will say \"{dialog.Spelling}\" too." : "";
        if (done.Length + failed.Length + newOnes.Length > 0) Ui.Info(this, (done + failed + newOnes).Trim());
    }

    void RetryNow()
    {
        if (Selected is not { CanRetry: true } r) return;
        _queue.RetryNow(r.Name);
        Ui.Info(this, "It will be tried again as soon as the meeting being worked on now (if any) is finished.");
    }

    void Recover()
    {
        if (Selected is not { Kind: RowKind.Interrupted } r) return;
        try
        {
            var meta = new RecordingFolder(r.Path).Recover();
            Log.Info($"{r.Name}: unfinished recording recovered ({meta.DurationSeconds:0}s) at the user's request");
            _queue.Kick();
        }
        catch (Exception ex)
        {
            Log.Warn($"{r.Name}: could not recover: {ex.Message}");
            Ui.Error(this, $"This recording couldn't be recovered: {ex.Message}");
        }
        RefreshRows();
    }

    void Delete()
    {
        if (Selected is not { Kind: not (RowKind.Recording or RowKind.Working) } r) return;
        var question = r.Kind == RowKind.Done
            ? $"Delete the working files of \"{r.Title}\" (audio, transcript, speaker data)?\n\nIts note stays where it is. You won't be able to name its speakers afterwards."
            : $"Delete \"{r.Title}\"?\n\nIt hasn't been turned into notes, so nothing of this meeting will be kept.";
        if (!Ui.Confirm(this, question, warning: true)) return;
        try
        {
            new RecordingFolder(r.Path).Delete();
            Log.Info($"{r.Name}: deleted from the Recordings window");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Error(this, $"It couldn't be deleted (a file is in use?): {ex.Message}");
        }
        RefreshRows();
    }

    /// <summary>The files dragged over the window, if any are recordings.</summary>
    static string[] Dropped(IDataObject? data) =>
        data?.GetData(DataFormats.FileDrop) is string[] files ? [.. files.Where(f => File.Exists(f) && RecordingImport.LooksLikeAudio(f))] : [];

    /// <summary>
    /// Makes notes from recordings made elsewhere (Import…, a file dropped on the window, the tray menu). One file opens
    /// the Import window to check its title and start time; several are imported as their files say, to rename after.
    /// </summary>
    /// <param name="files">The files; null asks for them.</param>
    public async void Import(string[]? files = null)
    {
        files ??= PickRecordings();
        if (files is null || files.Length == 0) return;
        var recordings = files.Where(RecordingImport.LooksLikeAudio).ToArray();
        if (recordings.Length == 0)
        {
            Ui.Warn(this, "Lemur makes notes from recordings: sound files like .m4a, .mp3 or .wav, or a video's sound (.mp4, .mov).");
            return;
        }
        try
        {
            if (recordings.Length == 1) await ImportOne(recordings[0]);
            else await ImportMany(recordings);
        }
        catch (ObjectDisposedException) { } // the window was closed meanwhile; the import itself finished
    }

    string[]? PickRecordings()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Import a recording",
            Multiselect = true,
            Filter = $"Recordings|{string.Join(";", RecordingImport.Extensions.Select(e => "*" + e))}|All files (*.*)|*.*",
        };
        if (Ui.Blank(_settings().Import.Folder) is { } folder && Directory.Exists(folder)) dialog.InitialDirectory = folder;
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileNames : null;
    }

    async Task ImportOne(string path)
    {
        var root = _settings().ResolvedRecordingsFolder;
        ImportGuess guess;
        RecordingFolder? earlier;
        Busy(+1);
        try
        {
            (guess, earlier) = await Task.Run(() => (RecordingImport.Guess(path), RecordingImport.FindImported(root, RecordingImport.Sha256Of(path))));
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            Unexpected(ex);
            Ui.Error(this, $"{Path.GetFileName(path)} can't be imported: {Reason(ex)}");
            return;
        }
        finally
        {
            Busy(-1);
        }
        if (earlier is not null)
        {
            if (!Ui.Confirm(this, $"This recording has been imported already: it's \"{TitleOf(earlier)}\". Import it again as another meeting?"))
            {
                SelectRecording(earlier.Name);
                return;
            }
        }
        string title;
        DateTimeOffset started;
        using (var dialog = new ImportDialog(path, guess))
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            (title, started) = (dialog.ChosenTitle, dialog.StartedAt);
        }
        RecordingFolder? made = null;
        Busy(+1);
        try
        {
            made = await Task.Run(() => RecordingImport.Import(root, path, title, started));
            Log.Info($"{made.Name}: imported from the Recordings window");
            _queue.Kick();
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            Unexpected(ex);
            Ui.Error(this, $"{Path.GetFileName(path)} couldn't be imported: {Reason(ex)}");
        }
        finally
        {
            Busy(-1);
        }
        if (made is not null) SelectRecording(made.Name);
    }

    async Task ImportMany(string[] paths)
    {
        if (!Ui.Confirm(this, $"Make notes from these {paths.Length} recordings? Each is named and dated from its file; you can rename them afterwards."))
            return;
        var root = _settings().ResolvedRecordingsFolder;
        var problems = new List<string>();
        int imported = 0, already = 0;
        string? last = null;
        Busy(+1);
        try
        {
            foreach (var path in paths)
            {
                try
                {
                    var rec = await Task.Run(() => RecordingImport.FindImported(root, RecordingImport.Sha256Of(path)) is null
                        ? RecordingImport.Import(root, path)
                        : null);
                    if (rec is null)
                    {
                        already++;
                        continue;
                    }
                    imported++;
                    last = rec.Name;
                    _queue.Kick();
                }
                catch (Exception ex) when (ex is not ObjectDisposedException)
                {
                    Unexpected(ex);
                    problems.Add($"{Path.GetFileName(path)}: {Reason(ex)}");
                }
            }
            Log.Info($"{imported} of {paths.Length} recordings imported from the Recordings window");
        }
        finally
        {
            Busy(-1);
        }
        if (last is not null) SelectRecording(last);
        if (already > 0 || problems.Count > 0)
            Ui.Info(this, $"Imported {imported} of {paths.Length}." +
                          (already > 0 ? $" {already} had been imported already." : "") +
                          (problems.Count > 0 ? "\n\nThese couldn't be:\n• " + string.Join("\n• ", problems) : ""));
    }

    void Busy(int change)
    {
        _importing += change;
        if (!IsDisposed) ShowRows();
    }

    static string Reason(Exception ex) => ex is FileNotFoundException ? "the file isn't there any more." : ex.Message;

    /// <summary>A failure that isn't about the file (a bug, Windows' decoders misbehaving): logged in full.</summary>
    static void Unexpected(Exception ex)
    {
        if (ex is ImportException) Log.Warn($"import: {ex.Message}");
        else if (ex is IOException or UnauthorizedAccessException) Log.Warn($"import: {ex.GetType().Name} 0x{ex.HResult:X8}"); // its message names the file
        else Log.Error("import failed", ex);
    }

    static string TitleOf(RecordingFolder rec)
    {
        try
        {
            var meta = rec.LoadMeta();
            return string.IsNullOrWhiteSpace(meta.Title) ? MeetingAbout.Placeholder(meta.Source) : meta.Title;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            return rec.Name;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _typing.Dispose();
            _menu.Dispose();
        }
        base.Dispose(disposing);
    }
}
