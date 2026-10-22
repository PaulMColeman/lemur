using System.Net.Http.Headers;
using System.Text.Json;
using Lemur.Config;
using Lemur.Speakers;

namespace Lemur.Backends;

public sealed record CheckResult(string What, bool Ok, string Detail);

/// <summary>
/// "Test connection" (design 7.2): each server checked on its own, and only what is switched on, so a
/// user who said no to speaker names isn't failed on the voiceprint model (design 7.4).
/// </summary>
public static class ConnectionTest
{
    public static async Task<IReadOnlyList<CheckResult>> RunAsync(AppSettings s, ISecretStore secrets, CancellationToken ct,
        HttpMessageHandler? handler = null)
    {
        var results = new List<CheckResult>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(45));

        if (s.Backend.Type == BackendTypes.Scriberr)
        {
            var key = secrets.Get(s.Backend.Scriberr.ApiKeySecret);
            results.Add(await Check("Scriberr (transcription)", async () =>
            {
                if (key is null) return (false, $"no key saved ({s.Backend.Scriberr.ApiKeySecret})");
                using var http = Http.Create(handler);
                http.DefaultRequestHeaders.Add("X-API-Key", key);
                using var r = await http.GetAsync($"{s.Backend.Scriberr.BaseUrl.TrimEnd('/')}/api/v1/profiles/", cts.Token);
                using var doc = await Http.ReadJson(r, "Scriberr", cts.Token);
                int n = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
                return (true, $"{n} transcription profile(s)");
            }));
        }
        else
        {
            var stt = s.Backend.OpenAi;
            results.Add(await Models("Transcription", stt.TranscriptionBaseUrl, stt.TranscriptionModel,
                string.IsNullOrWhiteSpace(stt.TranscriptionApiKeySecret) ? null : secrets.Get(stt.TranscriptionApiKeySecret), handler, cts.Token));
        }

        bool summaryNeeded = s.Backend.Type == BackendTypes.OpenAi || s.Speakers.NameFromIntroductions;
        if (summaryNeeded && AppSettings.IsHttpUrl(s.Summary.BaseUrl))
            results.Add(await Models("Summary model", s.Summary.BaseUrl, s.Summary.Model,
                string.IsNullOrWhiteSpace(s.Summary.ApiKeySecret) ? null : secrets.Get(s.Summary.ApiKeySecret), handler, cts.Token));
        else if (s.Backend.Type == BackendTypes.OpenAi)
            results.Add(new CheckResult("Summary model", false, "summary URL isn't set"));

        if (s.Speakers.Enabled)
        {
            results.Add(await Check("Voiceprint model", async () =>
            {
                using var client = VoiceprintClient.Create(s, secrets, out var whyNot, handler);
                if (client is null) return (false, whyNot!);
                var v = (await client.EmbedAsync([Calibration.SignalWav()], cts.Token))[0];
                var numbers = BuiltInThresholds.For(v, s.Speakers.Level, s.Speakers.Thresholds);
                return (true, numbers is null
                    ? $"answers ({v.Length} dimensions), but there are no matching numbers for this model yet"
                    : $"answers ({v.Length} dimensions), matching numbers found");
            }));
        }
        return results;
    }

    /// <summary>An OpenAI-style server: reachable, key accepted, and (when it lists models) the model is there.</summary>
    static Task<CheckResult> Models(string what, string baseUrl, string model, string? key, HttpMessageHandler? handler, CancellationToken ct) =>
        Check(what, async () =>
        {
            if (!AppSettings.IsHttpUrl(baseUrl)) return (false, "URL isn't set");
            using var http = Http.Create(handler);
            if (!string.IsNullOrWhiteSpace(key)) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
            using var r = await http.GetAsync($"{baseUrl.TrimEnd('/')}/models", ct);
            if ((int)r.StatusCode is 401 or 403) return (false, "the key was refused");
            if ((int)r.StatusCode == 404) return (true, "reachable (it doesn't list models)");
            using var doc = await Http.ReadJson(r, what, ct);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return (true, "reachable");
            var ids = data.EnumerateArray().Select(m => m.TryGetProperty("id", out var id) ? id.GetString() : null).ToList();
            return ids.Contains(model) || string.IsNullOrWhiteSpace(model)
                ? (true, $"reachable; '{model}' is available")
                : (false, $"reachable, but '{model}' isn't one of its models");
        });

    static async Task<CheckResult> Check(string what, Func<Task<(bool Ok, string Detail)>> run)
    {
        try
        {
            var (ok, detail) = await run();
            return new CheckResult(what, ok, detail);
        }
        catch (BackendException ex)
        {
            return new CheckResult(what, false, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return new CheckResult(what, false, ex is HttpRequestException h ? $"couldn't connect: {Http.SafeSnippet(h.Message, 120)}" : "timed out");
        }
    }
}
