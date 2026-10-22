using System.Diagnostics;
using Microsoft.Win32;
using Lemur.Logging;

namespace Lemur.App.Windows;

/// <summary>
/// A page on disk opened in the user's default browser at a #section: the help pages (<see cref="HelpLink"/>) and
/// meetings' note pages at a moment (<see cref="NoteLink"/>). It goes as a file:// address to the program that opens
/// https:// links, because Windows drops a #section when it opens a file, and .html may belong to an editor.
/// </summary>
static class Browser
{
    /// <returns>False when there's no default browser to ask or it wouldn't start; the caller then opens the file itself.</returns>
    public static bool Open(string path, string? section = null)
    {
        if (Default() is not { } browser) return false;
        var url = new Uri(path).AbsoluteUri + (section is null ? "" : "#" + section); // spaces become %20 and # %23: one argument
        try
        {
            Process.Start(new ProcessStartInfo(browser.Exe, browser.Arguments.Replace("%1", url)) { UseShellExecute = false })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn($"could not start the browser ({Log.TypeAndCode(ex)})"); // not its message, which may hold the page's path
            return false;
        }
    }

    /// <summary>The program that opens https:// links for this user, with its arguments ("%1" = the address).</summary>
    static (string Exe, string Arguments)? Default()
    {
        try
        {
            using var choice = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            if (choice?.GetValue("ProgId") is not string progId) return null;
            using var command = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
            if (command?.GetValue("") is not string line) return null;

            // "C:\...\msedge.exe" --single-argument %1   ·   "C:\...\firefox.exe" -osint -url "%1"
            line = line.Trim();
            string exe, rest;
            if (line.StartsWith('"'))
            {
                int close = line.IndexOf('"', 1);
                if (close < 0) return null;
                exe = line[1..close];
                rest = line[(close + 1)..].Trim();
            }
            else
            {
                int space = line.IndexOf(' ');
                exe = space < 0 ? line : line[..space];
                rest = space < 0 ? "" : line[(space + 1)..].Trim();
            }
            return File.Exists(exe) && rest.Contains("%1") ? (exe, rest) : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
