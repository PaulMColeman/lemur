using Lemur.Config;

namespace Lemur.App.Windows;

/// <summary>
/// One group of settings: a tab in <see cref="SettingsForm"/> and a step in <see cref="SetupWizard"/>.
/// A page touches the working copy only in <see cref="LoadFrom"/> and <see cref="SaveTo"/>, so the window can
/// test, import or cancel without half-applied changes.
/// </summary>
abstract class SettingsPage : UserControl
{
    protected SettingsPage(PageHost host, string title, string intro)
    {
        Host = host;
        Title = title;
        Intro = intro;
        AutoScaleMode = AutoScaleMode.Inherit;
        AutoScroll = true;
        Dock = DockStyle.Fill;
        Padding = new Padding(16, 12, 16, 12);
        Controls.Add(Body);
    }

    protected PageHost Host { get; }

    protected VStack Body { get; } = new();

    /// <summary>Tab text, and the wizard step's heading.</summary>
    public string Title { get; }

    /// <summary>One line under the wizard step's heading.</summary>
    public string Intro { get; }

    public abstract void LoadFrom(AppSettings s);

    public abstract void SaveTo(AppSettings s);

    /// <summary>What must be fixed before saving. (<see cref="AppSettings.Problems"/> only warns: you can record without a backend.)</summary>
    public virtual void Check(List<string> errors) { }

    /// <summary>The page was just shown (tab selected, wizard step reached).</summary>
    public virtual void Entered() { }
}

/// <summary>The working copy shared by a window's pages, plus what they need to reach outside it.</summary>
sealed class PageHost(AppSettings settings, ISecretStore secrets, bool wizard, string settingsPath)
{
    public AppSettings Edited { get; } = Ui.Copy(settings);
    public ISecretStore Secrets { get; } = secrets;
    public bool Wizard { get; } = wizard;
    public string SettingsPath { get; } = settingsPath;
    public string ConfigFolder => Path.GetDirectoryName(Path.GetFullPath(SettingsPath)) ?? AppPaths.ConfigFolder;
    public List<SettingsPage> Pages { get; } = [];

    /// <summary>Writes every page into <see cref="Edited"/> and returns it.</summary>
    public AppSettings Flush()
    {
        foreach (var page in Pages) page.SaveTo(Edited);
        return Edited;
    }

    /// <summary>Shows <see cref="Edited"/> on every page again (after an import).</summary>
    public void Reload()
    {
        foreach (var page in Pages) page.LoadFrom(Edited);
    }

    /// <summary>Collects every page's errors; returns the first page that has any.</summary>
    public SettingsPage? Check(List<string> errors)
    {
        SettingsPage? first = null;
        foreach (var page in Pages)
        {
            int before = errors.Count;
            page.Check(errors);
            if (errors.Count > before) first ??= page;
        }
        return first;
    }
}
