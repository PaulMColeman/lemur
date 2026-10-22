using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Lemur.Audio;
using Lemur.Logging;

namespace Lemur.Pipeline;

/// <summary>Where an imported recording came from, kept in its meta.json.</summary>
public sealed class ImportedFile
{
    /// <summary>The file's name when it was imported ("New Recording 12.m4a").</summary>
    public string Name { get; set; } = "";

    public long Bytes { get; set; }

    /// <summary>SHA-256 of the file, so the same file isn't made into notes twice.</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>Where <see cref="RecordingMeta.StartedAt"/> came from.</summary>
    public StartSource StartedFrom { get; set; }
}

/// <summary>How an imported recording's start time was worked out, most trustworthy first.</summary>
public enum StartSource
{
    /// <summary>Typed in when importing it.</summary>
    User,
    /// <summary>The time the phone or recorder wrote into the file (an MP4/M4A/MOV/3GP file's own creation time).</summary>
    Recording,
    /// <summary>A date and time in the file's name ("Voice 20261014_093000.m4a").</summary>
    FileName,
    /// <summary>When the file was last written, less its length: right when it was saved the moment the recording stopped.</summary>
    Modified,
}

/// <summary>What importing a file would give, before it is imported: for the Import window to show and change.</summary>
public sealed record ImportGuess(string Title, DateTimeOffset StartedAt, StartSource StartedFrom, double Seconds);

/// <summary>A file Lemur can't make a recording from. <see cref="Permanent"/>: trying again won't help (not audio, silent).</summary>
public sealed class ImportException(string message, bool permanent, Exception? inner = null) : Exception(message, inner)
{
    public bool Permanent { get; } = permanent;
}

/// <summary>
/// Turns a recording made somewhere else — a phone's voice memo, a dictaphone, a video's sound — into a Lemur recording
/// waiting for notes. The sound becomes the "mic" track and the call track is left empty, so it goes through the pipeline
/// as an in-person meeting: everyone is on one track, and you are recognised by your voice (if your voiceprint is known)
/// rather than by which track you were on. Windows' own decoders (Media Foundation) read the file.
/// </summary>
public static partial class RecordingImport
{
    /// <summary>meta.json's source for imported recordings.</summary>
    public const string Source = "Imported";

    /// <summary>
    /// Files worth trying: what phones save (m4a, 3gp, amr), recorders (mp3, wav, wma, flac) and videos (their sound). Windows
    /// reads most of these out of the box; ogg, opus and webm need Microsoft's free "Web Media Extensions".
    /// </summary>
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".m4a", ".mp3", ".wav", ".aac", ".mp4", ".mov", ".m4v", ".wma", ".flac", ".ogg", ".opus", ".3gp", ".3gpp", ".amr", ".webm",
    };

    public static bool LooksLikeAudio(string path) => Extensions.Contains(Path.GetExtension(path));

    /// <summary>The kinds of file that keep their recording time in an MP4 movie header.</summary>
    static readonly HashSet<string> Mp4Family = new(StringComparer.OrdinalIgnoreCase) { ".m4a", ".mp4", ".mov", ".m4v", ".3gp", ".3gpp" };

    /// <summary>The title, start time and length importing <paramref name="path"/> would give.</summary>
    /// <exception cref="ImportException">Windows can't read it.</exception>
    public static ImportGuess Guess(string path)
    {
        double seconds;
        using (var reader = Open(path)) seconds = reader.TotalTime.TotalSeconds;
        var (started, from) = StartOf(path, seconds);
        return new ImportGuess(TitleFrom(Path.GetFileName(path)), started, from, seconds);
    }

    /// <summary>
    /// Makes a new recording folder from <paramref name="path"/>, waiting for notes. The file itself is only read.
    /// </summary>
    /// <param name="title">The meeting's title, chosen by the user ("" for none yet); null takes the file's name as a
    /// stand-in that its summary's title replaces.</param>
    /// <param name="startedAt">When the meeting began; null works it out from the file.</param>
    /// <exception cref="ImportException">It isn't a recording Lemur can use.</exception>
    public static RecordingFolder Import(string root, string path, string? title = null, DateTimeOffset? startedAt = null,
        CancellationToken ct = default)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("The file isn't there any more.", path);
        Directory.CreateDirectory(root);
        CleanUp(root);
        var sha = Sha256Of(path);
        // Decoded beside the recordings first: a folder appears only once its audio is whole, and its name needs the
        // start time, which for some files needs the exact length.
        var tmp = Path.Combine(root, $".import-{Guid.NewGuid():N}.part");
        RecordingFolder? rec = null;
        try
        {
            double seconds = Decode(path, tmp, ct);
            var (started, from) = startedAt is { } given ? (given, StartSource.User) : StartOf(path, seconds);
            rec = RecordingFolder.CreateNew(root, started);
            File.Move(tmp, rec.MicWav);
            new WavSampleSink(rec.OthersWav).Dispose(); // no call audio: an in-person meeting
            var named = title is null ? TitleFrom(file.Name) : MeetingAbout.TypedTitle(title) ?? "";
            rec.SaveMeta(new RecordingMeta
            {
                Title = named,
                TitleByUser = title is not null && named.Length > 0,
                TitleFromFile = title is null && named.Length > 0,
                Source = Source,
                Manual = true,
                StartedAt = started,
                EndedAt = started.AddSeconds(seconds),
                DurationSeconds = Math.Round(seconds, 1),
                Imported = new ImportedFile { Name = file.Name, Bytes = file.Length, Sha256 = sha, StartedFrom = from },
            });
            rec.SaveState(new ProcessingState());
            Log.Info($"{rec.Name}: imported {seconds:0}s from a {file.Extension.TrimStart('.').ToLowerInvariant()} file " +
                     $"({file.Length / 1e6:0.0} MB; start time from {from.ToString().ToLowerInvariant()})");
            return rec;
        }
        catch
        {
            if (rec is not null)
            {
                try { rec.Delete(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"{rec.Name}: half-imported folder left: {ex.Message}"); }
            }
            throw;
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    /// <summary>The recording already made from a file with these bytes, if there is one.</summary>
    public static RecordingFolder? FindImported(string root, string sha256)
    {
        foreach (var rec in RecordingFolder.All(root))
        {
            try
            {
                if (string.Equals(rec.Read<RecordingMeta>(rec.MetaFile)?.Imported?.Sha256, sha256, StringComparison.OrdinalIgnoreCase)) return rec;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        }
        return null;
    }

    public static string Sha256Of(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>
    /// Why <paramref name="importFolder"/> can't be the import folder, or null. Not the notes folder (its MP3s would be
    /// imported back) nor the recordings folder.
    /// </summary>
    public static string? FolderProblem(string importFolder, string notesFolder, string recordingsFolder)
    {
        if (Same(importFolder, notesFolder)) return "The import folder can't be your notes folder: Lemur would import the notes' own MP3s.";
        if (Same(importFolder, recordingsFolder)) return "The import folder can't be the recordings folder.";
        return null;

        static bool Same(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }
    }

    /// <summary>The file's sound as 16 kHz mono, the way the recorder writes its tracks. Returns its length in seconds.</summary>
    static double Decode(string path, string wavPath, CancellationToken ct)
    {
        int rate = AnchoredTrackWriter.SampleRate;
        long total = 0;
        float peak = 0;
        using (var reader = Open(path))
        {
            ISampleProvider samples;
            try
            {
                samples = reader.ToSampleProvider();
            }
            catch (ArgumentException ex)
            {
                throw new ImportException($"its sound is in a form Lemur can't read ({reader.WaveFormat.Encoding}).", permanent: true, ex);
            }
            if (samples.WaveFormat.Channels > 1) samples = new DownmixToMono(samples);
            if (samples.WaveFormat.SampleRate != rate) samples = new WdlResamplingSampleProvider(samples, rate);
            var buffer = new float[rate];
            using var sink = new WavSampleSink(wavPath);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int n;
                try
                {
                    n = samples.Read(buffer, 0, buffer.Length);
                }
                catch (COMException ex)
                {
                    throw new ImportException($"Windows couldn't read all of it (0x{ex.HResult:X8}); the file may be damaged.", permanent: true, ex);
                }
                if (n == 0) break;
                for (int i = 0; i < n; i++) peak = Math.Max(peak, Math.Abs(buffer[i]));
                sink.Write(buffer.AsSpan(0, n));
                total += n;
            }
        }
        if (total < rate) throw new ImportException("it has less than a second of sound in it.", permanent: true);
        if (peak < 1e-4f) throw new ImportException("it is silent.", permanent: true);
        return total / (double)rate;
    }

    /// <summary>A reader for the file's sound: WAV directly, anything else through Media Foundation.</summary>
    static WaveStream Open(string path)
    {
        if (Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var wav = new WaveFileReader(path);
                if (wav.WaveFormat.Encoding is WaveFormatEncoding.Pcm or WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Extensible) return wav;
                wav.Dispose(); // ADPCM, µ-law…: Media Foundation may know it
            }
            catch (FormatException) { }
        }
        try
        {
            return new NAudio.Wave.MediaFoundationReader(path);
        }
        catch (COMException ex) when ((ex.HResult & 0xFFFF0000) == 0x80070000)
        {
            // A Windows error, not Media Foundation's verdict on the file: in use, no access, a sync client not running.
            throw new ImportException($"it couldn't be opened just now (0x{ex.HResult:X8}).", permanent: false, ex);
        }
        catch (COMException ex)
        {
            throw new ImportException(ex.HResult == unchecked((int)0xC00D36C4) // MF_E_UNSUPPORTED_BYTESTREAM_TYPE
                ? "Windows can't read this kind of file."
                : $"Windows couldn't open it as audio (0x{ex.HResult:X8}).", permanent: true, ex);
        }
    }

    /// <summary>Temporary files left by an import that never finished (the app was closed or crashed during one).</summary>
    static void CleanUp(string root)
    {
        foreach (var tmp in Directory.EnumerateFiles(root, ".import-*.part"))
        {
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(tmp) > TimeSpan.FromHours(6)) File.Delete(tmp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>When the recording began: the time inside the file, a date and time in its name, or its last write less its length.</summary>
    public static (DateTimeOffset StartedAt, StartSource From) StartOf(string path, double seconds)
    {
        if (RecordedAt(path) is { } at) return (at, StartSource.Recording);
        if (FromName(Path.GetFileNameWithoutExtension(path)) is { } named) return (named, StartSource.FileName);
        return (new DateTimeOffset(File.GetLastWriteTime(path)).AddSeconds(-seconds), StartSource.Modified);
    }

    static bool Plausible(DateTimeOffset t) => t.Year >= 2005 && t <= DateTimeOffset.Now.AddDays(1);

    /// <summary>
    /// The creation time an MP4-family file (m4a, mp4, mov, 3gp) carries in its movie header: phones write the moment the
    /// recording began, in UTC. Null for other files, or a time that can't be right (0, the year 1904, the future).
    /// </summary>
    public static DateTimeOffset? RecordedAt(string path)
    {
        if (!Mp4Family.Contains(Path.GetExtension(path))) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (FindBox(stream, stream.Length, "moov") is not { } moov) return null;
            if (FindBox(stream, moov.End, "mvhd") is not { } mvhd) return null;
            stream.Position = mvhd.Start;
            Span<byte> head = stackalloc byte[12];
            if (stream.Read(head) < 12) return null;
            ulong created = head[0] == 1 ? BinaryPrimitives.ReadUInt64BigEndian(head[4..12]) : BinaryPrimitives.ReadUInt32BigEndian(head[4..8]);
            if (created == 0) return null; // not set
            var at = new DateTimeOffset(1904, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(created);
            return Plausible(at) ? at.ToLocalTime() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>The next box of this type from the stream's position up to <paramref name="end"/>: where its contents start and end.</summary>
    static (long Start, long End)? FindBox(Stream s, long end, string type)
    {
        Span<byte> header = stackalloc byte[16];
        while (s.Position + 8 <= end)
        {
            long at = s.Position;
            if (s.Read(header[..8]) < 8) return null;
            long size = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            int headerSize = 8;
            if (size == 1)
            {
                if (s.Read(header[8..16]) < 8) return null;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..16]);
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = end - at; // runs to the end
            }
            if (size < headerSize || at + size > end) return null; // not an MP4 file, or a damaged one
            if (header[4] == type[0] && header[5] == type[1] && header[6] == type[2] && header[7] == type[3])
                return (at + headerSize, at + size);
            s.Position = at + size;
        }
        return null;
    }

    /// <summary>A date and time written into a file's name: "Voice 20261014_093000", "2026-10-14 09.30", "2026-10-14 0930 …".</summary>
    public static DateTimeOffset? FromName(string name)
    {
        var m = DateInName().Match(name);
        if (!m.Success) return null;
        int Part(string g) => m.Groups[g].Success ? int.Parse(m.Groups[g].Value) : 0;
        try
        {
            var local = new DateTime(Part("y"), Part("mo"), Part("d"), Part("h"), Part("mi"), Part("s"), DateTimeKind.Local);
            var at = new DateTimeOffset(local);
            return Plausible(at) ? at : null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null; // 31 February
        }
    }

    /// <summary>
    /// A title from a file's name, without the date in it and with underscores as spaces; "" when the name says nothing
    /// about the meeting (what phones and recorders call recordings: "New Recording 12", "Voice 003", "ZOOM0001").
    /// </summary>
    public static string TitleFrom(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        stem = DateInName().Replace(stem, " ");
        stem = Spaces().Replace(stem.Replace('_', ' '), " ").Trim(' ', '-', '.', ',', '–', '—');
        if (stem.Length == 0 || NothingSaid().IsMatch(stem) || MeetingAbout.IsUntitled(stem)) return "";
        return stem.Length <= MeetingAbout.MaxTypedTitle ? stem : stem[..MeetingAbout.MaxTypedTitle].TrimEnd();
    }

    [GeneratedRegex(@"(?<!\d)(?<y>20\d\d)[-_. ]?(?<mo>0[1-9]|1[0-2])[-_. ]?(?<d>0[1-9]|[12]\d|3[01])(?:[ T_.-]+|\s*at\s*)?(?<h>[01]\d|2[0-3])[-_.:h ]?(?<mi>[0-5]\d)(?:[-_.:m ]?(?<s>[0-5]\d))?(?!\d)",
        RegexOptions.IgnoreCase)]
    private static partial Regex DateInName();

    [GeneratedRegex(@"^(?:(?:new\s+)?(?:recording|voice\s*memo|memo|voice|audio|sound|track|rec|record|dictation|untitled|clip|video|movie|meeting)(?:\s*\d+)?|(?:img|vid|mov|dsc|pxl|dji|gopr|zoom|r)\s*\d+\w*|[\d\s().-]+)(?:\s*\(\d+\))?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex NothingSaid();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
