using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lemur.Config;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

public static class SettingsStore
{
    /// <summary>Loads settings, writing the defaults first if the file does not exist yet.</summary>
    public static AppSettings LoadOrCreate(string? path = null)
    {
        path ??= AppPaths.SettingsFile;
        if (!File.Exists(path))
        {
            var fresh = new AppSettings();
            Save(fresh, path);
            return fresh;
        }
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json.Options)
                       ?? throw new InvalidDataException($"{path} is empty");
        // A file written before a rule list existed (or with it deleted) gets the defaults back.
        if (settings.Detection.Sources.Count == 0) settings.Detection.Sources = WatchRule.Defaults();
        // Built-in rules added since the file was written (Meet, Zoom, GoTo) join its list; to stop one, untick it.
        var defaults = WatchRule.Defaults();
        settings.Detection.Sources.AddRange(defaults.Where(d => !settings.Detection.Sources.Any(r => r.Name == d.Name)));
        // Files written before rules listed their audio processes: fill them in from the built-in rule of the same name.
        foreach (var rule in settings.Detection.Sources.Where(r => r.AudioProcesses.Count == 0))
            rule.AudioProcesses = defaults.FirstOrDefault(d => d.Name == rule.Name)?.AudioProcesses ?? [];
        return settings;
    }

    public static void Save(AppSettings settings, string? path = null)
    {
        path ??= AppPaths.SettingsFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json.Options));
        File.Move(tmp, path, overwrite: true);
    }
}
