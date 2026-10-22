using System.Text.Json;
using System.Text.Json.Serialization;
using Lemur.Speakers;

namespace Lemur.Config;

/// <summary>
/// The portable part of the settings (design 6.3): server URLs and model names, never a key, a device
/// or a folder. An admin hands one to a new user; you carry one to your second PC.
/// </summary>
public sealed class SetupFile
{
    public const int CurrentFormat = 1;
    public const string Extension = ".lemursetup";

    /// <summary>Setup files made by Teams Recorder (Lemur before 1.0); they import the same way.</summary>
    public const string LegacyExtension = ".trecsetup";

    public int Format { get; set; } = CurrentFormat;

    /// <summary>Shown when importing: "Example Org".</summary>
    public string Name { get; set; } = "";

    /// <summary>One line telling the user where to get their key.</summary>
    public string? KeyHelp { get; set; }

    public SetupBackend Backend { get; set; } = new();
    public SetupSummary Summary { get; set; } = new();
    public SetupSpeakers Speakers { get; set; } = new();

    public sealed class SetupBackend
    {
        public string Type { get; set; } = BackendTypes.OpenAi;
        public string? ScriberrBaseUrl { get; set; }
        public string? ScriberrProfileName { get; set; }
        public string? ScriberrSummaryModel { get; set; }
        public string? ScriberrSummaryTemplateName { get; set; }
        public string? TranscriptionBaseUrl { get; set; }
        public string? TranscriptionModel { get; set; }
        public string? ResponseFormat { get; set; }
        public string? Language { get; set; }
        public Dictionary<string, string>? ExtraFields { get; set; }
    }

    public sealed class SetupSummary
    {
        public string? BaseUrl { get; set; }
        public string? Model { get; set; }
        public int? MaxTokens { get; set; }
    }

    public sealed class SetupSpeakers
    {
        public string? BaseUrl { get; set; }
        public string? Model { get; set; }
        public ModelThresholds? Thresholds { get; set; }
    }

    public static SetupFile From(AppSettings s, string name, string? keyHelp) => new()
    {
        Name = name,
        KeyHelp = keyHelp,
        Backend = new SetupBackend
        {
            Type = s.Backend.Type,
            ScriberrBaseUrl = Blank(s.Backend.Scriberr.BaseUrl),
            ScriberrProfileName = Blank(s.Backend.Scriberr.ProfileName),
            ScriberrSummaryModel = Blank(s.Backend.Scriberr.SummaryModel),
            ScriberrSummaryTemplateName = Blank(s.Backend.Scriberr.SummaryTemplateName),
            TranscriptionBaseUrl = Blank(s.Backend.OpenAi.TranscriptionBaseUrl),
            TranscriptionModel = Blank(s.Backend.OpenAi.TranscriptionModel),
            ResponseFormat = Blank(s.Backend.OpenAi.ResponseFormat),
            Language = Blank(s.Backend.OpenAi.Language),
            ExtraFields = s.Backend.OpenAi.ExtraFields.Count == 0 ? null : new(s.Backend.OpenAi.ExtraFields),
        },
        Summary = new SetupSummary { BaseUrl = Blank(s.Summary.BaseUrl), Model = Blank(s.Summary.Model), MaxTokens = s.Summary.MaxTokens },
        Speakers = new SetupSpeakers { BaseUrl = Blank(s.Speakers.BaseUrl), Model = Blank(s.Speakers.Model), Thresholds = s.Speakers.Thresholds },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static SetupFile Parse(string json)
    {
        var file = JsonSerializer.Deserialize<SetupFile>(json, Options) ?? throw new InvalidDataException("empty setup file");
        if (file.Format > CurrentFormat) throw new InvalidDataException($"setup file format {file.Format} is newer than this app understands");
        foreach (var url in new[] { file.Backend.ScriberrBaseUrl, file.Backend.TranscriptionBaseUrl, file.Summary.BaseUrl, file.Speakers.BaseUrl })
            if (url is not null && !AppSettings.IsHttpUrl(url))
                throw new InvalidDataException($"setup file has a URL that isn't http(s): {url}");
        return file;
    }

    /// <summary>What importing would change, as "setting: old → new" lines; empty when nothing changes.</summary>
    public IReadOnlyList<string> Preview(AppSettings current)
    {
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(current, Json.Options), Json.Options)!;
        ApplyTo(copy);
        var changes = new List<string>();
        void Diff(string what, object? before, object? after)
        {
            var a = Show(before);
            var b = Show(after);
            if (a != b) changes.Add($"{what}: {a} → {b}");
        }
        Diff("backend", current.Backend.Type, copy.Backend.Type);
        Diff("Scriberr URL", current.Backend.Scriberr.BaseUrl, copy.Backend.Scriberr.BaseUrl);
        Diff("Scriberr profile", current.Backend.Scriberr.ProfileName, copy.Backend.Scriberr.ProfileName);
        Diff("Scriberr summary model", current.Backend.Scriberr.SummaryModel, copy.Backend.Scriberr.SummaryModel);
        Diff("Scriberr template", current.Backend.Scriberr.SummaryTemplateName, copy.Backend.Scriberr.SummaryTemplateName);
        Diff("transcription URL", current.Backend.OpenAi.TranscriptionBaseUrl, copy.Backend.OpenAi.TranscriptionBaseUrl);
        Diff("transcription model", current.Backend.OpenAi.TranscriptionModel, copy.Backend.OpenAi.TranscriptionModel);
        Diff("summary URL", current.Summary.BaseUrl, copy.Summary.BaseUrl);
        Diff("summary model", current.Summary.Model, copy.Summary.Model);
        Diff("voiceprint URL", current.Speakers.BaseUrl, copy.Speakers.BaseUrl);
        Diff("voiceprint model", current.Speakers.Model, copy.Speakers.Model);
        Diff("voiceprint numbers", current.Speakers.Thresholds?.Name, copy.Speakers.Thresholds?.Name);
        return changes;
    }

    /// <summary>Copies everything the file sets; leaves the rest (devices, folders, name, keys) alone.</summary>
    public void ApplyTo(AppSettings s)
    {
        s.Backend.Type = Backend.Type;
        s.Backend.Scriberr.BaseUrl = Backend.ScriberrBaseUrl ?? s.Backend.Scriberr.BaseUrl;
        s.Backend.Scriberr.ProfileName = Backend.ScriberrProfileName ?? s.Backend.Scriberr.ProfileName;
        s.Backend.Scriberr.SummaryModel = Backend.ScriberrSummaryModel ?? s.Backend.Scriberr.SummaryModel;
        s.Backend.Scriberr.SummaryTemplateName = Backend.ScriberrSummaryTemplateName ?? s.Backend.Scriberr.SummaryTemplateName;
        s.Backend.OpenAi.TranscriptionBaseUrl = Backend.TranscriptionBaseUrl ?? s.Backend.OpenAi.TranscriptionBaseUrl;
        s.Backend.OpenAi.TranscriptionModel = Backend.TranscriptionModel ?? s.Backend.OpenAi.TranscriptionModel;
        s.Backend.OpenAi.ResponseFormat = Backend.ResponseFormat ?? s.Backend.OpenAi.ResponseFormat;
        s.Backend.OpenAi.Language = Backend.Language ?? s.Backend.OpenAi.Language;
        if (Backend.ExtraFields is not null) s.Backend.OpenAi.ExtraFields = new(Backend.ExtraFields);
        s.Summary.BaseUrl = Summary.BaseUrl ?? s.Summary.BaseUrl;
        s.Summary.Model = Summary.Model ?? s.Summary.Model;
        if (Summary.MaxTokens is { } max) s.Summary.MaxTokens = max;
        s.Speakers.BaseUrl = Speakers.BaseUrl ?? s.Speakers.BaseUrl;
        s.Speakers.Model = Speakers.Model ?? s.Speakers.Model;
        if (Speakers.Thresholds is not null) s.Speakers.Thresholds = Speakers.Thresholds;
    }

    static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    static string Show(object? v) => v is null || (v is string s && s.Length == 0) ? "(none)" : v.ToString()!;

    static readonly JsonSerializerOptions Options = new(Json.Options) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
}
