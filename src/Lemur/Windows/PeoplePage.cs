using Lemur.Config;
using Lemur.Speakers;

namespace Lemur.App.Windows;

/// <summary>Speaker names, the people folder and the people list (design 7.2/7.3, section 8). The wizard shows a short version.</summary>
sealed class PeoplePage : SettingsPage
{
    readonly CheckBox _enabled = Ui.Check("Recognise people by their voice");
    readonly TextBox _url = Ui.Box(placeholder: "blank = the summary model's server");
    readonly TextBox _model = Ui.Box(240);
    readonly TrackBar _level = new() { Minimum = 1, Maximum = 5, SmallChange = 1, LargeChange = 1, TickStyle = TickStyle.BottomRight, Width = 200, Anchor = AnchorStyles.Left };
    readonly CheckBox _learnMe = Ui.Check("Learn my voice from calls");
    readonly CheckBox _askConsent = Ui.Check("Ask before remembering someone's voice");
    readonly CheckBox _intros = Ui.Check("Name people who introduce themselves (\"Hi, it's Dana\")");
    readonly List<Control> _voice = [];

    // Settings window: a folder box and the list.
    readonly TextBox _folder = Ui.Box(placeholder: AppPaths.DefaultPeopleFolder);
    readonly PeopleList? _list;
    readonly System.Windows.Forms.Timer _reload = new() { Interval = 600 };

    // Wizard: three choices.
    readonly RadioButton _thisPc = Ui.Radio("This PC");
    readonly RadioButton _oneDrive = Ui.Radio("OneDrive");
    readonly RadioButton _existing = Ui.Radio("A folder I already use on another PC");
    readonly Label _existingInfo = Ui.Muted("");
    string? _existingPath;

    public PeoplePage(PageHost host)
        : base(host, "People", host.Wizard ? "Optional: real names in your notes instead of \"Speaker B\". You can change all of this later." : "")
    {
        var g = new Grid();
        g.Heading("Speaker names");
        g.Row(null, _enabled,
            "The first time a voice is heard you name it once; after that it's recognised in every meeting. " +
            "This keeps a voiceprint of each person you name (biometric data in some places), only in your own people folder.");
        g.BeginGroup(_voice);
        g.Row("Server URL", _url, "The voiceprint model's OpenAI-style address, ending in /v1.");
        g.Row("Model", _model);
        g.Row("Naming", Ui.Line(SliderEnd("Careful"), _level, SliderEnd("Eager")),
            "Careful gives the fewest wrong names; Eager names more people but is wrong more often.", top: true);
        g.Row(null, _learnMe, "Adds to your own voiceprint after each call, so you're recognised in person too.");
        g.Row(null, _askConsent, "Before a new voice is remembered, you're asked whether that person agreed.");
        g.EndGroup();

        g.Heading("Introductions");
        g.Row(null, _intros, "Uses the summary model only, so it works with voice recognition off. It names them in that meeting's notes and never remembers a voice by itself.");

        g.Heading("People folder");
        if (host.Wizard) WizardFolder(g);
        else SettingsFolder(g);
        Body.Add(g);
        if (!host.Wizard)
        {
            _list = Body.Add(new PeopleList());
            _list.CurrentSettings = host.Flush;
            _list.Secrets = host.Secrets;
        }

        _enabled.CheckedChanged += (_, _) => UpdateEnabled();
    }

    void SettingsFolder(Grid g)
    {
        var buttons = new List<Control> { Ui.Button("Change…", ChangeFolder) };
        if (Ui.OneDrivePeopleFolder() is { } oneDrive) buttons.Add(Ui.Button("Use OneDrive", () => MoveTo(oneDrive)));
        g.Row("Folder", Ui.Stretch(_folder, [.. buttons]),
            "Point your other PCs at the same folder (OneDrive works) to share everyone you've named. Blank = this PC only.");
        g.Row(null, Ui.Line(Ui.Button("Export people…", Export), Ui.Button("Import people…", Import)),
            "For a PC that can't reach this folder: export here, import there. The file holds voiceprints and voice clips, so keep it private.");
        g.Row(null, Ui.Link("Using this at work? What to tell your IT department", ShowItNote));
        _folder.TextChanged += (_, _) => { _reload.Stop(); _reload.Start(); };
        _reload.Tick += (_, _) => { _reload.Stop(); _list?.ShowFolder(CurrentFolder()); };
    }

    void WizardFolder(Grid g)
    {
        var oneDrive = Ui.OneDrivePeopleFolder();
        g.Row(null, _thisPc, AppPaths.DefaultPeopleFolder);
        g.Row(null, _oneDrive, oneDrive is null ? "OneDrive isn't set up on this PC." : $"{oneDrive}. Your other PCs can use it too.");
        _oneDrive.Enabled = oneDrive is not null;
        g.Row(null, _existing);
        var choose = g.Row(null, Ui.Button("Choose folder…", ChooseExisting));
        choose.Margin = new Padding(22, 0, 3, 4);
        _existingInfo.Margin = new Padding(25, 0, 3, 8);
        g.Row(null, _existingInfo);
        _existingInfo.Visible = false;
    }

    public override void LoadFrom(AppSettings s)
    {
        var sp = s.Speakers;
        _enabled.Checked = sp.Enabled;
        _url.Text = sp.BaseUrl;
        _model.Text = sp.Model;
        _level.Value = Math.Clamp(sp.Level, _level.Minimum, _level.Maximum);
        _learnMe.Checked = sp.LearnMyVoice;
        _askConsent.Checked = sp.AskConsent;
        _intros.Checked = sp.NameFromIntroductions;

        var folder = Ui.Blank(sp.PeopleFolder);
        if (folder is not null && Ui.SamePath(folder, AppPaths.DefaultPeopleFolder)) folder = null;
        if (Host.Wizard)
        {
            if (folder is null) _thisPc.Checked = true;
            else if (Ui.SamePath(folder, Ui.OneDrivePeopleFolder())) _oneDrive.Checked = true;
            else ShowExisting(folder);
        }
        else
        {
            _folder.Text = folder ?? "";
            _reload.Stop();
            _list?.ShowFolder(CurrentFolder());
        }
        UpdateEnabled();
    }

    public override void SaveTo(AppSettings s)
    {
        var sp = s.Speakers;
        sp.Enabled = _enabled.Checked;
        sp.BaseUrl = _url.Text.Trim();
        sp.Model = _model.Text.Trim();
        sp.Level = _level.Value;
        sp.LearnMyVoice = _learnMe.Checked;
        sp.AskConsent = _askConsent.Checked;
        sp.NameFromIntroductions = _intros.Checked;
        sp.PeopleFolder = ChosenFolder();
    }

    public override void Check(List<string> errors)
    {
        var url = _url.Text.Trim();
        if (_enabled.Checked && url.Length > 0 && !Ui.IsHttpUrl(url)) errors.Add("People: the voiceprint server URL must start with https:// or http://.");
        if (_enabled.Checked && _model.Text.Trim().Length == 0) errors.Add("People: the voiceprint model is empty.");
        if (Host.Wizard && _existing.Checked && _existingPath is null) errors.Add("People: choose the folder you already use, or pick another option.");
        if (!Host.Wizard && Ui.Blank(_folder.Text) is { } folder && !Ui.ValidPath(folder))
            errors.Add("People: the people folder must be a full path, like C:\\Users\\you\\OneDrive\\Lemur\\people, or blank.");
    }

    public override void Entered() => _list?.Reload(); // another PC may have changed the folder

    string? ChosenFolder()
    {
        if (Host.Wizard)
            return _oneDrive.Checked ? Ui.OneDrivePeopleFolder() : _existing.Checked ? _existingPath : null;
        return Ui.Blank(_folder.Text) is { } f && !Ui.SamePath(f, AppPaths.DefaultPeopleFolder) ? f : null;
    }

    string CurrentFolder() => ChosenFolder() ?? AppPaths.DefaultPeopleFolder;

    /// <summary>"Careful" / "Eager", level with the slider's track rather than the middle of its tick marks.</summary>
    static Label SliderEnd(string text) =>
        new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Left, Margin = new Padding(3, 5, 3, 0) };

    void UpdateEnabled()
    {
        foreach (var c in _voice) c.Enabled = _enabled.Checked;
    }

    void ChangeFolder()
    {
        if (Ui.PickFolder(this, "Choose the people folder", CurrentFolder()) is { } picked) MoveTo(Normalize(picked));
    }

    /// <summary>Switches the folder box, offering to bring the people from the old folder along.</summary>
    void MoveTo(string target)
    {
        var from = CurrentFolder();
        if (Ui.SamePath(from, target)) return;
        int here = Count(from);
        if (here > 0 && Ui.Confirm(this, $"Copy the {Ui.People(here)} in the current folder into the new one too?\n\nAnyone already in the new folder is kept; someone in both is merged."))
        {
            var zip = Path.Combine(Path.GetTempPath(), $"lemur-people-{Guid.NewGuid():N}.zip");
            try
            {
                PeopleTransfer.Export(new PeopleStore(from), zip);
                PeopleTransfer.Import(new PeopleStore(target), zip);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Ui.Error(this, $"The people couldn't be copied: {ex.Message}");
                return;
            }
            finally
            {
                try { File.Delete(zip); } catch (IOException) { /* temp file; Windows cleans it up */ }
            }
        }
        _folder.Text = Ui.SamePath(target, AppPaths.DefaultPeopleFolder) ? "" : target;
        _reload.Stop();
        _list?.ShowFolder(CurrentFolder());
    }

    void ChooseExisting()
    {
        if (Ui.PickFolder(this, "Choose the people folder your other PC uses", _existingPath ?? Ui.OneDrive()) is { } picked)
            ShowExisting(Normalize(picked));
    }

    void ShowExisting(string folder)
    {
        _existingPath = folder;
        _existing.Checked = true;
        int n = Count(folder);
        _existingInfo.Text = n > 0
            ? $"{folder}\nFound {Ui.People(n)} there."
            : $"{folder}\nNobody is in this folder yet. Pick the one your other PC uses: it has a \"people\" folder inside.";
        _existingInfo.Visible = true;
    }

    void Export()
    {
        var store = new PeopleStore(CurrentFolder());
        if (Count(store.Folder) == 0)
        {
            Ui.Info(this, "Nobody to export yet.");
            return;
        }
        using var dialog = new SaveFileDialog
        {
            Title = "Export people", Filter = "Zip file (*.zip)|*.zip", DefaultExt = "zip", OverwritePrompt = true,
            FileName = $"Lemur people {DateTime.Now:yyyy-MM-dd}.zip",
        };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        try
        {
            int n = PeopleTransfer.Export(store, dialog.FileName);
            Ui.Info(this, $"Exported {Ui.People(n)}.\n\nThe file holds voiceprints and voice clips: keep it private, and delete it once it's imported on the other PC.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Error(this, $"The people couldn't be exported: {ex.Message}");
        }
    }

    void Import()
    {
        using var dialog = new OpenFileDialog { Title = "Import people", Filter = "Zip file (*.zip)|*.zip|All files (*.*)|*.*" };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        try
        {
            var (added, merged) = PeopleTransfer.Import(new PeopleStore(CurrentFolder()), dialog.FileName);
            Ui.Info(this, $"Added {Ui.People(added)}; merged {Ui.People(merged)} who were already here.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            Ui.Error(this, $"That file couldn't be imported: {ex.Message}");
        }
        _list?.Reload();
    }

    void ShowItNote() => Ui.Info(this,
        "Lemur can recognise people in meetings by their voice. This is off unless the user turns it on.\n\n" +
        "• A voiceprint (a list of numbers describing a voice) and one short reference clip are kept for each person the user names, " +
        "only in the user's people folder: their own profile or their OneDrive.\n" +
        "• The voiceprint server turns clips into voiceprints and keeps nothing.\n" +
        "• Nobody's voice is remembered unless the user names them, and by default the user is asked whether that person agreed.\n" +
        "• Deleting a person removes their voiceprints and clips at once.\n\n" +
        "Voiceprints count as biometric data under some laws (for example Illinois' BIPA and the GDPR). " +
        "Your organisation may have its own rules about them.");

    /// <summary>The picked folder may be the people folder's parent or its inner "people" folder; find the real one.</summary>
    static string Normalize(string picked)
    {
        try
        {
            if (HasPeople(picked)) return picked;
            if (HasPeople(Path.Combine(picked, "people"))) return Path.Combine(picked, "people");
            if (string.Equals(Path.GetFileName(picked), "people", StringComparison.OrdinalIgnoreCase)
                && Directory.EnumerateFiles(picked, "*.json").Any() && Path.GetDirectoryName(picked) is { } parent)
                return parent;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* use it as picked */ }
        return picked;

        static bool HasPeople(string folder) =>
            Directory.Exists(Path.Combine(folder, "people")) && Directory.EnumerateFiles(Path.Combine(folder, "people"), "*.json").Any();
    }

    static int Count(string folder)
    {
        try { return new PeopleStore(folder).People().Count; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _reload.Dispose();
        base.Dispose(disposing);
    }
}
