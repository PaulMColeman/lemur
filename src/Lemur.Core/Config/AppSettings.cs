namespace Lemur.Config;

/// <summary>
/// Everything a user can change, saved as %APPDATA%\Lemur\settings.json.
/// Secrets are NOT here: settings only name them, and <see cref="ISecretStore"/> holds the values.
/// Nothing defaults to a particular server — moving to another domain is a settings edit.
/// </summary>
public sealed class AppSettings
{
    /// <summary>How the recording user is named in transcripts and notes.</summary>
    public string DisplayName { get; set; } = Environment.UserName;

    public string NotesFolder { get; set; } = AppPaths.DefaultNotesFolder;

    /// <summary>Null = %LOCALAPPDATA%\Lemur\recordings.</summary>
    public string? RecordingsFolder { get; set; }

    /// <summary>Raw audio of processed meetings is deleted after this many days (-1: never), unless the meeting is marked Keep
    /// recording; notes are kept.</summary>
    public int KeepAudioDays { get; set; } = 14;

    /// <summary>Whether each note's MP3 goes too, after <see cref="KeepAudioDays"/> (<see cref="Pipeline.AudioCleanUp"/>).</summary>
    public NoteMp3Settings NoteMp3
    {
        get => _noteMp3;
        set => _noteMp3 = value ?? new(); // "noteMp3": null in a hand-edited file
    }

    NoteMp3Settings _noteMp3 = new();

    public DetectionSettings Detection { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();

    /// <summary>Recordings made elsewhere (a phone, a recorder) that Lemur picks up and makes notes from.</summary>
    public ImportSettings Import
    {
        get => _import;
        set => _import = value ?? new(); // "import": null in a hand-edited file
    }

    ImportSettings _import = new();

    /// <summary>What the .json beside each note holds (<see cref="Pipeline.MeetingFile"/>).</summary>
    public MeetingFileSettings MeetingFile
    {
        get => _meetingFile;
        set => _meetingFile = value ?? new(); // "meetingFile": null in a hand-edited file
    }

    MeetingFileSettings _meetingFile = new();
    public BackendSettings Backend { get; set; } = new();
    public SummarySettings Summary { get; set; } = new();
    public SpeakerSettings Speakers { get; set; } = new();

    /// <summary>Words and names the transcription gets wrong, and how to write them (Settings → Vocabulary).</summary>
    public List<VocabularyEntry> Vocabulary
    {
        get => _vocabulary;
        set => _vocabulary = value ?? []; // "vocabulary": null in a hand-edited file
    }

    List<VocabularyEntry> _vocabulary = [];

    public string ResolvedRecordingsFolder =>
        string.IsNullOrWhiteSpace(RecordingsFolder) ? AppPaths.DefaultRecordingsFolder : RecordingsFolder;

    /// <summary>Human-readable reasons processing cannot run yet. Empty = good to go.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(DisplayName)) problems.Add("displayName is empty");
        switch (Backend.Type)
        {
            case BackendTypes.Scriberr:
                if (!IsHttpUrl(Backend.Scriberr.BaseUrl)) problems.Add("backend.scriberr.baseUrl is not an http(s) URL");
                break;
            case BackendTypes.OpenAi:
                if (!IsHttpUrl(Backend.OpenAi.TranscriptionBaseUrl))
                    problems.Add("backend.openAi.transcriptionBaseUrl is not an http(s) URL");
                if (!IsHttpUrl(Summary.BaseUrl)) problems.Add("summary.baseUrl is not an http(s) URL");
                if (string.IsNullOrWhiteSpace(Summary.Model)) problems.Add("summary.model is empty");
                break;
            default:
                problems.Add($"backend.type must be '{BackendTypes.Scriberr}' or '{BackendTypes.OpenAi}'");
                break;
        }
        if (Speakers.Enabled && !IsHttpUrl(Speakers.ResolvedBaseUrl(this)))
            problems.Add("speakers.baseUrl is not an http(s) URL (speaker names are on)");
        return problems;
    }

    internal static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);
}

public sealed class DetectionSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>The mic must stay in use this long before recording starts (filters blips).</summary>
    public double StartAfterSeconds { get; set; } = 3;

    /// <summary>Recording stops once the mic has been released this long (rides out device switches).</summary>
    public double StopAfterSeconds { get; set; } = 15;

    /// <summary>Automatic recordings shorter than this are discarded (Teams' pre-join mic preview).</summary>
    public double MinAutoRecordingSeconds { get; set; } = 60;

    /// <summary>
    /// The call window's title on the screen before joining a meeting. Seen right after the mic was released
    /// and taken again, it means the next meeting has begun, so the recording splits (back-to-back meetings).
    /// Teams in English says "Meeting join"; blank turns this off (a change of meeting name still splits).
    /// </summary>
    public string? JoinScreenTitleContains { get; set; } = "Meeting join";

    public List<WatchRule> Sources { get; set; } = WatchRule.Defaults();
}

/// <summary>
/// One thing that counts as "a call is happening": an app holding the microphone, optionally
/// confirmed by a window title (needed for browsers, which use the mic for many sites).
/// </summary>
public sealed class WatchRule
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Case-insensitive substring of the app's key under
    /// HKCU\...\CapabilityAccessManager\ConsentStore\microphone — a package family name for
    /// Store apps (MSTeams_8wekyb3d8bbwe) or a '#'-separated exe path under NonPackaged.
    /// </summary>
    public string AppKeyContains { get; set; } = "";

    /// <summary>When set, a visible window of this process (no .exe) must have a title containing <see cref="WindowTitleContains"/>.</summary>
    public string? WindowProcess { get; set; }

    public string? WindowTitleContains { get; set; }

    /// <summary>Alternatively (or as well), a case-insensitive regular expression the window title must match.</summary>
    public string? WindowTitleMatches { get; set; }

    /// <summary>
    /// Processes that carry the call's audio. Used to follow the call onto whatever microphone and
    /// headset it actually uses (Teams can pick devices that are not the Windows defaults).
    /// </summary>
    public List<string> AudioProcesses { get; set; } = [];

    public bool NeedsWindow => !string.IsNullOrWhiteSpace(WindowProcess) || !string.IsNullOrWhiteSpace(WindowTitleContains) ||
                               !string.IsNullOrWhiteSpace(WindowTitleMatches);

    /// <summary>Google Meet's tab while joining and in the call: "Meet - abc-defg-hij" (a hyphen or a dash).</summary>
    const string MeetTab = @"(^|\s)Meet\s[-–—]\s|Google Meet";

    /// <summary>Zoom's web client: a tab called "Zoom" (seen on its join page, 2026-10-21), or a page "… - Zoom".</summary>
    const string ZoomTab = @"^Zoom\b|\s[-–—|]\sZoom\b";

    /// <summary>
    /// The built-in rules. Browsers share one mic entry, so each browser rule also needs its app's window; desktop
    /// apps own theirs. Zoom and GoTo were written from their install paths, not yet seen on a real call; Zoom in a
    /// browser from its join page's title (a Zoom call in Edge went unrecorded, 2026-10-21).
    /// </summary>
    public static List<WatchRule> Defaults() =>
    [
        new() { Name = "Teams (Edge)", AppKeyContains = "#msedge.exe", WindowProcess = "msedge", WindowTitleContains = "Microsoft Teams", AudioProcesses = ["msedge"] },
        new() { Name = "Teams (Chrome)", AppKeyContains = "#chrome.exe", WindowProcess = "chrome", WindowTitleContains = "Microsoft Teams", AudioProcesses = ["chrome"] },
        new() { Name = "Teams (app)", AppKeyContains = "MSTeams_", AudioProcesses = ["ms-teams", "msedgewebview2"] },
        new() { Name = "Teams (classic)", AppKeyContains = "#Teams.exe", AudioProcesses = ["Teams"] },
        new() { Name = "Google Meet (Edge)", AppKeyContains = "#msedge.exe", WindowProcess = "msedge", WindowTitleMatches = MeetTab, AudioProcesses = ["msedge"] },
        new() { Name = "Google Meet (Chrome)", AppKeyContains = "#chrome.exe", WindowProcess = "chrome", WindowTitleMatches = MeetTab, AudioProcesses = ["chrome"] },
        new() { Name = "Zoom (Edge)", AppKeyContains = "#msedge.exe", WindowProcess = "msedge", WindowTitleMatches = ZoomTab, AudioProcesses = ["msedge"] },
        new() { Name = "Zoom (Chrome)", AppKeyContains = "#chrome.exe", WindowProcess = "chrome", WindowTitleMatches = ZoomTab, AudioProcesses = ["chrome"] },
        new() { Name = "Zoom", AppKeyContains = "#Zoom.exe", AudioProcesses = ["Zoom"] },
        new() { Name = "GoTo Meeting", AppKeyContains = "#GoTo.exe", AudioProcesses = ["GoTo"] },
        new() { Name = "GoTo Meeting (classic)", AppKeyContains = "#g2m", AudioProcesses = ["g2mcomm", "g2mstart", "g2mui"] },
    ];
}

public sealed class AudioSettings
{
    /// <summary>Substring of the microphone's name; null = Windows default (follows changes).</summary>
    public string? MicDeviceContains { get; set; }

    /// <summary>Substring of the speakers/headset name to capture; null = Windows default (follows changes).</summary>
    public string? OutputDeviceContains { get; set; }

    /// <summary>
    /// Play inaudible silence on the output device while recording. Windows stops delivering loopback
    /// audio when nothing is playing; this keeps the "everyone else" track continuous.
    /// </summary>
    public bool KeepAliveOutput { get; set; } = true;

    /// <summary>
    /// Record the microphone the call app is actually capturing from, and the output paired with it
    /// (same device name), instead of the Windows defaults. Re-checked every few seconds during a call.
    /// Ignored when devices are pinned with <see cref="MicDeviceContains"/>/<see cref="OutputDeviceContains"/>.
    /// </summary>
    public bool FollowCallDevices { get; set; } = true;
}

public sealed class ImportSettings
{
    /// <summary>
    /// A folder Lemur watches for recordings to make notes from (say one a phone's voice memos sync to); null = none.
    /// Each file moves to "Imported" inside it once its recording is made (<see cref="Pipeline.ImportFolder"/>).
    /// </summary>
    public string? Folder { get; set; }
}

public sealed class NoteMp3Settings
{
    /// <summary>
    /// Delete each note's MP3 with the working audio, <see cref="AppSettings.KeepAudioDays"/> after its notes were made, unless
    /// the meeting is marked Keep recording. Off (the default): the MP3 is part of the note, kept until the user deletes the
    /// note. The note and its transcript stay either way; the page loses its player.
    /// </summary>
    public bool DeleteWithAudio { get; set; }
}

public sealed class MeetingFileSettings
{
    /// <summary>
    /// Put the transcript in the meeting file. Off when whatever reads the .json may hand it to a model on the internet:
    /// the file then says it was left out. The note's .md and page keep theirs.
    /// </summary>
    public bool IncludeTranscript { get; set; } = true;
}

public static class BackendTypes
{
    public const string Scriberr = "scriberr";
    public const string OpenAi = "openai";
}

public sealed class BackendSettings
{
    /// <summary>
    /// "openai" (any OpenAI-compatible gateway — the shareable default) or "scriberr" (a personal Scriberr,
    /// which is single-user). Existing settings files name theirs, so this only decides new installs.
    /// </summary>
    public string Type { get; set; } = BackendTypes.OpenAi;

    public ScriberrSettings Scriberr { get; set; } = new();
    public OpenAiSettings OpenAi { get; set; } = new();
}

public sealed class ScriberrSettings
{
    /// <summary>e.g. https://scriberr.example.com — the site root, not /api/v1.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Name of the secret holding the Scriberr API key (sent as X-API-Key).</summary>
    public string ApiKeySecret { get; set; } = "scriberr-api-key";

    /// <summary>Transcription profile to use; empty = the profile marked default in Scriberr.</summary>
    public string ProfileName { get; set; } = "";

    /// <summary>LLM Scriberr should summarize with; empty = Scriberr's default summary model.</summary>
    public string SummaryModel { get; set; } = "";

    /// <summary>Optional Scriberr summary template to attach the summary to (its prompt is not used).</summary>
    public string? SummaryTemplateName { get; set; }
}

/// <summary>A word or name as it should be written, and what the transcription writes instead.</summary>
public sealed class VocabularyEntry
{
    /// <summary>How it should be written: "Zentavo".</summary>
    public string Term
    {
        get => _term;
        set => _term = value ?? "";
    }

    /// <summary>What the transcription writes instead: "Santavo", "Zen Tavo". Empty: the summary model is only told the spelling.</summary>
    public List<string> HeardAs
    {
        get => _heardAs;
        set => _heardAs = value ?? []; // "heardAs": null in a hand-edited file
    }

    string _term = "";
    List<string> _heardAs = [];
}

public sealed class OpenAiSettings
{
    /// <summary>OpenAI-style base URL including /v1, e.g. https://gateway.example.com/v1.</summary>
    public string TranscriptionBaseUrl { get; set; } = "";

    /// <summary>Secret name for the transcription key; null = the endpoint needs no key.</summary>
    public string? TranscriptionApiKeySecret { get; set; } = "transcription-api-key";

    public string TranscriptionModel { get; set; } = "whisper-1";
    public string ResponseFormat { get; set; } = "diarized_json";
    public string? Language { get; set; } = "en";

    /// <summary>Extra multipart fields some providers require (e.g. chunking_strategy=auto).</summary>
    public Dictionary<string, string> ExtraFields { get; set; } = new();
}

/// <summary>Summary LLM for the "openai" backend. (The Scriberr backend summarizes through Scriberr.)</summary>
public sealed class SummarySettings
{
    /// <summary>OpenAI-style base URL including /v1.</summary>
    public string BaseUrl { get; set; } = "";

    public string? ApiKeySecret { get; set; } = "summary-api-key";
    public string Model { get; set; } = "";

    /// <summary>
    /// Generous on purpose: reasoning models spend tokens thinking before they answer (2026-10-14: QFN used up to
    /// 13.5k on the introductions check of a 17-minute meeting). 16k is also what hosted models like gpt-4o allow;
    /// a local reasoning model can take 32768.
    /// </summary>
    public int MaxTokens { get; set; } = 16384;

    /// <summary>Optional file whose contents replace the built-in summary prompt.</summary>
    public string? PromptFile { get; set; }
}

/// <summary>
/// Naming the other people in a meeting: by voice (a voiceprint model behind an OpenAI-style
/// /embeddings endpoint, usually LiteLLM) and by what they say ("hi, it's Dana").
/// See docs/2026-10-13-speaker-names-design.md.
/// </summary>
public sealed class SpeakerSettings
{
    /// <summary>Recognise voices. Off for new installs: it keeps voiceprints of other people (design D4).</summary>
    public bool Enabled { get; set; }

    /// <summary>OpenAI-style base URL including /v1; empty = the summary base URL (same LiteLLM).</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Secret holding the key; when missing and the host matches the summary's, the summary key is used.</summary>
    public string? ApiKeySecret { get; set; } = "voiceprint-api-key";

    public string Model { get; set; } = "speaker-embed-local";

    /// <summary>Null = %APPDATA%\Lemur\people. Point several PCs at one OneDrive folder to share people.</summary>
    public string? PeopleFolder { get; set; }

    /// <summary>1 = Careful (fewest wrong names) … 5 = Eager (most names).</summary>
    public int Level { get; set; } = 1;

    /// <summary>Add to your own voiceprint after each call, from the segments the track comparison says are you.</summary>
    public bool LearnMyVoice { get; set; } = true;

    /// <summary>Ask "did they agree?" before remembering someone's voice (design D3).</summary>
    public bool AskConsent { get; set; } = true;

    /// <summary>Name speakers who introduce themselves. Needs only the summary model, not the voiceprint service.</summary>
    public bool NameFromIntroductions { get; set; } = true;

    /// <summary>Matching numbers for a model that isn't built in (from `llmmr voices benchmark`); null = built-in.</summary>
    public Speakers.ModelThresholds? Thresholds { get; set; }

    public string ResolvedBaseUrl(AppSettings all) =>
        string.IsNullOrWhiteSpace(BaseUrl) ? all.Summary.BaseUrl : BaseUrl;

    public string ResolvedPeopleFolder =>
        string.IsNullOrWhiteSpace(PeopleFolder) ? AppPaths.DefaultPeopleFolder : PeopleFolder;
}
