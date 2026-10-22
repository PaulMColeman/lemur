using System.Buffers;
using System.Buffers.Binary;
using Voiceprint.Service.Audio;
using Voiceprint.Service.Embedding;
using static System.FormattableString;

namespace Voiceprint.Service.Api;

static class VoiceprintApi
{
    static readonly long Started = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public static void MapVoiceprintApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz", (IEmbedder e) => Results.Json(new Health(true, e.ModelId, e.Dim)));
        app.MapGet("/v1/models", (IEmbedder e) =>
            Results.Json(new ModelList("list", [new ModelInfo(e.ModelId, "model", Started, "voiceprint")])));
        app.MapPost("/v1/embeddings", Embeddings);
    }

    static async Task<IResult> Embeddings(HttpContext ctx, IEmbedder embedder, VoiceprintOptions o,
        ILoggerFactory logs)
    {
        var body = await ReadBody(ctx.Request, o.MaxBodyBytes, ctx.RequestAborted);
        if (body is null)
            return ApiError.Result(StatusCodes.Status413PayloadTooLarge,
                $"request body is over {o.MaxBodyBytes} bytes; send fewer clips per request", param: "input");

        EmbeddingsRequest request;
        float[][] clips;
        long samples = 0;
        try
        {
            request = EmbeddingsRequest.Parse(body.Value, o.MaxClips);
            RequestLog.Note(ctx, request.Inputs.Count, 0);
            clips = new float[request.Inputs.Count][];
            for (var i = 0; i < clips.Length; i++)
            {
                clips[i] = DecodeClip(request.Inputs[i], i, o);
                samples += clips[i].Length;
            }
        }
        catch (RequestException ex)
        {
            return ApiError.Result(StatusCodes.Status400BadRequest, ex.Message, param: ex.Param);
        }
        RequestLog.Note(ctx, clips.Length, (double)samples / ClipDecoder.SampleRate);

        float[][] vectors;
        try
        {
            vectors = embedder.Embed(clips);
        }
        catch (Exception ex)
        {
            logs.CreateLogger("Voiceprint.Embeddings")
                .LogError("Embedding failed: {Type}: {Message}", ex.GetType().Name, ex.Message);
            return ApiError.Result(StatusCodes.Status500InternalServerError, "embedding failed", "server_error");
        }

        var data = new EmbeddingItem[clips.Length];
        for (var i = 0; i < data.Length; i++)
        {
            if (i >= vectors.Length || vectors[i].Length != embedder.Dim || !Normalize(vectors[i]))
                return ApiError.Result(StatusCodes.Status500InternalServerError,
                    $"input[{i}]: the model returned an unusable embedding", "server_error");
            data[i] = new EmbeddingItem("embedding", i, request.Base64 ? ToBase64(vectors[i]) : vectors[i]);
        }

        var seconds = (int)((samples + ClipDecoder.SampleRate - 1) / ClipDecoder.SampleRate);
        return Results.Json(new EmbeddingsResponse("list", data, embedder.ModelId, new Usage(seconds, seconds)));
    }

    static float[] DecodeClip(string input, int index, VoiceprintOptions o)
    {
        float[] clip;
        try
        {
            clip = ClipDecoder.Decode(input);
        }
        catch (ClipFormatException ex)
        {
            throw new RequestException($"input[{index}]: {ex.Message}");
        }

        var seconds = (double)clip.Length / ClipDecoder.SampleRate;
        if (seconds < o.MinClipSeconds || seconds > o.MaxClipSeconds)
            throw new RequestException(Invariant(
                $"input[{index}]: clip is {seconds:0.###} s; each clip must be {o.MinClipSeconds}-{o.MaxClipSeconds} s"));
        return clip;
    }

    /// <summary>
    /// Null when the body is over the limit. Checked here as well as by Kestrel's
    /// MaxRequestBodySize so the client always gets the JSON error shape, and tests (TestServer) too.
    /// </summary>
    static async Task<ReadOnlyMemory<byte>?> ReadBody(HttpRequest request, long max, CancellationToken ct)
    {
        if (request.ContentLength > max) return null;
        using var buffer = new MemoryStream((int)Math.Min(request.ContentLength ?? 1 << 16, max));
        var chunk = ArrayPool<byte>.Shared.Rent(1 << 16);
        try
        {
            int n;
            while ((n = await request.Body.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + n > max) return null;
                buffer.Write(chunk, 0, n);
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
        return buffer.GetBuffer().AsMemory(0, (int)buffer.Length);
    }

    /// <summary>Scales to unit length in place; false for a zero or non-finite vector.</summary>
    internal static bool Normalize(float[] v)
    {
        double sum = 0;
        foreach (var x in v) sum += (double)x * x;
        var norm = Math.Sqrt(sum);
        if (!double.IsFinite(norm) || norm == 0) return false;
        for (var i = 0; i < v.Length; i++) v[i] = (float)(v[i] / norm);
        return true;
    }

    static string ToBase64(float[] v)
    {
        var bytes = new byte[v.Length * sizeof(float)];
        for (var i = 0; i < v.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), v[i]);
        return Convert.ToBase64String(bytes);
    }
}
