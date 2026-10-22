using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lemur.Config;

namespace Lemur.App.Windows;

/// <summary>
/// "Copy diagnostics": settings, setup problems, which keys are saved (never their values) and the last log lines,
/// with anything that looks like a credential masked. Transcript text never reaches the log, so it isn't here either.
/// </summary>
static partial class DiagnosticsReport
{
    public const int LogLines = 50;

    public static string Build(AppSettings s, ISecretStore secrets, string logFolder)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Lemur {AboutPage.Version()}");
        sb.AppendLine($"Windows {Environment.OSVersion.Version}, .NET {Environment.Version}, {(Environment.Is64BitProcess ? "64" : "32")}-bit");
        sb.AppendLine($"Copied {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine();

        sb.AppendLine("Setup problems:");
        var problems = s.Problems();
        if (problems.Count == 0) sb.AppendLine("  none");
        foreach (var p in problems) sb.AppendLine($"  - {p}");
        sb.AppendLine();

        sb.AppendLine("Keys (saved or not; values are never copied):");
        foreach (var name in KeyNames(s)) sb.AppendLine($"  {name}: {(secrets.Get(name) is { Length: > 0 } ? "saved" : "not saved")}");
        sb.AppendLine();

        sb.AppendLine("Settings:");
        sb.AppendLine(Redact(JsonSerializer.Serialize(s, Json.Options)));
        sb.AppendLine();

        var (file, lines) = LastLines(logFolder, LogLines);
        sb.AppendLine(file is null ? "Log: none found" : $"Log ({Path.GetFileName(file)}, last {lines.Count} lines):");
        foreach (var line in lines) sb.AppendLine(Redact(line));
        return sb.ToString();
    }

    /// <summary>Masks API keys, tokens and passwords (the same shapes Core's Http.SafeSnippet masks, plus Bearer values).</summary>
    public static string Redact(string text) =>
        Secretish().Replace(text, m => m.Groups[1].Success ? m.Groups[1].Value + "<redacted>" : "<redacted>");

    static IEnumerable<string> KeyNames(AppSettings s) =>
        new[] { s.Backend.Scriberr.ApiKeySecret, s.Backend.OpenAi.TranscriptionApiKeySecret, s.Summary.ApiKeySecret, s.Speakers.ApiKeySecret }
            .OfType<string>().Where(n => n.Trim().Length > 0).Distinct();

    static (string? File, List<string> Lines) LastLines(string folder, int count)
    {
        try
        {
            var newest = new DirectoryInfo(folder).Exists
                ? new DirectoryInfo(folder).EnumerateFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
                : null;
            if (newest is null) return (null, []);
            // The recorder may be writing to it right now.
            using var stream = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var last = new Queue<string>();
            while (reader.ReadLine() is { } line)
            {
                last.Enqueue(line);
                if (last.Count > count) last.Dequeue();
            }
            return (newest.FullName, [.. last]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, [$"(log not readable: {ex.GetType().Name})"]);
        }
    }

    [GeneratedRegex(@"hf_[A-Za-z0-9]{8,}|sk-[A-Za-z0-9_\-]{8,}|\bBearer\s+[A-Za-z0-9._~+/\-]+=*|(?<![A-Za-z])(""?(?:hf_token|api_key|apikey|x-api-key|access_token|token|authorization|password)""?\s*[:=]\s*)(""[^""]*""|[^"",\s}]+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex Secretish();
}
