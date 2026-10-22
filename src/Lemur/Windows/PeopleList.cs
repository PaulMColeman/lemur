using System.ComponentModel;
using System.Media;
using Lemur.Config;
using Lemur.Speakers;

namespace Lemur.App.Windows;

/// <summary>
/// The people list (design 7.3): everyone in a people folder, and the selected person's voiceprints.
/// Changes are written to the folder at once — it is shared with other PCs, not part of settings.json.
/// </summary>
sealed class PeopleList : TableLayoutPanel
{
    readonly ListView _people = NewList(170, [("Name", 2f), ("Meetings", 0.9f), ("Last heard", 1.1f), ("Voiceprints", 1.25f), ("Consent", 1.6f)]);
    readonly ListView _prints = NewList(110, [("Kind", 0.8f), ("Meeting", 2.4f), ("Date", 1.4f), ("Length", 0.8f), ("Confirmed", 1f)]);
    readonly Label _printsTitle = new() { AutoSize = true, Anchor = AnchorStyles.Left, Font = Ui.BoldFont, Margin = new Padding(0, 10, 3, 4) };
    readonly Label _status = Ui.Muted("");
    readonly Button _rename, _merge, _delete, _sample, _play, _deletePrint;
    readonly CheckBox _agreed = Ui.Check("They agreed");
    PeopleStore? _store;
    List<Person> _all = [];
    SoundPlayer? _player;
    bool _loading;

    public PeopleList()
    {
        ColumnCount = 2;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Anchor = Ui.Wide;
        Margin = new Padding(0, 6, 0, 0);

        _rename = Ui.Button("Rename…", () => Act(Rename));
        _merge = Ui.Button("Merge into…", () => Act(Merge));
        _delete = Ui.Button("Delete…", () => Act(Delete));
        _sample = Ui.Button("Add sample from file…", AddSample);
        _play = Ui.Button("▶ Play", Play);
        _deletePrint = Ui.Button("Delete voiceprint…", () => Act(DeletePrint));

        Put(new Label { Text = "People you've named", AutoSize = true, Font = Ui.HeadingFont, Anchor = AnchorStyles.Left, Margin = new Padding(0, 14, 3, 6) }, 0);
        int row = Put(_people, 0);
        Controls.Add(Buttons(_rename, _merge, _delete, _sample, _agreed), 1, row);
        Put(_status, 0);
        Put(_printsTitle, 0);
        row = Put(_prints, 0);
        Controls.Add(Buttons(_play, _deletePrint), 1, row);
        Put(Ui.Muted("A tick in 'agreed' records that you say they agreed; it isn't their written consent where the law asks for it."), 0);

        _people.SelectedIndexChanged += (_, _) => ShowPerson();
        _people.DoubleClick += (_, _) => { if (Selected() is not null) Act(Rename); };
        _prints.SelectedIndexChanged += (_, _) => UpdateButtons();
        _agreed.CheckedChanged += (_, _) => { if (!_loading) Act(SetConsent); };
        UpdateButtons();
    }

    /// <summary>The settings being edited (voiceprint server, consent question), for Add sample.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<AppSettings>? CurrentSettings { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ISecretStore? Secrets { get; set; }

    /// <summary>
    /// Add sample from file (design 7.3): a WAV, MP3 or M4A of the selected person talking alone. Remembering
    /// someone else's voice goes through the consent question like naming does (design D3).
    /// </summary>
    async void AddSample()
    {
        if (_store is null || Selected() is not { } person || CurrentSettings?.Invoke() is not { } settings || Secrets is null) return;
        if (!settings.Speakers.Enabled)
        {
            Ui.Info(this, "Turn on speaker names above first: a sample needs the voiceprint model.");
            return;
        }
        if (!person.IsMe && settings.Speakers.AskConsent && person.Consent is null)
        {
            if (!Ui.Confirm(this, $"Did {person.Name} agree to you keeping a voiceprint of their voice?")) return;
            person.Consent = new Consent { At = DateTimeOffset.Now, Note = "agreed when a sample was added" };
        }
        using var pick = new OpenFileDialog
        {
            Title = $"A recording of {Display(person)} talking alone",
            Filter = "Audio (*.wav;*.mp3;*.m4a)|*.wav;*.mp3;*.m4a|All files|*.*",
        };
        if (pick.ShowDialog(FindForm()) != DialogResult.OK) return;

        var client = VoiceprintClient.Create(settings, Secrets, out var whyNot);
        if (client is null)
        {
            Ui.Info(this, $"The voiceprint model isn't set up: {whyNot}.");
            return;
        }
        _sample.Enabled = false;
        _status.Text = "Listening to the sample…";
        _status.Visible = true;
        try
        {
            using (client)
            {
                var check = await SampleImporter.CheckAsync(pick.FileName, client, _store, settings.Speakers.Model, CancellationToken.None);
                if (check.MaybeTwoVoices && !Ui.Confirm(this, "This file may have more than one voice in it. Use it anyway?", warning: true)) return;
                var kind = !person.IsMe && Ui.Confirm(this, "Did this come from a call? (No for a voice memo or a recording made in the room.)")
                    ? VoiceKind.Call : VoiceKind.Local;
                bool kept = SampleImporter.Add(_store, person, check, pick.FileName, kind, DateTimeOffset.Now);
                Ui.Info(this, kept
                    ? $"Added {check.SpeechSeconds:0} seconds of {Display(person)}'s voice."
                    : $"This sounds like what's already kept for {Display(person)}; nothing new to add.");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Ui.Error(this, $"Couldn't use that file: {ex.Message}");
        }
        finally
        {
            _sample.Enabled = true;
            Reload(person.Id);
        }
    }

    public void ShowFolder(string folder)
    {
        _store = new PeopleStore(folder);
        Reload();
    }

    public void Reload(string? selectId = null)
    {
        selectId ??= Selected()?.Id;
        string status;
        try
        {
            _all = _store?.People().ToList() ?? [];
            status = _store is null || !Directory.Exists(_store.Folder)
                ? "There's no people folder here yet; it's made when the first voice is remembered."
                : _all.All(p => p.IsMe) ? "Nobody named yet. People appear here once you name a voice after a meeting." : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _all = [];
            status = $"The people folder can't be read: {ex.Message}";
        }
        _status.Text = status;
        _status.Visible = status.Length > 0;

        _people.BeginUpdate();
        _people.Items.Clear();
        if (!_all.Any(p => p.IsMe))
            _people.Items.Add(new ListViewItem(["You", "—", "—", "none yet", ""]) { ForeColor = SystemColors.GrayText });
        foreach (var p in _all.OrderByDescending(p => p.IsMe).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            _people.Items.Add(new ListViewItem(Columns(p)) { Tag = p, Selected = p.Id == selectId });
        _people.EndUpdate();
        ShowPerson();
    }

    Person? Selected() => _people.SelectedItems.Count > 0 ? _people.SelectedItems[0].Tag as Person : null;

    Voiceprint? SelectedPrint() => _prints.SelectedItems.Count > 0 ? _prints.SelectedItems[0].Tag as Voiceprint : null;

    void ShowPerson()
    {
        var p = Selected();
        _prints.BeginUpdate();
        _prints.Items.Clear();
        foreach (var v in p?.Prints.OrderByDescending(v => v.From?.At ?? v.Added) ?? Enumerable.Empty<Voiceprint>())
            _prints.Items.Add(new ListViewItem([
                v.Kind == VoiceKind.Call ? "call" : "local",
                v.From?.Title is { Length: > 0 } title ? title : v.From?.Recording ?? "—",
                Date(v.From?.At ?? v.Added),
                $"{v.Seconds:0} s",
                v.Confirmed ? "yes" : "automatic",
            ]) { Tag = v });
        _prints.EndUpdate();
        _printsTitle.Text = p is null ? "Voiceprints" : $"Voiceprints of {(p.IsMe ? "you" : p.Name)}";
        _loading = true;
        _agreed.Checked = p?.Consent is not null;
        _loading = false;
        UpdateButtons();
    }

    void UpdateButtons()
    {
        var p = Selected();
        var v = SelectedPrint();
        _rename.Enabled = _delete.Enabled = _sample.Enabled = p is not null;
        _merge.Enabled = p is { IsMe: false } && _all.Count > 1;
        _agreed.Enabled = p is { IsMe: false };
        _play.Enabled = v is not null && _store?.ClipPath(v) is { } clip && File.Exists(clip);
        _deletePrint.Enabled = v is not null;
    }

    static string[] Columns(Person p)
    {
        var heard = p.Prints.Where(v => v.From is not null).Select(v => v.From!).ToList();
        int meetings = heard.Select(f => f.Recording).Where(r => r.Length > 0).Distinct().Count();
        DateTimeOffset? last = heard.Count > 0 ? heard.Max(f => f.At) : p.Prints.Count > 0 ? p.Prints.Max(v => v.Added) : null;
        int call = p.Prints.Count(v => v.Kind == VoiceKind.Call), local = p.Prints.Count(v => v.Kind == VoiceKind.Local);
        var prints = string.Join(", ", new[] { call > 0 ? $"{call} call" : null, local > 0 ? $"{local} local" : null }.OfType<string>());
        return
        [
            Display(p),
            meetings == 0 ? "—" : meetings.ToString(),
            last is { } at ? Date(at) : "—",
            prints.Length == 0 ? "none" : prints,
            p.IsMe ? "" : p.Consent is { } c ? $"agreed {Date(c.At)}" : "—",
        ];
    }

    static string Display(Person p) => p.IsMe ? (p.Name.Length > 0 ? $"You ({p.Name})" : "You") : p.Name;

    static string Date(DateTimeOffset at) => at.LocalDateTime.ToString("d MMM yyyy");

    void Act(Action<PeopleStore, Person> action)
    {
        if (_store is null || Selected() is not { } person) return;
        try
        {
            action(_store, person);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Error(this, $"The people folder couldn't be changed: {ex.Message}");
            Reload();
        }
    }

    void Rename(PeopleStore store, Person p)
    {
        var name = PromptDialog.Ask(this, "Rename", $"New name for {Display(p)}:", p.Name);
        if (name is null || name == p.Name) return;
        if (_all.FirstOrDefault(o => o.Id != p.Id && o.Answers(name)) is { } other)
        {
            Ui.Info(this, $"{other.Name} is already in the list. To make them one person, select {Display(p)} and use Merge into….");
            return;
        }
        if (p.Name.Length > 0 && !p.Aliases.Any(a => Same(a, p.Name))) p.Aliases.Add(p.Name); // notes that used the old name still match
        p.Aliases.RemoveAll(a => Same(a, name));
        p.Name = name;
        store.Save(p);
        Reload(p.Id);
    }

    void Merge(PeopleStore store, Person p)
    {
        var others = _all.Where(o => o.Id != p.Id).OrderByDescending(o => o.IsMe).ThenBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        using var pick = new PickDialog("Merge into…",
            $"Who is {p.Name} really? Their voiceprints and names move to the person you pick, and {p.Name} leaves the list.",
            others.Select(Display), "Merge");
        if (pick.ShowDialog(FindForm()) != DialogResult.OK || pick.Selected < 0) return;
        var into = others[pick.Selected];
        StopPlaying();
        store.MergeInto(p, into);
        Reload(into.Id);
    }

    void Delete(PeopleStore store, Person p)
    {
        var text = $"Delete {Display(p)}?\n\nTheir voiceprints and voice clips are removed now. Notes that already name them aren't changed.";
        if (store.Folder.Contains("OneDrive", StringComparison.OrdinalIgnoreCase))
            text += "\n\nYour people folder is in OneDrive, which keeps deleted files in its recycle bin for a while (30 days for a personal " +
                    "account, usually 93 for a work account), and older versions of their file (people\\" + p.Id + ".json) in its version history. " +
                    "To remove them sooner: at onedrive.com, empty the recycle bin (for a work account, also the second-stage recycle bin), " +
                    "and delete that file's older versions from Version history.";
        if (!Ui.Confirm(this, text, warning: true)) return;
        StopPlaying();
        store.Delete(p);
        Reload();
    }

    void SetConsent(PeopleStore store, Person p)
    {
        p.Consent = _agreed.Checked ? new Consent { At = DateTimeOffset.Now, Note = "ticked in Settings" } : null;
        store.Save(p);
        Reload(p.Id);
    }

    void DeletePrint(PeopleStore store, Person p)
    {
        if (SelectedPrint() is not { } v || !Ui.Confirm(this, $"Delete this voiceprint of {Display(p)}? Its voice clip is deleted too.", warning: true)) return;
        StopPlaying();
        var clip = store.ClipPath(v);
        p.Prints.RemoveAll(x => x.Id == v.Id);
        store.Save(p);
        if (clip is not null && File.Exists(clip)) File.Delete(clip);
        Reload(p.Id);
    }

    void Play()
    {
        if (SelectedPrint() is not { } v || _store?.ClipPath(v) is not { } clip || !File.Exists(clip)) return;
        try
        {
            StopPlaying();
            _player = new SoundPlayer(clip);
            _player.Play();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            Ui.Error(this, $"The clip couldn't be played: {ex.Message}");
        }
    }

    void StopPlaying()
    {
        _player?.Stop();
        _player?.Dispose();
        _player = null;
    }

    static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    int Put(Control c, int column)
    {
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(c, column, RowCount);
        return RowCount++;
    }

    /// <summary>A column of buttons, all one width (in 96-dpi units; the form's DPI scaling widens it with the font).</summary>
    static FlowLayoutPanel Buttons(params Control[] items)
    {
        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Top | AnchorStyles.Left, Margin = new Padding(8, 0, 0, 0),
        };
        foreach (var c in items)
        {
            if (c is Button) c.MinimumSize = new Size(170, 0);
            panel.Controls.Add(c);
        }
        return panel;
    }

    static ListView NewList(int height, (string Name, float Weight)[] columns)
    {
        var list = new ListView
        {
            View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable, Height = height, Anchor = Ui.Wide | AnchorStyles.Top,
        };
        foreach (var (name, _) in columns) list.Columns.Add(name);
        float total = columns.Sum(c => c.Weight);
        list.ClientSizeChanged += (_, _) =>
        {
            int width = list.ClientSize.Width;
            for (int i = 0; i < columns.Length; i++) list.Columns[i].Width = (int)(width * columns[i].Weight / total);
        };
        return list;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) StopPlaying();
        base.Dispose(disposing);
    }
}
