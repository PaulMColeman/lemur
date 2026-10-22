using System.Text.Json;

namespace Voiceprint.Service.Api;

/// <summary>
/// The parts of an OpenAI embeddings request we use. <c>model</c> is ignored: one model per service,
/// and proxies may rename it anyway. Unknown fields (<c>user</c>, ...) are ignored too.
/// </summary>
sealed record EmbeddingsRequest(IReadOnlyList<string> Inputs, bool Base64)
{
    public static EmbeddingsRequest Parse(ReadOnlyMemory<byte> json, int maxClips)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new RequestException("request body is not valid JSON", param: null);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new RequestException("request body must be a JSON object", param: null);
            return new EmbeddingsRequest(ParseInputs(root, maxClips), WantsBase64(root));
        }
    }

    static List<string> ParseInputs(JsonElement root, int maxClips)
    {
        if (!root.TryGetProperty("input", out var input))
            throw new RequestException("'input' is required: a data:audio/wav;base64 URL or an array of them");

        switch (input.ValueKind)
        {
            case JsonValueKind.String:
                return [input.GetString()!];
            case JsonValueKind.Array:
                var count = input.GetArrayLength();
                if (count == 0)
                    throw new RequestException("'input' is empty");
                if (count > maxClips)
                    throw new RequestException($"too many clips: {count} (max {maxClips} per request)");
                var list = new List<string>(count);
                foreach (var item in input.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                        throw new RequestException($"input[{list.Count}]: must be a string (data:audio/wav;base64,...)");
                    list.Add(item.GetString()!);
                }
                return list;
            default:
                throw new RequestException("'input' must be a string or an array of strings");
        }
    }

    // The OpenAI Python SDK (used inside LiteLLM) asks for base64 unless told otherwise.
    static bool WantsBase64(JsonElement root)
    {
        if (!root.TryGetProperty("encoding_format", out var format) || format.ValueKind == JsonValueKind.Null)
            return false;
        return (format.ValueKind == JsonValueKind.String ? format.GetString() : null) switch
        {
            "float" => false,
            "base64" => true,
            _ => throw new RequestException("'encoding_format' must be \"float\" or \"base64\"", "encoding_format"),
        };
    }
}
