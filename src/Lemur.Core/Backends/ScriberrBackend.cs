using System.Net;
using System.Text.Json;
using Lemur.Config;
using Lemur.Transcripts;

namespace Lemur.Backends;

/// <summary>
/// Scriberr (github.com/rishikanthc/Scriberr, v1.2.0 API). Upload → start with a transcription
/// profile's parameters → poll → fetch segments. Summaries go through Scriberr's /summarize so they
/// are also saved on the job and visible in its web UI. Auth: X-API-Key.
/// Scriberr is single-user — this backend is for a personal instance.
/// </summary>
public sealed class ScriberrBackend : IMeetingBackend, IResumableTranscription
{
    readonly ScriberrSettings _settings;
    readonly string? _hint;
    readonly HttpClient _http;
    readonly string _api;

    /// <param name="hint">Words the transcription should expect (the vocabulary): a Whisper profile gets them as its
    /// initial_prompt. Parakeet and Canary take no hints (Scriberr v1.2.0), so for them Lemur fixes the words afterwards.</param>
    public ScriberrBackend(ScriberrSettings settings, string apiKey, HttpMessageHandler? handler = null, string? hint = null)
    {
        _settings = settings;
        _hint = hint;
        _api = settings.BaseUrl.TrimEnd('/') + "/api/v1";
        _http = Http.Create(handler);
        _http.DefaultRequestHeaders.Add("X-API-Key", apiKey);
    }

    public string Kind => BackendTypes.Scriberr;
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(3);

    public Task<TranscriptResult> TranscribeAsync(string audioPath, string title, TimeSpan duration, CancellationToken ct) =>
        TranscribeAsync(audioPath, title, duration, null, null, ct);

    public async Task<TranscriptResult> TranscribeAsync(string audioPath, string title, TimeSpan duration, string? job,
        Action<TranscriptionJob>? started, CancellationToken ct)
    {
        string parameters = WithHint(await ProfileParametersJson(ct), _hint);

        // A retry: the job may still be queued or running, may have finished since, or may have failed.
        string? status = job is null ? null : (await Status(job, ct))?.Status;
        string jobId;
        if (job is not null && status is not null)
        {
            jobId = job;
        }
        else
        {
            using var form = new MultipartFormDataContent();
            form.Add(Http.FileContent(audioPath, "audio/wav"), "audio", Path.GetFileName(audioPath));
            form.Add(new StringContent(title), "title");
            using var upload = await _http.PostAsync($"{_api}/transcription/upload", form, ct);
            using var doc = await Http.ReadJson(upload, "Scriberr upload", ct);
            jobId = doc.RootElement.GetProperty("id").GetString()
                    ?? throw new BackendException("Scriberr upload: no job id in response");
            status = "uploaded";
            started?.Invoke(new TranscriptionJob(jobId, WebUrl(jobId)));
        }

        if (status is "uploaded" or "failed") // Scriberr starts a failed job again on request
        {
            using var start = await _http.PostAsync($"{_api}/transcription/{jobId}/start", Http.JsonBody(parameters), ct);
            await Http.EnsureOk(start, "Scriberr start", ct);
        }

        // Generous: the GPU may be busy with someone else's job, and models load per job.
        var waitFrom = DateTimeOffset.UtcNow;
        var deadline = waitFrom + TimeSpan.FromMinutes(20) + duration;
        while (true)
        {
            var now = await Status(jobId, ct) ?? throw new BackendException($"Scriberr job {jobId} disappeared");
            if (now.Status == "completed") break;
            if (now.Status is "failed" or "error")
                throw new BackendException($"Scriberr job {jobId} failed: {Http.SafeSnippet(now.Error, 400)}");
            if (DateTimeOffset.UtcNow > deadline)
                throw new BackendException($"Scriberr job {jobId} still '{now.Status}' after {(int)(DateTimeOffset.UtcNow - waitFrom).TotalMinutes} min " +
                                           "(the next try picks it up again)");
            await Task.Delay(PollInterval, ct);
        }

        using var transcript = await _http.GetAsync($"{_api}/transcription/{jobId}/transcript", ct);
        using var tdoc = await Http.ReadJson(transcript, "Scriberr transcript", ct);
        var segments = ParseSegments(tdoc.RootElement);
        return new TranscriptResult(segments, jobId, WebUrl(jobId));
    }

    string WebUrl(string jobId) => $"{_settings.BaseUrl.TrimEnd('/')}/audio/{jobId}";

    /// <summary>
    /// A Whisper profile (model_family "whisper", Scriberr's default) with no initial_prompt of its own gets the vocabulary's
    /// words as one. The parameters carry hf_token: they are only passed on, never logged.
    /// </summary>
    internal static string WithHint(string parameters, string? hint)
    {
        if (hint is null) return parameters;
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(parameters) is not System.Text.Json.Nodes.JsonObject p) return parameters;
            var family = p["model_family"]?.GetValue<string>() ?? "whisper";
            if (!family.Equals("whisper", StringComparison.OrdinalIgnoreCase)) return parameters;
            if (p["initial_prompt"]?.GetValue<string>() is { Length: > 0 }) return parameters; // the profile's own wins
            p["initial_prompt"] = hint;
            return p.ToJsonString();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            return parameters; // not what we expected: send it as it is
        }
    }

    /// <summary>A job's state, or null if Scriberr no longer has it (deleted).</summary>
    async Task<(string? Status, string? Error)?> Status(string jobId, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"{_api}/transcription/{jobId}/status", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        using var doc = await Http.ReadJson(response, "Scriberr status", ct);
        // Only the status and error text: the job object also carries its parameters, hf_token included.
        var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
        var error = doc.RootElement.TryGetProperty("error_message", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        return (status, error);
    }

    public async Task NameSpeakersAsync(TranscriptResult transcript, IReadOnlyDictionary<string, string> engineToName, CancellationToken ct)
    {
        if (transcript.JobId is null || engineToName.Count == 0) return;
        var body = new
        {
            mappings = engineToName.Select(kv => new { original_speaker = kv.Key, custom_name = kv.Value }).ToArray(),
        };
        using var response = await _http.PostAsync($"{_api}/transcription/{transcript.JobId}/speakers", Http.JsonBody(body), ct);
        await Http.EnsureOk(response, "Scriberr speaker names", ct);
    }

    public async Task<string> SummarizeAsync(TranscriptResult transcript, string instructions, string labelledTranscript, CancellationToken ct)
    {
        if (transcript.JobId is null) throw new BackendException("Scriberr summarize: transcript has no job id");
        string model = string.IsNullOrWhiteSpace(_settings.SummaryModel) ? await DefaultSummaryModel(ct) : _settings.SummaryModel;
        string? templateId = await TemplateId(ct);
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["content"] = $"{instructions}\n\nTranscript:\n{labelledTranscript}",
            ["transcription_id"] = transcript.JobId,
        };
        if (templateId is not null) body["template_id"] = templateId;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(20));
        // Streams plain text and saves the summary on the job when the stream ends.
        using var response = await _http.PostAsync($"{_api}/summarize/", Http.JsonBody(body), cts.Token);
        await Http.EnsureOk(response, "Scriberr summarize", cts.Token);
        var text = SummaryText.Clean(await response.Content.ReadAsStringAsync(cts.Token));
        // Scriberr stops waiting for the model after 5 minutes and still answers 200, with whatever had arrived.
        if (text.Length == 0)
            throw new BackendException("Scriberr summarize: no summary came back (Scriberr gives the model 5 minutes; it will be retried)");
        return text;
    }

    async Task<string> ProfileParametersJson(CancellationToken ct)
    {
        using var response = await _http.GetAsync($"{_api}/profiles/", ct);
        using var doc = await Http.ReadJson(response, "Scriberr profiles", ct);
        JsonElement? chosen = null;
        foreach (var p in doc.RootElement.EnumerateArray())
        {
            var name = p.TryGetProperty("name", out var n) ? n.GetString() : null;
            bool isDefault = p.TryGetProperty("is_default", out var d) && d.ValueKind == JsonValueKind.True;
            if (!string.IsNullOrWhiteSpace(_settings.ProfileName))
            {
                if (string.Equals(name, _settings.ProfileName, StringComparison.OrdinalIgnoreCase)) { chosen = p; break; }
            }
            else if (isDefault) { chosen = p; break; }
        }
        if (chosen is null)
            throw new BackendException(string.IsNullOrWhiteSpace(_settings.ProfileName)
                ? "Scriberr has no default transcription profile"
                : $"Scriberr has no transcription profile named '{_settings.ProfileName}'");
        return chosen.Value.GetProperty("parameters").GetRawText();
    }

    async Task<string> DefaultSummaryModel(CancellationToken ct)
    {
        using var response = await _http.GetAsync($"{_api}/summaries/settings", ct);
        using var doc = await Http.ReadJson(response, "Scriberr summary settings", ct);
        var model = doc.RootElement.TryGetProperty("default_model", out var m) ? m.GetString() : null;
        return string.IsNullOrWhiteSpace(model)
            ? throw new BackendException("Scriberr has no default summary model; set backend.scriberr.summaryModel")
            : model;
    }

    async Task<string?> TemplateId(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_settings.SummaryTemplateName)) return null;
        using var response = await _http.GetAsync($"{_api}/summaries/", ct);
        using var doc = await Http.ReadJson(response, "Scriberr summary templates", ct);
        foreach (var t in doc.RootElement.EnumerateArray())
            if (string.Equals(t.GetProperty("name").GetString(), _settings.SummaryTemplateName, StringComparison.OrdinalIgnoreCase))
                return t.GetProperty("id").GetString();
        return null;
    }

    /// <summary>The transcript arrives as {transcript: {...segments}} — sometimes with that inner object as a JSON string.</summary>
    internal static IReadOnlyList<TranscriptSegment> ParseSegments(JsonElement root)
    {
        JsonElement container = root;
        if (root.TryGetProperty("transcript", out var t))
        {
            if (t.ValueKind == JsonValueKind.String)
            {
                using var inner = JsonDocument.Parse(t.GetString() ?? "{}");
                return ParseSegments(inner.RootElement.Clone());
            }
            if (t.ValueKind == JsonValueKind.Object) container = t;
        }
        return OpenAiBackend.ReadSegments(container);
    }

    public void Dispose() => _http.Dispose();
}
