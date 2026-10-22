using System.Net.Http.Headers;
using System.Text.Json;
using Lemur.Backends;
using Lemur.Config;

namespace Lemur.Speakers;

/// <summary>Turns clips into voiceprints. One vector per clip, in order, unit length.</summary>
public interface IVoiceprintClient : IDisposable
{
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<byte[]> wavClips, CancellationToken ct);
}

/// <summary>
/// The voiceprint service through an OpenAI-style <c>POST /embeddings</c>, each input a
/// <c>data:audio/wav;base64,…</c> string (our convention on OpenAI's request shape; design 3.6).
/// </summary>
public sealed class VoiceprintClient : IVoiceprintClient
{
    /// <summary>Base64 bytes per request: under common proxy upload limits (nginx's default is 1 MB).</summary>
    public const int MaxRequestChars = 900_000;

    readonly HttpClient _http;
    readonly string _baseUrl;
    readonly string _model;

    public VoiceprintClient(string baseUrl, string model, string? apiKey, HttpMessageHandler? handler = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _model = model;
        _http = Http.Create(handler);
        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
    }

    /// <summary>The configured client, or null with a reason when speaker names can't run.</summary>
    public static IVoiceprintClient? Create(AppSettings settings, ISecretStore secrets, out string? whyNot, HttpMessageHandler? handler = null)
    {
        var s = settings.Speakers;
        var baseUrl = s.ResolvedBaseUrl(settings);
        if (!AppSettings.IsHttpUrl(baseUrl)) { whyNot = "speakers.baseUrl is not set"; return null; }
        whyNot = null;
        return new VoiceprintClient(baseUrl, s.Model, KeyFor(settings, secrets), handler);
    }

    /// <summary>
    /// The voiceprint key; if there is none and the service is on the same server as the summary model
    /// (same scheme, host and port — usually one LiteLLM), the summary key (design 6.4).
    /// </summary>
    public static string? KeyFor(AppSettings settings, ISecretStore secrets)
    {
        var s = settings.Speakers;
        if (!string.IsNullOrWhiteSpace(s.ApiKeySecret) && secrets.Get(s.ApiKeySecret) is { Length: > 0 } own) return own;
        if (SameServer(s.ResolvedBaseUrl(settings), settings.Summary.BaseUrl) && !string.IsNullOrWhiteSpace(settings.Summary.ApiKeySecret))
            return secrets.Get(settings.Summary.ApiKeySecret);
        return null;
    }

    public static bool SameServer(string? a, string? b) =>
        Uri.TryCreate(a, UriKind.Absolute, out var x) && Uri.TryCreate(b, UriKind.Absolute, out var y) &&
        x.Scheme == y.Scheme && string.Equals(x.Host, y.Host, StringComparison.OrdinalIgnoreCase) && x.Port == y.Port;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<byte[]> wavClips, CancellationToken ct)
    {
        var result = new List<float[]>(wavClips.Count);
        var batch = new List<string>();
        int chars = 0;
        foreach (var clip in wavClips)
        {
            var url = Wav.DataUrl(clip);
            if (batch.Count > 0 && chars + url.Length > MaxRequestChars)
            {
                result.AddRange(await PostAsync(batch, ct));
                batch.Clear();
                chars = 0;
            }
            batch.Add(url);
            chars += url.Length;
        }
        if (batch.Count > 0) result.AddRange(await PostAsync(batch, ct));
        return result;
    }

    async Task<IReadOnlyList<float[]>> PostAsync(List<string> inputs, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(2));
        var body = new { model = _model, input = inputs, encoding_format = "float" };
        using var response = await _http.PostAsync($"{_baseUrl}/embeddings", Http.JsonBody(body), cts.Token);
        using var doc = await Http.ReadJson(response, "voiceprints", cts.Token);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new BackendException("voiceprints: response has no data array");

        var vectors = new float[inputs.Count][];
        foreach (var item in data.EnumerateArray())
        {
            int index = item.TryGetProperty("index", out var i) ? i.GetInt32() : Array.IndexOf(vectors, null);
            if (index < 0 || index >= vectors.Length) throw new BackendException("voiceprints: response index out of range");
            var e = item.GetProperty("embedding");
            vectors[index] = Vectors.Normalize(e.ValueKind == JsonValueKind.String
                ? Vectors.FromBase64(e.GetString()!)
                : e.EnumerateArray().Select(x => x.GetSingle()).ToArray());
        }
        if (vectors.Any(v => v is null)) throw new BackendException($"voiceprints: asked for {inputs.Count}, got {data.GetArrayLength()}");
        return vectors;
    }

    public void Dispose() => _http.Dispose();
}
