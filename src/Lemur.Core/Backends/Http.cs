using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lemur.Backends;

static partial class Http
{
    public static HttpClient Create(HttpMessageHandler? handler)
    {
        var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.Timeout = Timeout.InfiniteTimeSpan; // every call carries its own cancellation deadline
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Lemur", typeof(Http).Assembly.GetName().Version?.ToString(3) ?? "1.0"));
        return client;
    }

    public static StringContent JsonBody(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    public static StringContent JsonBody(string rawJson) => new(rawJson, Encoding.UTF8, "application/json");

    public static StreamContent FileContent(string path, string mediaType)
    {
        var content = new StreamContent(File.OpenRead(path), 1 << 16);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return content;
    }

    /// <summary>Throws a log-safe <see cref="BackendException"/> unless the response is 2xx.</summary>
    public static async Task EnsureOk(HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new BackendException($"{what}: HTTP {(int)response.StatusCode} {SafeSnippet(body)}");
    }

    public static async Task<JsonDocument> ReadJson(HttpResponseMessage response, string what, CancellationToken ct)
    {
        await EnsureOk(response, what, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
        catch (JsonException)
        {
            throw new BackendException($"{what}: response was not JSON");
        }
    }

    /// <summary>
    /// Short, single-line, with anything that looks like a credential masked. Error bodies are
    /// echoed only through this — some backends put the request (and its tokens) in their errors.
    /// </summary>
    public static string SafeSnippet(string? text, int max = 300)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = Secretish().Replace(text, "<redacted>");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length <= max ? s : "…" + s[^max..];
    }

    [GeneratedRegex(@"(hf_[A-Za-z0-9]{8,}|sk-[A-Za-z0-9_\-]{8,}|(?i)(""?(hf_token|api_key|apikey|token|authorization|password)""?\s*[:=]\s*)""?[^"",\s}]+""?)")]
    private static partial Regex Secretish();
}
