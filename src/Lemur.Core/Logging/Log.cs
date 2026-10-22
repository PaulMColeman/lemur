namespace Lemur.Logging;

/// <summary>
/// Append-only daily log file. Never pass secrets, API responses that may carry them,
/// or transcript text to it — a log is the first thing people paste into a bug report.
/// </summary>
public static class Log
{
    static readonly object Gate = new();
    static string? _folder;

    /// <summary>Mirror every line here as well (the CLI points this at the console).</summary>
    public static Action<string>? Echo { get; set; }

    public static void Init(string folder)
    {
        Directory.CreateDirectory(folder);
        _folder = folder;
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    /// <summary>
    /// An exception for a log line, when its message may name a meeting or quote one: its type and Windows' error code
    /// only. A note's path is its title, and .NET puts the file in a failed Process.Start's message, for one.
    /// </summary>
    public static string TypeAndCode(Exception ex) => ex is System.ComponentModel.Win32Exception w
        ? $"{ex.GetType().Name} {w.NativeErrorCode}"
        : $"{ex.GetType().Name} 0x{ex.HResult:X8}";

    static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level,-5} {message}";
        Echo?.Invoke(line);
        if (_folder is null) return;
        lock (Gate)
        {
            try
            {
                File.AppendAllText(Path.Combine(_folder, $"lemur-{DateTime.Now:yyyyMMdd}.log"),
                    line + Environment.NewLine);
            }
            catch
            {
                // Logging must never take the recorder down.
            }
        }
    }
}
