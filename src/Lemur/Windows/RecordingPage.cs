using Lemur.Config;
using Lemur.Detection;

namespace Lemur.App.Windows;

/// <summary>When to record and from which devices (design 7.2, Recording).</summary>
sealed class RecordingPage : SettingsPage
{
    const string DefaultDevice = "(Windows default)";

    readonly CheckBox _auto = Ui.Check("Record calls automatically");
    readonly CheckedListBox _sources = new() { CheckOnClick = true, Width = 260, Anchor = AnchorStyles.Left, BorderStyle = BorderStyle.FixedSingle };
    readonly NumericUpDown _start = Ui.Number(0, 120);
    readonly NumericUpDown _stop = Ui.Number(0, 600);
    readonly NumericUpDown _shortest = Ui.Number(0, 3600);
    readonly RadioButton _follow = Ui.Radio("Follow the call (recommended)");
    readonly RadioButton _default = Ui.Radio("Windows default");
    readonly RadioButton _pick = Ui.Radio("This device:");
    readonly ComboBox _mic = Ui.Combo();
    readonly ComboBox _speakers = Ui.Combo();
    readonly Grid _devices = new() { Margin = new Padding(20, 0, 0, 0) };
    readonly CheckBox _keepAlive = Ui.Check("Keep the other side's audio flowing during silences (recommended)");
    readonly CheckBox _startAtSignIn = Ui.Check("Start Lemur when I sign in to Windows");
    readonly TextBox _importFolder = Ui.Box(placeholder: "none: import with the Recordings window's Import… instead");
    readonly List<Control> _timing = [];

    public RecordingPage(PageHost host) : base(host, "Recording", "When to record, and from which microphone and speakers.")
    {
        FillDevices();

        var g = new Grid();
        if (!host.Wizard) // the wizard asks on its last step
        {
            g.Heading("Starting");
            g.Row(null, _startAtSignIn, "So Lemur is always there for your calls. It sits in the system tray, by the clock.");
        }
        g.Heading("Calls");
        g.Row(null, _auto, "Recording starts when a call app below takes your microphone and stops when the call ends. You can always start or stop it from the tray menu.");
        g.BeginGroup(_timing);
        g.Row("Watch", _sources, "Which call apps to watch. A browser counts only while one of its windows shows the call (\"… | Microsoft Teams\", \"Meet - …\").", top: true);
        g.Row("Start after", Seconds(_start), "How long a call must hold the microphone first; filters out blips.");
        g.Row("Stop after", Seconds(_stop), "How long after the call lets go of the microphone; rides out switching headsets.");
        g.Row("Shortest call kept", Seconds(_shortest), "Shorter automatic recordings are thrown away (like the microphone preview before you join a call).");
        g.EndGroup();

        g.Heading("Devices");
        g.Row("Record from", _follow, "The microphone and headset the call really uses, even when they aren't the Windows defaults.");
        g.Row(null, _default, "Whatever Windows is set to use when the recording starts.");
        g.Row(null, _pick);
        _devices.Row("Microphone", _mic);
        _devices.Row("Speakers or headset", _speakers, "Matched by name, so part of it (\"Arctis\") is enough.");
        g.Row(null, _devices);
        g.Row(null, _keepAlive, "Plays inaudible silence on the speakers while recording; without it Windows stops sending the call's sound when nobody talks.");
        var test = new Button { Text = "Test devices…", AutoSize = true, Enabled = false, UseVisualStyleBackColor = true, Anchor = AnchorStyles.Left };
        g.Row(null, Ui.Line(test, Ui.Muted("coming soon: records 5 seconds with a beep and shows both levels")));

        if (!host.Wizard) // one thing less to read while setting up; it's here afterwards
        {
            g.Heading("Recordings from elsewhere");
            g.Row("Import from", Ui.Stretch(_importFolder, Ui.Button("Browse…", BrowseImport)),
                "Recordings put in this folder become notes: save a voice memo from your phone to a folder that syncs to this PC " +
                "(OneDrive, iCloud Drive, Nextcloud), or copy in a recorder's file. Each one moves to \"Imported\" inside the folder " +
                "once Lemur has it. Blank = don't watch a folder.");
        }
        Body.Add(g);

        _auto.CheckedChanged += (_, _) => UpdateEnabled();
        _pick.CheckedChanged += (_, _) => UpdateEnabled();
    }

    /// <summary>"Start Lemur when I sign in" as ticked; not a setting in settings.json but the Run entry, applied on Save.</summary>
    public bool StartAtSignIn => _startAtSignIn.Checked;

    public override void LoadFrom(AppSettings s)
    {
        _startAtSignIn.Checked = Autostart.IsOn;
        _auto.Checked = s.Detection.Enabled;
        _sources.Items.Clear();
        foreach (var rule in s.Detection.Sources) _sources.Items.Add(rule.Name, rule.Enabled);
        _sources.Height = _sources.ItemHeight * Math.Max(2, _sources.Items.Count) + 4;
        _start.Value = Seconds(s.Detection.StartAfterSeconds, _start);
        _stop.Value = Seconds(s.Detection.StopAfterSeconds, _stop);
        _shortest.Value = Seconds(s.Detection.MinAutoRecordingSeconds, _shortest);

        var audio = s.Audio;
        bool pinned = Ui.Blank(audio.MicDeviceContains) is not null || Ui.Blank(audio.OutputDeviceContains) is not null;
        (pinned ? _pick : audio.FollowCallDevices ? _follow : _default).Checked = true;
        _mic.Text = Ui.Blank(audio.MicDeviceContains) ?? DefaultDevice;
        _speakers.Text = Ui.Blank(audio.OutputDeviceContains) ?? DefaultDevice;
        _keepAlive.Checked = audio.KeepAliveOutput;
        _importFolder.Text = s.Import.Folder ?? "";
        UpdateEnabled();
    }

    public override void SaveTo(AppSettings s)
    {
        s.Detection.Enabled = _auto.Checked;
        for (int i = 0; i < _sources.Items.Count; i++)
            if (s.Detection.Sources.FirstOrDefault(r => r.Name == (string)_sources.Items[i]) is { } rule)
                rule.Enabled = _sources.GetItemChecked(i);
        s.Detection.StartAfterSeconds = (double)_start.Value;
        s.Detection.StopAfterSeconds = (double)_stop.Value;
        s.Detection.MinAutoRecordingSeconds = (double)_shortest.Value;

        // A pinned device wins over following the call; "(Windows default)" beside a pinned one means the default.
        if (_pick.Checked)
        {
            s.Audio.FollowCallDevices = false;
            s.Audio.MicDeviceContains = Device(_mic);
            s.Audio.OutputDeviceContains = Device(_speakers);
        }
        else
        {
            s.Audio.FollowCallDevices = _follow.Checked;
            s.Audio.MicDeviceContains = null;
            s.Audio.OutputDeviceContains = null;
        }
        s.Audio.KeepAliveOutput = _keepAlive.Checked;
        s.Import.Folder = Ui.Blank(_importFolder.Text);
    }

    public override void Check(List<string> errors)
    {
        if (Ui.Blank(_importFolder.Text) is not { } folder) return;
        if (!Ui.ValidPath(folder))
            errors.Add("Recording: the import folder must be a full path, like C:\\Users\\you\\OneDrive\\Voice memos, or blank.");
        else if (Pipeline.RecordingImport.FolderProblem(folder, Host.Edited.NotesFolder, Host.Edited.ResolvedRecordingsFolder) is { } problem)
            errors.Add("Recording: " + problem);
    }

    void BrowseImport()
    {
        var start = Ui.Blank(_importFolder.Text) ?? Ui.OneDrive();
        if (Ui.PickFolder(this, "Choose a folder to import recordings from", start) is { } folder) _importFolder.Text = folder;
    }

    void UpdateEnabled()
    {
        foreach (var c in _timing) c.Enabled = _auto.Checked;
        _devices.Enabled = _pick.Checked;
    }

    void FillDevices()
    {
        var endpoints = new WasapiSessionSource().Endpoints(); // empty if Windows audio can't be asked
        Fill(_mic, endpoints.Where(e => e.IsCapture));
        Fill(_speakers, endpoints.Where(e => !e.IsCapture));

        static void Fill(ComboBox box, IEnumerable<AudioEndpoint> devices)
        {
            box.Items.Add(DefaultDevice);
            foreach (var name in devices.Select(d => d.Name).Distinct().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase))
                box.Items.Add(name);
        }
    }

    static string? Device(ComboBox box)
    {
        var text = box.Text.Trim();
        return text.Length == 0 || text == DefaultDevice ? null : text;
    }

    static TableLayoutPanel Seconds(NumericUpDown box) => Ui.Line(box, Ui.Text("seconds"));

    static decimal Seconds(double value, NumericUpDown box) => Math.Clamp((decimal)Math.Round(value), box.Minimum, box.Maximum);
}
