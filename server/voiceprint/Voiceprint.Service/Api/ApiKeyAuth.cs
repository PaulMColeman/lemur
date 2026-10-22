using System.Security.Cryptography;
using System.Text;

namespace Voiceprint.Service.Api;

/// <summary>
/// If VOICEPRINT_API_KEY is set, /v1/* needs <c>Authorization: Bearer &lt;key&gt;</c>. /healthz stays
/// open for container health checks. Hashing both sides first makes the compare constant-time
/// regardless of length.
/// </summary>
sealed class ApiKeyAuth(RequestDelegate next, VoiceprintOptions options)
{
    readonly byte[]? _keyHash = options.ApiKey is null ? null : Hash(options.ApiKey);

    public Task InvokeAsync(HttpContext ctx)
    {
        if (_keyHash is null || !ctx.Request.Path.StartsWithSegments("/v1") || Authorized(ctx.Request))
            return next(ctx);
        return ApiError.Write(ctx, StatusCodes.Status401Unauthorized,
            "missing or wrong API key; send Authorization: Bearer <key>", code: "invalid_api_key");
    }

    bool Authorized(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return false;
        return CryptographicOperations.FixedTimeEquals(Hash(header[scheme.Length..].Trim()), _keyHash);
    }

    static byte[] Hash(string s) => SHA256.HashData(Encoding.UTF8.GetBytes(s));
}
