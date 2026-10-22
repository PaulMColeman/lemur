namespace Lemur.Speakers;

/// <summary>Voiceprint arithmetic. Vectors are kept unit-length so cosine similarity is a dot product.</summary>
public static class Vectors
{
    public static float[] Normalize(float[] v)
    {
        double norm = 0;
        foreach (var x in v) norm += x * x;
        norm = Math.Sqrt(norm);
        if (norm < 1e-12) return (float[])v.Clone();
        var result = new float[v.Length];
        for (int i = 0; i < v.Length; i++) result[i] = (float)(v[i] / norm);
        return result;
    }

    /// <summary>Cosine similarity; vectors of different length (different models) are unrelated: -1.</summary>
    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return -1;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return na < 1e-12 || nb < 1e-12 ? -1 : dot / Math.Sqrt(na * nb);
    }

    /// <summary>Weighted average of unit vectors, normalised again.</summary>
    public static float[] WeightedMean(IReadOnlyList<(float[] Vector, double Weight)> items)
    {
        if (items.Count == 0) throw new ArgumentException("no vectors to average");
        var sum = new double[items[0].Vector.Length];
        foreach (var (v, w) in items)
            for (int i = 0; i < sum.Length; i++) sum[i] += v[i] * w;
        return Normalize(sum.Select(x => (float)x).ToArray());
    }

    public static string ToBase64(float[] v)
    {
        var bytes = new byte[v.Length * 4];
        Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
        return Convert.ToBase64String(bytes);
    }

    public static float[] FromBase64(string s)
    {
        var bytes = Convert.FromBase64String(s);
        var v = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, v, 0, v.Length * 4);
        return v;
    }
}
