using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Lemur.Config;
using Lemur.Transcripts;

namespace Lemur.Backends;

/// <summary>
/// Any OpenAI-compatible gateway: POST /audio/transcriptions with response_format=diarized_json for
/// the transcript, POST /chat/completions for the summary. Works against LiteLLM (per-user keys),
/// WhisperLiveKit, or a hosted provider — the shareable, multi-user path.
/// </summary>
public sealed class OpenAiBackend : IMeetingBackend
{
    readonly OpenAiSettings _stt;
    readonly SummarySettings _summary;
    readonly string? _hint;
    readonly HttpClient _sttHttp;
    readonly HttpClient _llmHttp;

    /// <param name="hint">Words the transcription should expect (the vocabulary), sent as the prompt Whisper reads.</param>
    public OpenAiBackend(OpenAiSettings stt, string? sttKey, SummarySettings summary, string? summaryKey,
        HttpMessageHandler? handler = null, string? hint = null)
    {
        _stt = stt;
        _summary = summary;
        _hint = hint;
        _sttHttp = Http.Create(handler);
        _llmHttp = Http.Create(handler);
        if (!string.IsNullOrWhiteSpace(sttKey))
            _sttHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", sttKey.Trim());
        if (!string.IsNullOrWhiteSpace(summaryKey))
            _llmHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", summaryKey.Trim());
    }

    public string Kind => BackendTypes.OpenAi;

    public async Task<TranscriptResult> TranscribeAsync(string audioPath, string title, TimeSpan duration, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        form.Add(Http.FileContent(audioPath, "audio/wav"), "file", Path.GetFileName(audioPath));
        form.Add(new StringContent(_stt.TranscriptionModel), "model");
        form.Add(new StringContent(_stt.ResponseFormat), "response_format");
        if (!string.IsNullOrWhiteSpace(_stt.Language)) form.Add(new StringContent(_stt.Language), "language");
        // The vocabulary as Whisper's prompt, unless extraFields has one: an empty "prompt" there sends none at all.
        if (_hint is not null && !_stt.ExtraFields.ContainsKey("prompt")) form.Add(new StringContent(_hint), "prompt");
        foreach (var (key, value) in _stt.ExtraFields)
            if (!(key == "prompt" && value.Length == 0)) form.Add(new StringContent(value), key);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(20) + duration);
        using var response = await _sttHttp.PostAsync($"{_stt.TranscriptionBaseUrl.TrimEnd('/')}/audio/transcriptions", form, cts.Token);
        using var doc = await Http.ReadJson(response, "transcription", cts.Token);
        var segments = ReadSegments(doc.RootElement);
        if (segments.Count == 0 && doc.RootElement.TryGetProperty("text", out var text) && !string.IsNullOrWhiteSpace(text.GetString()))
            segments = [new TranscriptSegment(0, duration.TotalSeconds, "A", text.GetString()!)]; // engine without diarization
        return new TranscriptResult(segments, null, null);
    }

    public Task NameSpeakersAsync(TranscriptResult transcript, IReadOnlyDictionary<string, string> engineToName, CancellationToken ct) =>
        Task.CompletedTask; // nothing server-side to rename

    public async Task<string> SummarizeAsync(TranscriptResult transcript, string instructions, string labelledTranscript, CancellationToken ct)
    {
        var body = new
        {
            model = _summary.Model,
            max_tokens = _summary.MaxTokens,
            messages = new object[]
            {
                new { role = "system", content = instructions },
                new { role = "user", content = "Transcript:\n" + labelledTranscript },
            },
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(20));
        using var response = await _llmHttp.PostAsync($"{_summary.BaseUrl.TrimEnd('/')}/chat/completions", Http.JsonBody(body), cts.Token);
        using var doc = await Http.ReadJson(response, "summary", cts.Token);
        var content = SummaryText.Clean(doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "");
        if (content.Length == 0)
            throw new BackendException("summary: model returned no text (a reasoning model may need a larger summary.maxTokens)");
        return content;
    }

    /// <summary>
    /// Reads {segments:[{start,end,speaker,text}]}; times may be numbers or "h:mm:ss.fff" strings. Word times, when the
    /// engine sends them (Scriberr/WhisperX word_segments, OpenAI's words), go with their segments: they tell, word by
    /// word, which track a line was spoken on.
    /// </summary>
    internal static IReadOnlyList<TranscriptSegment> ReadSegments(JsonElement container)
    {
        var list = new List<TranscriptSegment>();
        if (!container.TryGetProperty("segments", out var segments) || segments.ValueKind != JsonValueKind.Array) return list;
        foreach (var s in segments.EnumerateArray())
        {
            var text = s.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(text)) continue;
            list.Add(new TranscriptSegment(Time(s, "start"), Time(s, "end"), Speaker(s) ?? "?", text.Trim()));
        }
        return TranscriptWords.Attach(list, ReadWords(container));
    }

    /// <summary>Timed words, in order; a word without both times (WhisperX leaves some numbers unaligned) is left out.</summary>
    static List<TranscriptWord> ReadWords(JsonElement container)
    {
        var words = new List<TranscriptWord>();
        JsonElement array = default;
        bool found = container.TryGetProperty("word_segments", out array) && array.ValueKind == JsonValueKind.Array ||
                     container.TryGetProperty("words", out array) && array.ValueKind == JsonValueKind.Array;
        if (!found) return words;
        foreach (var w in array.EnumerateArray())
        {
            if (w.ValueKind != JsonValueKind.Object || !Timed(w, "start") || !Timed(w, "end")) continue;
            var text = (w.TryGetProperty("word", out var x) || w.TryGetProperty("text", out x)) && x.ValueKind == JsonValueKind.String
                ? x.GetString() ?? ""
                : "";
            if (string.IsNullOrWhiteSpace(text)) continue;
            words.Add(new TranscriptWord(Time(w, "start"), Time(w, "end"), text.Trim(), Speaker(w)));
        }
        return words;
    }

    static bool Timed(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.Number or JsonValueKind.String;

    static string? Speaker(JsonElement e) =>
        e.TryGetProperty("speaker", out var sp) && sp.ValueKind != JsonValueKind.Null
            ? sp.ValueKind == JsonValueKind.String ? sp.GetString() : sp.GetRawText()
            : null;

    static double Time(JsonElement s, string name)
    {
        if (!s.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String)
        {
            var str = v.GetString() ?? "";
            if (double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            if (TimeSpan.TryParse(str, CultureInfo.InvariantCulture, out var ts)) return ts.TotalSeconds;
        }
        return 0;
    }

    public void Dispose()
    {
        _sttHttp.Dispose();
        _llmHttp.Dispose();
    }
}

static class SummaryText
{
    /// <summary>Drops a leaked &lt;think&gt; block and surrounding whitespace; a reply cut off mid-thought is empty.</summary>
    public static string Clean(string text)
    {
        var s = text;
        int end = s.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (end >= 0) s = s[(end + "</think>".Length)..];
        else if (s.TrimStart().StartsWith("<think>", StringComparison.OrdinalIgnoreCase)) return "";
        return s.Trim();
    }
}
