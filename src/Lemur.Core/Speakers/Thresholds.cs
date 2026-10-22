namespace Lemur.Speakers;

/// <summary>
/// The numbers matching runs on (design 3.3). <see cref="Accept"/>: a clear match. <see cref="Maybe"/>:
/// shown as "Dana?". <see cref="Margin"/>: how far ahead of the runner-up a clear match must be.
/// <see cref="KindPenalty"/>: subtracted when comparing a call voice with a local one, or vice versa.
/// </summary>
public sealed record ThresholdSet(double Accept, double Maybe, double Margin, double KindPenalty);

/// <summary>
/// Matching numbers for one model, tied to the model by its answer to the calibration signal rather than
/// its name (proxies rename models). Five levels, Careful → Eager. Produced by `llmmr voices benchmark`.
/// </summary>
public sealed class ModelThresholds
{
    public string Name { get; set; } = "";

    /// <summary>The model's voiceprint of <see cref="Calibration.Signal"/>, base64 float32.</summary>
    public string Calibration { get; set; } = "";

    public List<ThresholdSet> Levels { get; set; } = [];

    public ThresholdSet? Level(int level) =>
        Levels.Count == 0 ? null : Levels[Math.Clamp(level, 1, Levels.Count) - 1];

    /// <summary>True when <paramref name="calibration"/> came from the same model these numbers were fitted on.</summary>
    public bool Fits(float[] calibration) =>
        !string.IsNullOrWhiteSpace(Calibration) &&
        Vectors.Cosine(Vectors.FromBase64(Calibration), calibration) >= Speakers.Calibration.SameModel;
}

public static class BuiltInThresholds
{
    /// <summary>
    /// From `llmmr voices benchmark` on 12 AMI series (47 people, 137 speaker-meetings, clean and through a
    /// 12 kbps Opus codec), 2026-10-13. Careful: 96% of speaker-meetings named, none wrong, 1 short clip
    /// in ~1,600 wrong. Matched to the serving model by its calibration answer, whatever the model is called.
    /// </summary>
    public static readonly IReadOnlyList<ModelThresholds> All =
    [
        new ModelThresholds
        {
            Name = "nemo_en_titanet_large",
            Calibration =
                "MF/uvfTRhL3084a9KFUyPuXqI75hclI9rW7HvDthjD0HyeI8JZ9DPYsCvjxy+Ws90Jm6ve6RrrzMXkE8vqALvMaKFL0vbvE9" +
                "JWbuPQTjTj0tPbQ9P/W0vShXULxHOm+9DYaAPHXznT2Elgi8x+YavFco0LqEoty9Bj5QPZQXcr2pKIA9oXOIPf6VAj2B47q8" +
                "5NUSOzjMPz3Kr3c9HmpnPecbFr4V4A49YIx4vADjnj38bUa8YWjgva7gsT09hTS91kvMPNi0Nj09y1A8fJ5EvbrDHz4ikWi9" +
                "Q8KYPEbRZL16GZM8GMZEumS037vmXAm9zUwPvdKDNz6Mxm89SpKavIi8pT011Bw9Zfc7vZtGITzALAw9bpmYvc2M8j3Z2SC8" +
                "giwDvMiEGT26f+s9GAo1PH02F739I+c8qaegPZzjmb31f349oMFAvPTejL3HNCE8JdBWvM2v5zznuCS9cd0BPYVqZrzsy+O9" +
                "swdwvEBwSj2AKZu9FVd4O7uIjb1rfKI8XYDKvVt4cD3T4B69velgvZr+h73XVNI9DZiRvYz3tzxKAlU97IVSvdzjWz3cEZo6" +
                "OgAjPKSE0L04dvE9XWSePdCqnj1r5ug8VsZBvREjhLznxr29MMXRvZrTLT1EmVe9g+e0PXqO/71QmfI90h2UvdeC3zxru6+9" +
                "rPuJvO/9FT14Q+g9CPBSO3t8mDy0/l89+58SvTnHWr1AG/Q9u7SRvbHtLz04EqM8DqFAPaZyZzzmIuS9gbA7vTsCsrzzNDy+" +
                "WxYnvcz+lLyxty8+/my7vJtdU70Ye7W98CwqPRETCL4sHiu8BUijvUDksTxWyKY9qI8Hvn9KtL212Gy9Jy/zPMjQOL1M3C6+" +
                "EpnEu5RwTT3XwC29jBC6vV+0s7uaHv09ev41vXU33LvPxR49BRpVPBYhRD6zSPW8lv2kvTiWxr1G/LU9kobYPJcoLL0/ss88" +
                "zGnPvIy5rbwSJlq+/wmrvD5j3T2Rq+Y9ViJdPco2cryShjC9a8vHPfAvV72njJE9",
            Levels =
            [
                new(0.662, 0.562, 0.04, 0.085),
                new(0.63, 0.53, 0, 0.085),
                new(0.617, 0.517, 0, 0.085),
                new(0.591, 0.491, 0, 0.085),
                new(0.569, 0.469, 0, 0.085),
            ],
        },
    ];

    /// <summary>Settings first (a model someone benchmarked themselves), then the built-in table.</summary>
    public static ThresholdSet? For(float[] calibration, int level, ModelThresholds? fromSettings)
    {
        if (fromSettings is not null && fromSettings.Fits(calibration)) return fromSettings.Level(level);
        return All.FirstOrDefault(m => m.Fits(calibration))?.Level(level);
    }
}
