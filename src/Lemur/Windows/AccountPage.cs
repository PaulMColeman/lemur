using System.Text.Json;
using Lemur.Config;
using Lemur.Speakers;

namespace Lemur.App.Windows;

/// <summary>Your name, which servers do the work, their keys, Test connection and setup files (design 7.2, Account).</summary>
sealed class AccountPage : SettingsPage
{
    readonly TextBox _name = Ui.Box();
    readonly RadioButton _openAi = Ui.Radio("LiteLLM / OpenAI-compatible");
    readonly RadioButton _scriberr = Ui.Radio("Scriberr");
    readonly TextBox _sttUrl = Ui.Box(placeholder: "https://gateway.example.com/v1");
    readonly TextBox _sttModel = Ui.Box(220);
    readonly TextBox _sttLanguage = Ui.Box(80, "detect");
    readonly TextBox _scrUrl = Ui.Box(placeholder: "https://scriberr.example.com");
    readonly TextBox _scrProfile = Ui.Box(220, "Scriberr's default");
    readonly TextBox _scrSummary = Ui.Box(220, "Scriberr's default");
    readonly TextBox _scrTemplate = Ui.Box(220, "none");
    readonly TextBox _sumUrl = Ui.Box(placeholder: "https://gateway.example.com/v1");
    readonly TextBox _sumModel = Ui.Box(220);
    readonly Label _sumNote = Ui.Muted("");
    readonly KeyField _sttKey, _scrKey, _sumKey, _voiceKey;
    readonly List<Control> _openAiRows = [], _scriberrRows = [];
    readonly Label _keyHelp = Ui.Bold("");
    readonly Button _test;
    readonly CheckList _results = new();
    CancellationTokenSource? _testing;

    public AccountPage(PageHost host)
        : base(host, "Account", "Where your recordings are turned into text and notes. Import a setup file if you were given one, then save your key.")
    {
        _sttKey = new KeyField(host, "transcription", bearer: true, s => s.Backend.OpenAi.TranscriptionApiKeySecret,
            (s, n) => s.Backend.OpenAi.TranscriptionApiKeySecret = n, "transcription-api-key", () => _sttUrl.Text);
        _scrKey = new KeyField(host, "Scriberr", bearer: false, s => s.Backend.Scriberr.ApiKeySecret,
            (s, n) => s.Backend.Scriberr.ApiKeySecret = n, "scriberr-api-key", () => _scrUrl.Text);
        _sumKey = new KeyField(host, "summary", bearer: true, s => s.Summary.ApiKeySecret,
            (s, n) => s.Summary.ApiKeySecret = n, "summary-api-key", () => _sumUrl.Text);
        _voiceKey = new KeyField(host, "voiceprint", bearer: true, s => s.Speakers.ApiKeySecret,
            (s, n) => s.Speakers.ApiKeySecret = n, "voiceprint-api-key", VoiceUrl, VoiceKeyNotNeeded);
        _test = Ui.Button("Test connection", RunTest);
        _keyHelp.Visible = false;
        _keyHelp.Margin = new Padding(3, 0, 3, 8);

        if (host.Wizard)
        {
            var import = Ui.Button("Import setup file…", ImportSetup);
            import.Font = Ui.BoldFont;
            import.Padding = new Padding(12, 4, 12, 4);
            import.Margin = new Padding(3, 6, 3, 6);
            Body.Add(Ui.Callout(SystemColors.Window,
                Ui.Bold("Got a setup file?"),
                Ui.Text("If whoever runs your server gave you a .lemursetup file, import it first: it fills in everything below except your key."),
                import,
                _keyHelp));
        }

        var g = new Grid();
        g.Heading("You");
        g.Row("Your name", _name, "How you're named in transcripts and notes.");

        g.Heading("Transcription");
        g.Row("Server type", Ui.Line(_openAi, _scriberr));
        g.BeginGroup(_openAiRows);
        g.Row("Server URL", _sttUrl, "Its OpenAI-style address, ending in /v1.");
        g.Row("Model", _sttModel);
        g.Row("Language", _sttLanguage, "A language code such as en; blank = detect it.");
        g.Row("Key", _sttKey);
        g.EndGroup();
        g.BeginGroup(_scriberrRows);
        g.Row("Server URL", _scrUrl, "The site's address, without /api/v1.");
        g.Row("Profile", _scrProfile, "Transcription profile; blank = the one marked default in Scriberr.");
        g.Row("Summary model", _scrSummary, "The model Scriberr summarizes with; blank = Scriberr's default.");
        g.Row("Template", _scrTemplate, "Optional: a Scriberr summary template to file summaries under.");
        g.Row("Key", _scrKey);
        g.EndGroup();

        g.Heading("Summary model", _sumNote);
        g.Row("Server URL", _sumUrl, "OpenAI-style address ending in /v1; often the same server as transcription.");
        g.Row("Model", _sumModel);
        g.Row("Key", _sumKey);

        g.Heading("Voiceprints");
        g.Row("Key", _voiceKey, "Only for speaker names (People tab). When the voiceprint model is on the summary server, the summary key is used.");

        g.Heading("Check");
        g.Row(null, _test, "Checks each server that's switched on, separately, using what's in this window. Keys are saved as soon as you change them.");
        g.Row(null, _results);

        if (!host.Wizard)
        {
            g.Heading("Setup file");
            g.Row(null, Ui.Line(Ui.Button("Import setup file…", ImportSetup), Ui.Button("Export setup file…", ExportSetup)),
                "Moves these settings to another PC or to a colleague: server addresses and model names only, never keys, folders or devices.");
            g.Row(null, _keyHelp);
        }
        Body.Add(g);

        _openAi.CheckedChanged += (_, _) => ShowBackend();
        _sumUrl.Leave += (_, _) => _voiceKey.UpdateStatus();
        foreach (var key in new[] { _sttKey, _sumKey, _voiceKey }) key.KeySaved += OfferSameKey;
    }

    public override void LoadFrom(AppSettings s)
    {
        _name.Text = s.DisplayName;
        (s.Backend.Type == BackendTypes.Scriberr ? _scriberr : _openAi).Checked = true;
        _sttUrl.Text = s.Backend.OpenAi.TranscriptionBaseUrl;
        _sttModel.Text = s.Backend.OpenAi.TranscriptionModel;
        _sttLanguage.Text = s.Backend.OpenAi.Language ?? "";
        _scrUrl.Text = s.Backend.Scriberr.BaseUrl;
        _scrProfile.Text = s.Backend.Scriberr.ProfileName;
        _scrSummary.Text = s.Backend.Scriberr.SummaryModel;
        _scrTemplate.Text = s.Backend.Scriberr.SummaryTemplateName ?? "";
        _sumUrl.Text = s.Summary.BaseUrl;
        _sumModel.Text = s.Summary.Model;
        ShowBackend();
        UpdateKeys();
    }

    public override void SaveTo(AppSettings s)
    {
        s.DisplayName = _name.Text.Trim();
        s.Backend.Type = _scriberr.Checked ? BackendTypes.Scriberr : BackendTypes.OpenAi;
        s.Backend.OpenAi.TranscriptionBaseUrl = _sttUrl.Text.Trim();
        s.Backend.OpenAi.TranscriptionModel = _sttModel.Text.Trim();
        s.Backend.OpenAi.Language = Ui.Blank(_sttLanguage.Text);
        s.Backend.Scriberr.BaseUrl = _scrUrl.Text.Trim();
        s.Backend.Scriberr.ProfileName = _scrProfile.Text.Trim();
        s.Backend.Scriberr.SummaryModel = _scrSummary.Text.Trim();
        s.Backend.Scriberr.SummaryTemplateName = Ui.Blank(_scrTemplate.Text);
        s.Summary.BaseUrl = _sumUrl.Text.Trim();
        s.Summary.Model = _sumModel.Text.Trim();
    }

    public override void Check(List<string> errors)
    {
        if (_name.Text.Trim().Length == 0) errors.Add("Account: your name is empty.");
        if (_openAi.Checked) UrlCheck(errors, "transcription server URL", _sttUrl);
        else UrlCheck(errors, "Scriberr URL", _scrUrl);
        UrlCheck(errors, "summary server URL", _sumUrl);
    }

    public override void Entered()
    {
        Host.Flush(); // the voiceprint key's status depends on the People tab
        UpdateKeys();
    }

    static void UrlCheck(List<string> errors, string what, TextBox box)
    {
        var url = box.Text.Trim();
        if (url.Length > 0 && !Ui.IsHttpUrl(url)) errors.Add($"Account: the {what} must start with https:// or http://.");
    }

    void ShowBackend()
    {
        bool scriberr = _scriberr.Checked;
        SuspendLayout();
        foreach (var c in _openAiRows) c.Visible = !scriberr;
        foreach (var c in _scriberrRows) c.Visible = scriberr;
        _sumNote.Text = scriberr
            ? "Scriberr writes the summary; this model only names people who introduce themselves. Leave it blank to skip that."
            : "Writes the summary, and names people who introduce themselves.";
        ResumeLayout();
    }

    void UpdateKeys()
    {
        foreach (var key in new[] { _sttKey, _scrKey, _sumKey, _voiceKey }) key.UpdateStatus();
    }

    string VoiceUrl() => Ui.Blank(Host.Edited.Speakers.BaseUrl) ?? _sumUrl.Text.Trim();

    string? VoiceKeyNotNeeded()
    {
        if (!Host.Edited.Speakers.Enabled) return "not needed while speaker names are off";
        return VoiceprintClient.SameServer(VoiceUrl(), _sumUrl.Text.Trim()) && _sumKey.IsSaved
            ? "not needed: the summary key is used (same server)"
            : null;
    }

    /// <summary>One LiteLLM usually serves transcription and summary: offer the key just pasted to the other one.</summary>
    void OfferSameKey(KeyField from, string value)
    {
        var url = from.ServerUrl();
        var candidates = _openAi.Checked ? new[] { _sttKey, _sumKey } : new[] { _sumKey };
        var others = candidates.Where(k => k != from && !k.IsSaved && VoiceprintClient.SameServer(k.ServerUrl(), url)).ToList();
        if (others.Count == 0) return;
        if (!Ui.Confirm(this, $"Use the same key for {string.Join(" and ", others.Select(k => k.What))} too? It's on the same server.")) return;
        try
        {
            foreach (var k in others) k.Store(value);
        }
        catch (InvalidOperationException ex)
        {
            Ui.Error(this, $"The key couldn't be saved: {ex.Message}");
        }
        _voiceKey.UpdateStatus();
    }

    async void RunTest()
    {
        _testing?.Cancel();
        _testing = new CancellationTokenSource();
        var ct = _testing.Token;
        var snapshot = Ui.Copy(Host.Flush());
        _test.Enabled = false;
        _results.Running();
        try
        {
            var results = await CheckList.RunAsync(snapshot, Host.Secrets, ct);
            if (!IsDisposed && !ct.IsCancellationRequested) _results.ShowResults(results);
        }
        catch (OperationCanceledException)
        {
            // window closed or test restarted
        }
        finally
        {
            if (!IsDisposed) _test.Enabled = true;
        }
    }

    void ImportSetup()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Import setup file",
            Filter = $"Lemur setup (*{SetupFile.Extension};*{SetupFile.LegacyExtension})|*{SetupFile.Extension};*{SetupFile.LegacyExtension}|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;

        SetupFile file;
        try
        {
            file = SetupFile.Parse(File.ReadAllText(dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            Ui.Error(this, $"That setup file can't be used: {ex.Message}");
            return;
        }

        var current = Host.Flush();
        var from = string.IsNullOrWhiteSpace(file.Name) ? Path.GetFileName(dialog.FileName) : file.Name;
        var changes = file.Preview(current);
        if (changes.Count == 0)
        {
            Ui.Info(this, $"The setup from \"{from}\" matches your settings already; nothing to change.");
        }
        else
        {
            using var confirm = new ListDialog("Import setup file", $"The setup from \"{from}\" will change:", changes, "Apply",
                "Your name, folders, devices and keys stay as they are. Nothing is saved until you click Save" + (Host.Wizard ? " at the end." : "."));
            if (confirm.ShowDialog(FindForm()) != DialogResult.OK) return;
            file.ApplyTo(current);
            Host.Reload();
        }

        _keyHelp.Text = "Next: save your key with Change… below." + (string.IsNullOrWhiteSpace(file.KeyHelp) ? "" : " " + file.KeyHelp.Trim());
        _keyHelp.Visible = true;
    }

    void ExportSetup()
    {
        var s = Host.Flush();
        var main = s.Backend.Type == BackendTypes.Scriberr ? s.Backend.Scriberr.BaseUrl : s.Backend.OpenAi.TranscriptionBaseUrl;
        var suggested = Uri.TryCreate(main, UriKind.Absolute, out var uri) ? uri.Host : "";
        using var ask = new PromptDialog("Export setup file", "Server addresses and model names go in the file; keys, folders and devices don't.",
            ("Name", suggested, "Shown to whoever imports it."),
            ("Key help", "", "Optional: one line telling them where to get their own key."));
        if (ask.ShowDialog(FindForm()) != DialogResult.OK) return;

        using var save = new SaveFileDialog
        {
            Title = "Export setup file",
            Filter = $"Lemur setup (*{SetupFile.Extension})|*{SetupFile.Extension}",
            DefaultExt = SetupFile.Extension.TrimStart('.'),
            FileName = SafeFileName(ask.Value(0)) + SetupFile.Extension,
            OverwritePrompt = true,
        };
        if (save.ShowDialog(FindForm()) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(save.FileName, SetupFile.From(s, ask.Value(0), Ui.Blank(ask.Value(1))).ToJson());
            Ui.Info(this, "Setup file saved. It holds no keys, so it's safe to send.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Error(this, $"The setup file couldn't be saved: {ex.Message}");
        }
    }

    static string SafeFileName(string name)
    {
        var clean = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)).Trim();
        return clean.Length == 0 ? "Lemur" : clean;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _testing?.Cancel();
            _testing?.Dispose();
        }
        base.Dispose(disposing);
    }
}
