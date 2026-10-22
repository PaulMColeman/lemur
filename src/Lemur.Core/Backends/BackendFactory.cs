using Lemur.Config;

namespace Lemur.Backends;

public static class BackendFactory
{
    /// <summary>Builds the configured backend, pulling keys from the secret store. Throws with a fixable message.</summary>
    public static IMeetingBackend Create(AppSettings settings, ISecretStore secrets, HttpMessageHandler? handler = null)
    {
        var problems = settings.Problems();
        if (problems.Count > 0) throw new BackendException("settings: " + string.Join("; ", problems));
        var hint = new Transcripts.Vocabulary(settings.Vocabulary).Hint();

        switch (settings.Backend.Type)
        {
            case BackendTypes.Scriberr:
            {
                var s = settings.Backend.Scriberr;
                var key = secrets.Get(s.ApiKeySecret)
                          ?? throw new BackendException($"no secret '{s.ApiKeySecret}' — run: llmmr set-secret {s.ApiKeySecret}");
                return new ScriberrBackend(s, key, handler, hint);
            }
            case BackendTypes.OpenAi:
            {
                var stt = settings.Backend.OpenAi;
                string? sttKey = Optional(secrets, stt.TranscriptionApiKeySecret);
                string? llmKey = Optional(secrets, settings.Summary.ApiKeySecret);
                return new OpenAiBackend(stt, sttKey, settings.Summary, llmKey, handler, hint);
            }
            default:
                throw new BackendException($"unknown backend.type '{settings.Backend.Type}'");
        }
    }

    /// <summary>A named-but-missing secret is allowed (some local endpoints need no key).</summary>
    static string? Optional(ISecretStore secrets, string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : secrets.Get(name);
}
