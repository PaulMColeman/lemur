using System.Text.Json.Serialization;

namespace Lemur.Audio;

/// <summary>Loudness of a track over time: one RMS value (dBFS) per frame.</summary>
public sealed class Envelope
{
    public const double SilenceDb = -100;

    [JsonConstructor]
    public Envelope(double frameSeconds, float[] db)
    {
        FrameSeconds = frameSeconds;
        Db = db;
    }

    public double FrameSeconds { get; }
    public float[] Db { get; }

    public double DurationSeconds => Db.Length * FrameSeconds;

    /// <summary>Energy-average loudness between two times (power mean, not dB mean).</summary>
    public double MeanDb(double startSeconds, double endSeconds)
    {
        if (Db.Length == 0) return SilenceDb;
        var (a, b) = Frames(startSeconds, endSeconds);
        double power = 0;
        for (int i = a; i < b; i++) power += Math.Pow(10, Db[i] / 10.0);
        return ToDb(power / (b - a));
    }

    /// <summary>The frames from <c>From</c> up to (not including) <c>To</c> that cover two times; at least one frame, if there are any.</summary>
    public (int From, int To) Frames(double startSeconds, double endSeconds)
    {
        if (Db.Length == 0) return (0, 0);
        int a = Math.Clamp((int)Math.Floor(startSeconds / FrameSeconds), 0, Db.Length - 1);
        int b = Math.Clamp((int)Math.Ceiling(endSeconds / FrameSeconds), a + 1, Db.Length);
        return (a, b);
    }

    /// <summary>p in 0..100 over all frames.</summary>
    public double Percentile(double p)
    {
        if (Db.Length == 0) return SilenceDb;
        var sorted = (float[])Db.Clone();
        Array.Sort(sorted);
        int index = (int)Math.Clamp(Math.Round(p / 100.0 * (sorted.Length - 1)), 0, sorted.Length - 1);
        return sorted[index];
    }

    public static double ToDb(double meanSquare) =>
        meanSquare <= 1e-10 ? SilenceDb : Math.Max(SilenceDb, 10 * Math.Log10(meanSquare));
}
