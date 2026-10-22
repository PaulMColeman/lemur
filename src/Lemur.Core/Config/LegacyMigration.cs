using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lemur.Config;

/// <summary>
/// Lemur was called Teams Recorder before 1.0. On Lemur's first start, Teams Recorder's settings and people
/// (%APPDATA%\TeamsRecorder), recordings and logs (%LOCALAPPDATA%\TeamsRecorder) and keys ("TeamsRecorder:&lt;name&gt;" in
/// Credential Manager) move to Lemur's names. Folders are renamed in place, not copied. Anything that already exists under
/// the new name is left alone, so this runs once and is safe to repeat. Notes are untouched: they never lived in AppData.
/// </summary>
public static class LegacyMigration
{
    /// <summary>Teams Recorder's single-instance mutex: while it exists, the old app is running and owns those folders.</summary>
    public const string LegacyTrayMutex = @"Local\TeamsRecorder.Tray";

    /// <summary>Teams Recorder left folders or keys behind (always false for a LEMUR_HOME copy).</summary>
    public static bool Needed => AppPaths.Home is null &&
        (Directory.Exists(AppPaths.LegacyConfigFolder) || Directory.Exists(AppPaths.LegacyDataFolder) ||
         new WindowsCredentialStore(AppPaths.LegacyFolderName).Names().Any());

    public static bool LegacyAppRunning
    {
        get
        {
            if (!Mutex.TryOpenExisting(LegacyTrayMutex, out var mutex)) return false;
            mutex.Dispose();
            return true;
        }
    }

    /// <summary>Moves what there is to move. Returns one line per thing moved or left, for the log (never a key's value).</summary>
    public static List<string> Run() =>
        AppPaths.Home is not null ? [] :
        Run(AppPaths.LegacyConfigFolder, AppPaths.ConfigFolder, AppPaths.LegacyDataFolder, AppPaths.DataFolder,
            new WindowsCredentialStore(AppPaths.LegacyFolderName), new WindowsCredentialStore());

    public static List<string> Run(string oldConfig, string newConfig, string oldData, string newData, ISecretStore oldKeys, ISecretStore newKeys)
    {
        var log = new List<string>();
        MoveFolder(oldConfig, newConfig, log);
        MoveFolder(oldData, newData, log);
        FixSettingsPaths(Path.Combine(newConfig, "settings.json"), [(oldConfig, newConfig), (oldData, newData)], log);
        MoveKeys(oldKeys, newKeys, log);
        return log;
    }

    static void MoveFolder(string from, string to, List<string> log)
    {
        if (!Directory.Exists(from)) return;
        try
        {
            if (Directory.Exists(to))
            {
                if (Directory.EnumerateFileSystemEntries(to).Any())
                {
                    log.Add($"left {from} where it is: {to} already has files");
                    return;
                }
                Directory.Delete(to);
            }
            Directory.Move(from, to);
            log.Add($"moved {from} to {to}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Add($"could not move {from} to {to}: {ex.Message}");
        }
    }

    /// <summary>Settings that name a folder inside the old ones (recordings, notes, people) are pointed at the new ones.</summary>
    static void FixSettingsPaths(string settingsFile, (string Old, string New)[] moves, List<string> log)
    {
        if (!File.Exists(settingsFile)) return;
        JsonObject? settings;
        try
        {
            settings = JsonNode.Parse(File.ReadAllText(settingsFile), documentOptions:
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
        }
        catch (JsonException)
        {
            return; // the app will say what's wrong with it when it loads it
        }
        if (settings is null) return;

        bool changed = false;
        foreach (var (parent, key) in new[] { (settings, "recordingsFolder"), (settings, "notesFolder"), (settings["speakers"] as JsonObject, "peopleFolder") })
        {
            if (parent?[key] is not JsonValue value || !value.TryGetValue<string>(out var path)) continue;
            foreach (var (old, now) in moves)
            {
                if (!path.Equals(old, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(old + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;
                parent[key] = now + path[old.Length..];
                log.Add($"settings: {key} now {parent[key]}");
                changed = true;
                break;
            }
        }
        if (!changed) return;
        var tmp = settingsFile + ".tmp";
        File.WriteAllText(tmp, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, settingsFile, overwrite: true);
    }

    /// <summary>Each key is copied, read back, and only then removed under the old name.</summary>
    static void MoveKeys(ISecretStore from, ISecretStore to, List<string> log)
    {
        foreach (var name in from.Names().ToList())
        {
            if (from.Get(name) is not { } value) continue;
            var existing = to.Get(name);
            if (existing is null)
            {
                to.Set(name, value);
                existing = to.Get(name);
            }
            if (existing == value)
            {
                from.Delete(name);
                log.Add($"moved key '{name}'");
            }
            else
            {
                log.Add($"kept the old key '{name}': Lemur already has a different one");
            }
        }
    }
}
