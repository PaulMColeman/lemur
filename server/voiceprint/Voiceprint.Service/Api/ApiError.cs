namespace Voiceprint.Service.Api;

/// <summary>OpenAI-shaped errors: <c>{"error":{"message","type","param","code"}}</c>.</summary>
static class ApiError
{
    public static IResult Result(int status, string message, string type = "invalid_request_error",
        string? param = null, string? code = null) =>
        Results.Json(new ErrorBody(new ErrorDetail(message, type, param, code)), statusCode: status);

    public static Task Write(HttpContext ctx, int status, string message, string? code = null) =>
        Result(status, message, code: code).ExecuteAsync(ctx);
}

/// <summary>A malformed request; the message is safe to return (it never quotes audio).</summary>
sealed class RequestException(string message, string? param = "input") : Exception(message)
{
    public string? Param { get; } = param;
}
