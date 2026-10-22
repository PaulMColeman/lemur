
namespace Lemur.Config;

/// <summary>Per-user locations. Nothing here is shared between Windows accounts.</summary>
public static class AppPaths
{
    public const string AppFolderName = "Lemur";

    /// <summary>The app's name before 1.0 (Teams Recorder). Its folders and keys are moved on first start (<see cref="LegacyMigration"/>).</summary>
    public const string LegacyFolderName = "TeamsRecorder";

    /// <summary>
    /// LEMUR_HOME keeps settings and data in that one folder instead of AppData: a separate copy of the app for demos,
    /// screenshots and tests, which can run beside the real one, with its own keys (CredentialPrefix).
    /// </summary>
    public static string? Home => Environment.GetEnvironmentVariable("LEMUR_HOME") is { Length: > 0 } home ? Path.GetFullPath(home) : null;

    /// <summary>Added to the names of the single-instance mutex and events, so a LEMUR_HOME copy doesn't collide with the real one.</summary>
    public static string InstanceSuffix => Home is { } home ? "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(home.ToLowerInvariant())))[..12] : "";

    /// <summary>
    /// Keys are "Lemur:&lt;name&gt;" in Credential Manager; a LEMUR_HOME copy gets its own prefix, so a demo or test can
    /// never read or overwrite the real keys.
    /// </summary>
    public static string CredentialPrefix => AppFolderName + InstanceSuffix;

    /// <summary>Roaming: settings follow the user. %APPDATA%\Lemur</summary>
    public static string ConfigFolder => Home is { } home ? Path.Combine(home, "config") : Path.Combine(RoamingRoot, AppFolderName);

    /// <summary>Local: audio and logs stay on this PC. %LOCALAPPDATA%\Lemur</summary>
    public static string DataFolder => Home is { } home ? Path.Combine(home, "data") : Path.Combine(LocalRoot, AppFolderName);

    public static string LegacyConfigFolder => Path.Combine(RoamingRoot, LegacyFolderName);
    public static string LegacyDataFolder => Path.Combine(LocalRoot, LegacyFolderName);

    static string RoamingRoot => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    static string LocalRoot => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string SettingsFile => Path.Combine(ConfigFolder, "settings.json");
    public static string LogFolder => Path.Combine(DataFolder, "logs");
    public static string DefaultRecordingsFolder => Path.Combine(DataFolder, "recordings");

    public static string DefaultNotesFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Meeting Notes");

    /// <summary>Roaming AppData: travels with roaming profiles; OneDrive is the usual way to share it.</summary>
    public static string DefaultPeopleFolder => Path.Combine(ConfigFolder, "people");
}
