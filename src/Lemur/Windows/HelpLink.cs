using Lemur.Logging;

namespace Lemur.App.Windows;

/// <summary>
/// The help pages installed beside the app (help\index.html, manual.html, self-hosting.html), opened in a browser tab
/// at their #section (<see cref="Browser"/>).
/// </summary>
static class HelpLink
{
    public const string Manual = "manual.html";
    public const string SelfHosting = "self-hosting.html";
    public const string Readme = "index.html";

    public static string Folder => Path.Combine(AppContext.BaseDirectory, "help");

    public static void Open(string page = Manual, string? section = null, Control? owner = null)
    {
        var path = Path.Combine(Folder, page);
        if (!File.Exists(path))
        {
            Log.Warn($"help page missing: {path}");
            Ui.Warn(owner, $"The help pages aren't installed with this copy of Lemur.\n\n{path}");
            return;
        }
        if (!Browser.Open(path, section)) Ui.Shell(path);
    }
}
