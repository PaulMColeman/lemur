using System.Text.Json.Serialization;

namespace Voiceprint.Service.Api;

// Wire shapes of OpenAI's embeddings/models API, so LiteLLM's "openai/<id>" provider can call us.

sealed record EmbeddingsResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("data")] IReadOnlyList<EmbeddingItem> Data,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("usage")] Usage Usage);

/// <param name="Embedding">float[] or, for encoding_format=base64, a base64 string of little-endian float32.</param>
sealed record EmbeddingItem(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("embedding")] object Embedding);

/// <summary>"Tokens" are audio seconds, rounded up, so LiteLLM's usage logs show something meaningful.</summary>
sealed record Usage(
    [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
    [property: JsonPropertyName("total_tokens")] int TotalTokens);

sealed record ModelList(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("data")] IReadOnlyList<ModelInfo> Data);

sealed record ModelInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("owned_by")] string OwnedBy);

sealed record Health(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("dim")] int Dim);

sealed record ErrorBody([property: JsonPropertyName("error")] ErrorDetail Error);

sealed record ErrorDetail(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("param")] string? Param,
    [property: JsonPropertyName("code")] string? Code);
