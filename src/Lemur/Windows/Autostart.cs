using Microsoft.Win32;
using Lemur.Config;
using Lemur.Logging;

namespace Lemur.App.Windows;

/// <summary>"Start when I sign in": the per-user Run key, so no admin rights are needed.</summary>
static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "Lemur";

    /// <summary>What Teams Recorder (Lemur before 1.0) called its entry.</summary>
    const string LegacyName = "TeamsRecorder";

    public static bool IsOn
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is string;
        }
    }

    /// <summary>A Teams Recorder entry becomes Lemur's, pointing at this exe (the old one no longer exists).</summary>
    public static void MoveLegacy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(LegacyName) is null) return;
            key.DeleteValue(LegacyName, throwOnMissingValue: false);
            Set(true);
            Log.Info("start at sign-in: moved from Teams Recorder's entry to Lemur's");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Warn($"could not move Teams Recorder's start-at-sign-in entry: {ex.Message}");
        }
    }

    public static void Set(bool on)
    {
        if (AppPaths.Home is not null) return; // a LEMUR_HOME copy (demo, test) never starts itself at sign-in
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on && Environment.ProcessPath is { } exe) key.SetValue(Name, $"\"{exe}\"");
            else key.DeleteValue(Name, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Warn($"could not change start-at-sign-in: {ex.Message}");
        }
    }
}
