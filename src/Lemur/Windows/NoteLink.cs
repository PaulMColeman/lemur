using System.ComponentModel;
using System.Diagnostics;
using Lemur.Config;
using Lemur.Logging;
using Lemur.Pipeline;

namespace Lemur.App.Windows;

/// <summary>
/// lemur://note/&lt;recording&gt;[?t=&lt;seconds&gt;] (<see cref="NoteLinks"/>) — from a task board's card, the meeting file, a
/// chat: a second copy of the app opens that meeting's note page in the browser, at the moment the link names, and
/// exits. No tray and no window, but a message when the note isn't on this PC. The scheme is registered by
/// <see cref="NameLink"/>.
/// </summary>
static class NoteLink
{
    /// <summary>The meeting and moment a link among the arguments names, or null if none is a note link.</summary>
    public static (string Recording, int? Seconds)? In(string[] args)
    {
        foreach (var arg in args)
            if (NoteLinks.TryParse(arg, out var recording, out var seconds)) return (recording, seconds);
        return null;
    }

    public static void Open(string recording, int? seconds)
    {
        string? page = null;
        try
        {
            // Never set up on this PC: no notes to find, and making settings here would skip the first-run wizard.
            if (File.Exists(AppPaths.SettingsFile))
            {
                var settings = SettingsStore.LoadOrCreate();
                page = NoteLinks.FindNote(settings.ResolvedRecordingsFolder, settings.NotesFolder, recording);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            Log.Warn($"note link {recording}: settings unreadable ({ex.GetType().Name})");
        }
        if (page is null)
        {
            Log.Info($"note link {recording}: not on this PC");
            Ui.Info(null, "That meeting's note isn't on this PC.");
            return;
        }

        // Paths name the meeting, so the log says only which recording.
        Log.Info($"note link {recording}: opening it{(seconds is { } t ? $" at {t} s" : "")}");
        bool isPage = Path.GetExtension(page).Equals(".html", StringComparison.OrdinalIgnoreCase);
        if (isPage && Browser.Open(page, seconds is { } at ? $"t={at}" : null)) return;
        // No browser to ask (the page opens without its moment), or a note without a page (its Markdown app).
        try
        {
            Process.Start(new ProcessStartInfo(page) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Log.Warn($"note link {recording}: couldn't open its note ({Log.TypeAndCode(ex)})"); // the message has the path
            Ui.Warn(null, "Couldn't open that meeting's note.");
        }
    }
}
