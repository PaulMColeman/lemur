using System.Runtime.InteropServices;
using Lemur.Config;

namespace Lemur.App.Windows;

/// <summary>Version, where things are kept, and Copy diagnostics (design 7.2, About).</summary>
sealed class AboutPage : SettingsPage
{
    readonly LinkLabel _people, _notes, _recordings;

    public AboutPage(PageHost host) : base(host, "About", "")
    {
        var g = new Grid();
        g.Heading("Lemur");
        g.Row("Version", Ui.Text($"{Version()}  ·  .NET {Environment.Version}"));
        g.Row(null, Ui.Muted("LLM Meeting Recorder: records your calls and in-person meetings, and turns them into meeting notes on your own servers."));

        g.Heading("Where things are");
        g.Row("Settings", Ui.Link(host.SettingsPath, () => Ui.Shell(host.SettingsPath)));
        g.Row("Logs", Ui.Link(AppPaths.LogFolder, () => Ui.OpenFolder(AppPaths.LogFolder)));
        _people = g.Row("People", FolderLink());
        _notes = g.Row("Notes", FolderLink());
        _recordings = g.Row("Recordings", FolderLink());
        g.Row("Keys", Ui.Text($"Windows Credential Manager, as \"{AppPaths.CredentialPrefix}:<name>\""));

        g.Heading("Help");
        g.Row("Manual", Ui.Link("How to use Lemur", () => HelpLink.Open(owner: this)));
        g.Row("Servers", Ui.Link("Running the server side yourself", () => HelpLink.Open(HelpLink.SelfHosting, owner: this)));
        g.Row("Overview", Ui.Link("What Lemur is and does", () => HelpLink.Open(HelpLink.Readme, owner: this)));
        g.Row(null, Ui.Button("Copy diagnostics", CopyDiagnostics),
            $"Copies your settings, any setup problems and the last {DiagnosticsReport.LogLines} log lines to the clipboard, for a bug report. Keys are removed, and transcripts are never in it.");
        Body.Add(g);
    }

    public override void LoadFrom(AppSettings s)
    {
        _people.Text = s.Speakers.ResolvedPeopleFolder;
        _notes.Text = s.NotesFolder;
        _recordings.Text = s.ResolvedRecordingsFolder;
    }

    public override void SaveTo(AppSettings s) { }

    public override void Entered() => LoadFrom(Host.Flush());

    static LinkLabel FolderLink()
    {
        var link = new LinkLabel { AutoSize = true, Anchor = AnchorStyles.Left };
        link.LinkClicked += (_, _) => Ui.OpenFolder(link.Text);
        return link;
    }

    public static string Version()
    {
        var v = Application.ProductVersion;
        int plus = v.IndexOf('+');
        return plus > 0 ? v[..plus] : v;
    }

    void CopyDiagnostics()
    {
        try
        {
            Clipboard.SetText(DiagnosticsReport.Build(Host.Flush(), Host.Secrets, AppPaths.LogFolder));
            Ui.Info(this, "Diagnostics copied. Paste them into your bug report or e-mail.");
        }
        catch (ExternalException ex)
        {
            Ui.Error(this, $"The clipboard is busy; try again. ({ex.Message})");
        }
    }
}
