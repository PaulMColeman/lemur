using System.Globalization;
using System.Numerics;

namespace Voiceprint.Service;

/// <summary>Everything comes from VOICEPRINT_* environment variables (any IConfiguration source works).</summary>
public sealed record VoiceprintOptions
{
    public string? ModelPath { get; init; }
    public string? ModelId { get; init; }
    public int Threads { get; init; } = 2;
    public string Provider { get; init; } = "cpu";
    public string? ApiKey { get; init; }
    public int MaxClips { get; init; } = 64;
    public double MinClipSeconds { get; init; } = 0.5;
    public double MaxClipSeconds { get; init; } = 15;
    public long MaxBodyBytes { get; init; } = 8 * 1024 * 1024;

    public static VoiceprintOptions From(IConfiguration c)
    {
        var o = new VoiceprintOptions
        {
            ModelPath = Text(c, "VOICEPRINT_MODEL_PATH"),
            ModelId = Text(c, "VOICEPRINT_MODEL_ID"),
            Provider = Text(c, "VOICEPRINT_PROVIDER") ?? "cpu",
            ApiKey = Text(c, "VOICEPRINT_API_KEY"),
        };
        o = o with
        {
            Threads = Number(c, "VOICEPRINT_THREADS", o.Threads, 1, 64),
            MaxClips = Number(c, "VOICEPRINT_MAX_CLIPS", o.MaxClips, 1, 10_000),
            MinClipSeconds = Number(c, "VOICEPRINT_MIN_CLIP_SECONDS", o.MinClipSeconds, 0.1, 600),
            MaxClipSeconds = Number(c, "VOICEPRINT_MAX_CLIP_SECONDS", o.MaxClipSeconds, 0.1, 600),
            MaxBodyBytes = Number(c, "VOICEPRINT_MAX_BODY_BYTES", o.MaxBodyBytes, 1024, 1L << 30),
        };
        if (o.MinClipSeconds > o.MaxClipSeconds)
            throw new ConfigException("VOICEPRINT_MIN_CLIP_SECONDS is larger than VOICEPRINT_MAX_CLIP_SECONDS.");
        return o;
    }

    static string? Text(IConfiguration c, string key) =>
        string.IsNullOrWhiteSpace(c[key]) ? null : c[key]!.Trim();

    static T Number<T>(IConfiguration c, string key, T fallback, T min, T max) where T : INumber<T>
    {
        var s = Text(c, key);
        if (s is null) return fallback;
        if (T.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= min && v <= max)
            return v;
        throw new ConfigException($"{key}=\"{s}\" is not a number between {min} and {max}.");
    }
}

/// <summary>Startup configuration problem; the message is shown to the operator as-is.</summary>
public sealed class ConfigException(string message) : Exception(message);
