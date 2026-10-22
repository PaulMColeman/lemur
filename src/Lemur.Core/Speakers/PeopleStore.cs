using System.Text.Json;
using Lemur.Config;
using Lemur.Logging;

namespace Lemur.Speakers;

public sealed class Consent
{
    public DateTimeOffset At { get; set; }
    public string? Note { get; set; }
}

public sealed class PrintSource
{
    public string Recording { get; set; } = "";
    public string? Title { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>One meeting's average voiceprint of one person (design 3.4).</summary>
public sealed class Voiceprint
{
    public string Id { get; set; } = "";
    public VoiceKind Kind { get; set; }

    /// <summary>Fingerprint of the model that made it (<see cref="Calibration"/>); only compared with its own model.</summary>
    public string Model { get; set; } = "";

    public string Vector { get; set; } = "";

    /// <summary>Seconds of audio that went into it.</summary>
    public double Seconds { get; set; }

    public PrintSource? From { get; set; }

    /// <summary>A short reference clip (design D2), relative to the people folder; lets a new model recompute.</summary>
    public string? Clip { get; set; }

    /// <summary>True when a person named it; false when it was added from a clear automatic match.</summary>
    public bool Confirmed { get; set; }

    public DateTimeOffset Added { get; set; }

    float[]? _vector;
    public float[] Values() => _vector ??= Vectors.FromBase64(Vector);
}

public sealed class Person
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Aliases { get; set; } = [];
    public bool IsMe { get; set; }
    public Consent? Consent { get; set; }
    public DateTimeOffset Updated { get; set; }

    /// <summary>Set on a deletion stub: no name, no voiceprints, kept so an offline PC can't bring them back.</summary>
    public DateTimeOffset? Deleted { get; set; }

    public List<Voiceprint> Prints { get; set; } = [];

    public bool IsDeleted => Deleted is not null;

    public bool Answers(string name) =>
        !IsDeleted && (Same(Name, name) || Aliases.Any(a => Same(a, name)));

    static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The people folder (design §5): <c>people\&lt;id&gt;.json</c> per person, <c>clips\&lt;id&gt;\</c> for reference
/// clips, <c>models\</c> for model fingerprints. Built to live in a sync folder shared by several PCs:
/// atomic writes, conflict copies merged by the id inside them, deletions kept as stubs.
/// </summary>
public sealed class PeopleStore
{
    public const int MaxPerKind = 3;
    public static readonly TimeSpan StubLifetime = TimeSpan.FromDays(90);

    /// <summary>A voiceprint this close to one already kept adds nothing (design 3.4). Refit after the benchmark.</summary>
    public const double SameAsKept = 0.92;

    public PeopleStore(string folder) => Folder = folder;

    public string Folder { get; }
    string PeopleDir => Path.Combine(Folder, "people");
    public ModelRegistry Models => new(Folder);

    /// <summary>Everyone, merged. Conflict copies are folded into <c>&lt;id&gt;.json</c> and removed.</summary>
    public IReadOnlyList<Person> LoadAll(DateTimeOffset? now = null)
    {
        if (!Directory.Exists(PeopleDir)) return [];
        var copies = new List<(string File, Person Person)>();
        foreach (var file in Directory.EnumerateFiles(PeopleDir, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<Person>(File.ReadAllText(file), Json.Options) is { Id.Length: > 0 } p)
                    copies.Add((file, p));
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                Log.Warn($"people: skipped unreadable {Path.GetFileName(file)}: {ex.GetType().Name}");
            }
        }

        var result = new List<Person>();
        var clock = now ?? DateTimeOffset.Now;
        foreach (var group in copies.GroupBy(c => c.Person.Id))
        {
            var merged = group.Count() == 1 ? group.First().Person : Merge(group.Select(c => c.Person).ToList());
            var canonical = FileFor(merged.Id);
            if (merged.IsDeleted && clock - merged.Deleted!.Value > StubLifetime)
            {
                foreach (var (file, _) in group) TryDelete(file);
                continue;
            }
            if (group.Count() > 1 || !group.Any(c => PathEquals(c.File, canonical)))
            {
                AtomicFile.WriteJson(canonical, merged);
                foreach (var (file, _) in group)
                    if (!PathEquals(file, canonical)) TryDelete(file);
            }
            result.Add(merged);
        }
        return result;
    }

    /// <summary>Living people only.</summary>
    public IReadOnlyList<Person> People(DateTimeOffset? now = null) => LoadAll(now).Where(p => !p.IsDeleted).ToList();

    public Person? Find(string idOrName)
    {
        var all = People();
        return all.FirstOrDefault(p => p.Id == idOrName) ?? all.FirstOrDefault(p => p.Answers(idOrName));
    }

    public Person? Me() => People().FirstOrDefault(p => p.IsMe);

    public Person Create(string name, bool isMe = false)
    {
        var person = new Person { Id = Guid.NewGuid().ToString("N")[..6], Name = name.Trim(), IsMe = isMe };
        Save(person);
        return person;
    }

    public void Save(Person person, DateTimeOffset? now = null)
    {
        person.Updated = now ?? DateTimeOffset.Now;
        AtomicFile.WriteJson(FileFor(person.Id), person);
    }

    /// <summary>Removes voiceprints, clips and the name at once; leaves a stub (design §5).</summary>
    public void Delete(Person person, DateTimeOffset? now = null)
    {
        var clock = now ?? DateTimeOffset.Now;
        var stub = new Person { Id = person.Id, Deleted = clock };
        Save(stub, clock);
        var clips = Path.Combine(Folder, "clips", person.Id);
        if (Directory.Exists(clips)) Directory.Delete(clips, recursive: true);
    }

    /// <summary>Moves <paramref name="from"/>'s voiceprints and names into <paramref name="into"/>, then deletes it.</summary>
    public void MergeInto(Person from, Person into)
    {
        foreach (var print in from.Prints)
        {
            if (print.Clip is not null) print.Clip = MoveClip(print.Clip, into.Id);
            into.Prints.Add(print);
        }
        foreach (var alias in from.Aliases.Append(from.Name))
            if (!into.Answers(alias)) into.Aliases.Add(alias);
        into.Consent ??= from.Consent;
        Trim(into);
        Save(into);
        Delete(from);
    }

    /// <summary>
    /// Adds a voiceprint if it brings something new (design 3.4): the first of its kind always; later ones
    /// only if they differ from those kept; at the limit, replacing the most redundant. Returns whether it was kept.
    /// </summary>
    public static bool AddPrint(Person person, Voiceprint print)
    {
        var same = person.Prints.Where(p => p.Kind == print.Kind && p.Model == print.Model).ToList();
        if (same.Any(p => Vectors.Cosine(p.Values(), print.Values()) >= SameAsKept)) return false;
        person.Prints.Add(print);
        Trim(person);
        return person.Prints.Contains(print);
    }

    /// <summary>Keeps at most <see cref="MaxPerKind"/> per kind and model, dropping whichever is most like the rest.</summary>
    static void Trim(Person person)
    {
        foreach (var group in person.Prints.GroupBy(p => (p.Kind, p.Model)).ToList())
        {
            var set = group.ToList();
            while (set.Count > MaxPerKind)
            {
                // Confirmed voiceprints outrank automatic ones; among equals, drop the most redundant.
                var drop = set
                    .OrderBy(p => p.Confirmed)
                    .ThenByDescending(p => set.Where(o => o != p).Max(o => Vectors.Cosine(o.Values(), p.Values())))
                    .First();
                set.Remove(drop);
                person.Prints.Remove(drop);
            }
        }
    }

    /// <summary>Voiceprints whose model no PC has used for <see cref="StubLifetime"/> are removed.</summary>
    public void PruneUnusedModels(DateTimeOffset now)
    {
        var live = Models.All().Where(m => now - m.LastSeen <= StubLifetime).Select(m => m.Id).ToHashSet();
        if (live.Count == 0) return;
        foreach (var person in People(now))
        {
            int before = person.Prints.Count;
            person.Prints.RemoveAll(p => !live.Contains(p.Model));
            if (person.Prints.Count != before) Save(person, now);
        }
    }

    public string SaveClip(string personId, string printId, byte[] wav)
    {
        var relative = Path.Combine("clips", personId, printId + ".wav");
        AtomicFile.WriteBytes(Path.Combine(Folder, relative), wav);
        return relative.Replace('\\', '/');
    }

    public string? ClipPath(Voiceprint print) =>
        print.Clip is null ? null : Path.Combine(Folder, print.Clip.Replace('/', Path.DirectorySeparatorChar));

    string MoveClip(string relative, string newPersonId)
    {
        var source = Path.Combine(Folder, relative.Replace('/', Path.DirectorySeparatorChar));
        var target = Path.Combine("clips", newPersonId, Path.GetFileName(source));
        if (File.Exists(source))
        {
            Directory.CreateDirectory(Path.Combine(Folder, "clips", newPersonId));
            File.Move(source, Path.Combine(Folder, target), overwrite: true);
        }
        return target.Replace('\\', '/');
    }

    /// <summary>
    /// Folds copies of one person (e.g. OneDrive conflict copies) into one: voiceprints combined by id, the
    /// newest copy's name/aliases/consent, and a deletion that is newer than everything else wins.
    /// </summary>
    internal static Person Merge(IReadOnlyList<Person> copies)
    {
        var newest = copies.OrderByDescending(c => c.Updated).First();
        var deletion = copies.Where(c => c.IsDeleted).OrderByDescending(c => c.Deleted).FirstOrDefault();
        var lastLiving = copies.Where(c => !c.IsDeleted).Select(c => c.Updated).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
        if (deletion is not null && deletion.Deleted >= lastLiving)
            return new Person { Id = deletion.Id, Deleted = deletion.Deleted, Updated = deletion.Updated };

        var living = copies.Where(c => !c.IsDeleted).OrderByDescending(c => c.Updated).First();
        var merged = new Person
        {
            Id = living.Id,
            Name = living.Name,
            Aliases = [.. living.Aliases],
            IsMe = living.IsMe,
            Consent = living.Consent,
            Updated = newest.Updated,
            Prints = copies.Where(c => !c.IsDeleted).SelectMany(c => c.Prints).GroupBy(p => p.Id).Select(g => g.First()).ToList(),
        };
        Trim(merged);
        return merged;
    }

    string FileFor(string id) => Path.Combine(PeopleDir, id + ".json");

    static bool PathEquals(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (IOException ex) { Log.Warn($"people: could not remove {Path.GetFileName(file)}: {ex.Message}"); }
    }
}
