using Lemur.Config;

namespace Lemur.Tests;

public class UpgradeTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("llmmr-upgrade").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Teams_Recorders_folders_settings_and_keys_move_to_Lemur_once()
    {
        string oldConfig = Path.Combine(_root, "Roaming", "TeamsRecorder"), newConfig = Path.Combine(_root, "Roaming", "Lemur");
        string oldData = Path.Combine(_root, "Local", "TeamsRecorder"), newData = Path.Combine(_root, "Local", "Lemur");
        Directory.CreateDirectory(Path.Combine(oldConfig, "people"));
        Directory.CreateDirectory(Path.Combine(oldData, "recordings", "20261014-004936"));
        File.WriteAllText(Path.Combine(oldData, "recordings", "20261014-004936", "meta.json"), "{}");
        var notes = Path.Combine(_root, "Documents", "Meeting Notes");
        File.WriteAllText(Path.Combine(oldConfig, "settings.json"), $$"""
            {
              "displayName": "Paul",
              "recordingsFolder": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(oldData, "recordings"))}},
              "notesFolder": {{System.Text.Json.JsonSerializer.Serialize(notes)}},
              "speakers": { "enabled": true, "peopleFolder": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(oldConfig, "people"))}} }
            }
            """);
        var oldKeys = new MemorySecretStore();
        oldKeys.Set("scriberr-api-key", "s-1");
        oldKeys.Set("summary-api-key", "old-summary");
        var newKeys = new MemorySecretStore();
        newKeys.Set("summary-api-key", "new-summary"); // set up again under Lemur already: that one wins

        var log = LegacyMigration.Run(oldConfig, newConfig, oldData, newData, oldKeys, newKeys);

        Assert.False(Directory.Exists(oldConfig));
        Assert.False(Directory.Exists(oldData));
        Assert.True(File.Exists(Path.Combine(newData, "recordings", "20261014-004936", "meta.json")));
        var settings = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(newConfig, "settings.json")))!;
        Assert.Equal(Path.Combine(newData, "recordings"), (string?)settings["recordingsFolder"]);
        Assert.Equal(notes, (string?)settings["notesFolder"]);                             // not inside the old folders: untouched
        Assert.Equal(Path.Combine(newConfig, "people"), (string?)settings["speakers"]!["peopleFolder"]);
        Assert.Equal("Paul", (string?)settings["displayName"]);

        Assert.Equal("s-1", newKeys.Get("scriberr-api-key"));
        Assert.Null(oldKeys.Get("scriberr-api-key"));                                      // moved, not copied
        Assert.Equal("new-summary", newKeys.Get("summary-api-key"));
        Assert.Equal("old-summary", oldKeys.Get("summary-api-key"));                       // kept: Lemur had its own
        Assert.DoesNotContain(log, line => line.Contains("s-1") || line.Contains("summary\"") || line.Contains("old-summary"));

        // Running again finds nothing new to do.
        var again = LegacyMigration.Run(oldConfig, newConfig, oldData, newData, oldKeys, newKeys);
        Assert.Equal(["kept the old key 'summary-api-key': Lemur already has a different one"], again);
    }

    [Fact]
    public void A_Lemur_folder_that_already_has_files_is_never_overwritten()
    {
        string oldConfig = Path.Combine(_root, "old"), newConfig = Path.Combine(_root, "new");
        Directory.CreateDirectory(oldConfig);
        File.WriteAllText(Path.Combine(oldConfig, "settings.json"), "{}");
        Directory.CreateDirectory(newConfig);
        File.WriteAllText(Path.Combine(newConfig, "settings.json"), """{"displayName":"Lemur's own"}""");

        var log = LegacyMigration.Run(oldConfig, newConfig, Path.Combine(_root, "none"), Path.Combine(_root, "none2"),
            new MemorySecretStore(), new MemorySecretStore());

        Assert.Contains("already has files", Assert.Single(log));
        Assert.Contains("Lemur's own", File.ReadAllText(Path.Combine(newConfig, "settings.json")));
        Assert.True(File.Exists(Path.Combine(oldConfig, "settings.json")));
    }
}
