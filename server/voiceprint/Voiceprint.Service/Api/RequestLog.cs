using System.Diagnostics;

namespace Voiceprint.Service.Api;

/// <summary>
/// The only per-request log line: method, path, status, clip count, audio seconds, elapsed.
/// Never bodies, vectors or headers. /healthz is skipped so container probes don't flood the log.
/// </summary>
sealed class RequestLog(RequestDelegate next, ILogger<RequestLog> log)
{
    const string ClipsKey = "voiceprint.clips";
    const string SecondsKey = "voiceprint.seconds";

    public static void Note(HttpContext ctx, int clips, double seconds)
    {
        ctx.Items[ClipsKey] = clips;
        ctx.Items[SecondsKey] = seconds;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (ctx.Request.Path.StartsWithSegments("/healthz"))
        {
            await next(ctx);
            return;
        }

        var start = Stopwatch.GetTimestamp();
        var status = StatusCodes.Status500InternalServerError;
        try
        {
            await next(ctx);
            status = ctx.Response.StatusCode;
        }
        finally
        {
            var clips = ctx.Items[ClipsKey] as int? ?? 0;
            var seconds = ctx.Items[SecondsKey] as double? ?? 0;
            log.LogInformation("{Method} {Path} {Status} clips={Clips} audio={Seconds:0.0}s {ElapsedMs:0}ms",
                ctx.Request.Method, ctx.Request.Path.Value, status, clips, seconds,
                Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
    }
}
