using Lemur.Logging;

namespace Lemur.Pipeline;

/// <summary>
/// The import folder (settings.import.folder): recordings put there become notes. A phone's voice memo saved to a folder
/// that syncs to this PC (OneDrive, iCloud, Nextcloud), or a recorder's file copied in. Each pass looks at the files in
/// the folder itself (not its subfolders); a file is taken once it has stopped changing between two passes, since a sync
/// client or a copy writes it a piece at a time. Once its recording is made it moves to "Imported" inside the folder:
/// Lemur never deletes it. A file that can't be a recording (not audio Windows can read, silent) moves to
/// "Couldn't import", so it isn't tried again and again; anything else (in use, a full disk) is tried again later.
/// </summary>
public sealed class ImportFolder
{
    public const string ImportedName = "Imported";
    public const string FailedName = "Couldn't import";

    /// <summary>A file this new may still be being written, however still it looks.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

    /// <summary>After a failure that may pass (the file in use, a full disk), when to try that file again.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);

    readonly Dictionary<string, (long Size, DateTime Written)> _seen = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, DateTime> _wait = new(StringComparer.OrdinalIgnoreCase);
    readonly Func<DateTime> _utcNow;

    /// <param name="utcNow">The clock (tests).</param>
    public ImportFolder(Func<DateTime>? utcNow = null) => _utcNow = utcNow ?? (() => DateTime.UtcNow);

    /// <summary>What a pass did with one file. <see cref="Problem"/> is set when it couldn't be imported.</summary>
    /// <param name="MovedTo">Where the file is now, if it was moved.</param>
    public sealed record Result(string FileName, RecordingFolder? Recording, bool AlreadyImported, string? Problem, string? MovedTo);

    /// <summary>One pass over the folder: imports each file that has settled since the last pass.</summary>
    public List<Result> Scan(string folder, string recordingsRoot, CancellationToken ct = default)
    {
        var results = new List<Result>();
        if (!Directory.Exists(folder)) return results;
        var now = _utcNow();
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(folder))
        {
            ct.ThrowIfCancellationRequested();
            var file = new FileInfo(path);
            if (!Wanted(file)) continue;
            present.Add(path);
            if (_wait.TryGetValue(path, out var until) && now < until) continue;
            var look = (file.Length, file.LastWriteTimeUtc);
            bool settled = _seen.TryGetValue(path, out var before) && before == look && now - file.LastWriteTimeUtc >= Settle;
            _seen[path] = look;
            if (!settled || !Unlocked(path)) continue;
            if (Take(file, recordingsRoot, ct) is { } result) results.Add(result);
        }
        foreach (var gone in _seen.Keys.Where(k => !present.Contains(k)).ToList()) _seen.Remove(gone);
        foreach (var gone in _wait.Keys.Where(k => !present.Contains(k)).ToList()) _wait.Remove(gone);
        return results;
    }

    Result? Take(FileInfo file, string root, CancellationToken ct)
    {
        var path = file.FullName;
        try
        {
            if (RecordingImport.FindImported(root, RecordingImport.Sha256Of(path)) is { } earlier)
            {
                Log.Info($"{earlier.Name}: a file already imported was put in the import folder again; moved to {ImportedName}");
                return new Result(file.Name, earlier, AlreadyImported: true, null, MoveAside(path, ImportedName));
            }
            var rec = RecordingImport.Import(root, path, ct: ct);
            string? moved = null;
            try
            {
                moved = MoveAside(path, ImportedName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Imported, but still in the folder: the next pass finds it already imported and moves it then.
                Log.Warn($"{rec.Name}: imported, but its file couldn't be moved to {ImportedName} yet: {Why(ex)}");
            }
            _seen.Remove(path);
            return new Result(file.Name, rec, AlreadyImported: false, null, moved);
        }
        catch (ImportException ex) when (ex.Permanent)
        {
            Log.Warn($"import: a {file.Extension.TrimStart('.').ToLowerInvariant()} file couldn't be imported: {ex.Message}");
            string? moved = null;
            try { moved = MoveAside(path, FailedName); }
            catch (Exception move) when (move is IOException or UnauthorizedAccessException) { _wait[path] = _utcNow() + RetryAfter; }
            _seen.Remove(path);
            return new Result(file.Name, null, AlreadyImported: false, ex.Message, moved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ImportException or System.Runtime.InteropServices.COMException)
        {
            Log.Warn($"import: a {file.Extension.TrimStart('.').ToLowerInvariant()} file couldn't be imported this time, trying again in " +
                     $"{RetryAfter.TotalMinutes:0} min: {Why(ex)}");
            _wait[path] = _utcNow() + RetryAfter;
            return null;
        }
    }

    /// <summary>What went wrong, for the log: a file system error's message names the file, which may say what the meeting was.</summary>
    static string Why(Exception ex) => ex is ImportException ? ex.Message : $"{ex.GetType().Name} 0x{ex.HResult:X8}";

    /// <summary>Audio files only; not hidden ones, nor what a download, a copy or an editor leaves while still at work.</summary>
    static bool Wanted(FileInfo f) =>
        RecordingImport.LooksLikeAudio(f.Name) &&
        (f.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.Temporary)) == 0 &&
        !f.Name.StartsWith('.') && !f.Name.StartsWith('~');

    /// <summary>Nobody is writing it: it opens for reading while refusing writers.</summary>
    static bool Unlocked(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Moves the file into <paramref name="subfolder"/> beside it, as "name (2).m4a" if the name is taken.</summary>
    static string MoveAside(string path, string subfolder)
    {
        var dir = Path.Combine(Path.GetDirectoryName(path)!, subfolder);
        Directory.CreateDirectory(dir);
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var to = Path.Combine(dir, stem + ext);
        for (int i = 2; File.Exists(to); i++) to = Path.Combine(dir, $"{stem} ({i}){ext}");
        File.Move(path, to);
        return to;
    }
}
