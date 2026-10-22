using Microsoft.Win32;
using Lemur.Logging;
using Lemur.Pipeline;

namespace Lemur.App.Windows;

/// <summary>
/// lemur://name/&lt;recording&gt; — the "Name speakers" link in a meeting's page. Registered per user
/// (HKCU, no admin rights); the link starts a second copy of the app that shows only the naming window.
/// Pages written before 1.0 say teamsrecorder://, which is registered too so they keep working.
/// The same registration carries lemur://note/ links (<see cref="NoteLink"/>).
/// </summary>
static class NameLink
{
    static readonly string[] Schemes = [NoteHtml.NameLinkScheme, NoteHtml.LegacyNameLinkScheme];

    /// <summary>Points the addresses at this exe; repeated at every start so a moved install keeps working.</summary>
    public static void Register()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        foreach (var scheme in Schemes)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{scheme}");
                key.SetValue("", "URL:Lemur");
                key.SetValue("URL Protocol", "");
                using var command = key.CreateSubKey(@"shell\open\command");
                command.SetValue("", $"\"{exe}\" \"%1\"");
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                Log.Warn($"could not register {scheme}:// links: {ex.Message}");
            }
        }
    }

    /// <summary>The recording name in a link, or null if the argument isn't one of ours.</summary>
    public static string? RecordingIn(string[] args)
    {
        foreach (var scheme in Schemes)
        {
            var prefix = scheme + "://name/";
            var link = args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (link is not null) return Uri.UnescapeDataString(link[prefix.Length..].TrimEnd('/'));
        }
        return null;
    }
}
