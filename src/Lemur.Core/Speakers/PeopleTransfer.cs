using System.IO.Compression;
using System.Text.Json;
using Lemur.Config;

namespace Lemur.Speakers;

/// <summary>
/// Export/Import people (design 6.1): for moving to a PC without a shared OneDrive folder. Import merges
/// by person id with the same rules as sync conflicts, so importing twice changes nothing.
/// </summary>
public static class PeopleTransfer
{
    public static int Export(PeopleStore store, string zipPath)
    {
        var people = store.People();
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var p in people)
        {
            zip.CreateEntryFromFile(Path.Combine(store.Folder, "people", p.Id + ".json"), $"people/{p.Id}.json");
            foreach (var print in p.Prints)
                if (store.ClipPath(print) is { } clip && File.Exists(clip))
                    zip.CreateEntryFromFile(clip, print.Clip!);
        }
        var models = Path.Combine(store.Folder, "models");
        if (Directory.Exists(models))
            foreach (var m in Directory.EnumerateFiles(models, "*.json"))
                zip.CreateEntryFromFile(m, "models/" + Path.GetFileName(m));
        return people.Count;
    }

    public static (int Added, int Merged) Import(PeopleStore store, string zipPath)
    {
        int added = 0, merged = 0;
        var existing = store.LoadAll().ToDictionary(p => p.Id);
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            // Only the three folders we write, and nothing that climbs out of them.
            var parts = entry.FullName.Replace('\\', '/').Split('/');
            if (parts.Length < 2 || parts.Any(x => x is ".." or "") || parts[0] is not ("people" or "clips" or "models")) continue;
            var target = Path.Combine([store.Folder, .. parts]);

            if (parts[0] == "people")
            {
                using var reader = new StreamReader(entry.Open());
                var incoming = JsonSerializer.Deserialize<Person>(reader.ReadToEnd(), Json.Options);
                if (incoming is not { Id.Length: > 0 }) continue;
                if (existing.TryGetValue(incoming.Id, out var mine))
                {
                    AtomicFile.WriteJson(Path.Combine(store.Folder, "people", incoming.Id + ".json"), PeopleStore.Merge([mine, incoming]));
                    merged++;
                }
                else
                {
                    AtomicFile.WriteJson(Path.Combine(store.Folder, "people", incoming.Id + ".json"), incoming);
                    added++;
                }
            }
            else if (!File.Exists(target))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target);
            }
        }
        return (added, merged);
    }
}
