using System.Security.Cryptography;
using System.Text.Json;
using Lemur.Config;

namespace Lemur.Speakers;

/// <summary>
/// Which model is answering, decided by what it does rather than what it is called (design 3.7).
/// The app sends a fixed synthetic signal; the voiceprint that comes back identifies the model.
/// Voiceprints from different models are never compared.
/// </summary>
public static class Calibration
{
    /// <summary>Two answers this close come from the same model (allowing for CPU/GPU rounding).</summary>
    public const double SameModel = 0.995;

    /// <summary>
    /// 3 s of something voice-like (a gliding pitch with harmonics, syllable-rate loudness, a little noise),
    /// generated rather than shipped as a file. Integer-seeded noise and 16-bit rounding keep it identical
    /// on every PC.
    /// </summary>
    public static short[] Signal()
    {
        const int rate = 16000;
        var samples = new short[3 * rate];
        uint seed = 0x2545F491;
        double phase = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            double t = (double)i / rate;
            double f0 = 125 + 25 * Math.Sin(2 * Math.PI * 0.7 * t);
            phase += 2 * Math.PI * f0 / rate;
            double voice = 0;
            for (int h = 1; h <= 12; h++)
            {
                // Two broad "formant" bumps so the spectrum isn't flat.
                double f = h * f0;
                double formant = Math.Exp(-Math.Pow((f - 700) / 300, 2)) + 0.6 * Math.Exp(-Math.Pow((f - 1200) / 400, 2)) + 0.15;
                voice += formant * Math.Sin(h * phase) / h;
            }
            double syllables = 0.55 + 0.45 * Math.Sin(2 * Math.PI * 4 * t);
            seed = seed * 1664525 + 1013904223;
            double noise = ((seed >> 8) / (double)(1 << 24) - 0.5) * 0.02;
            samples[i] = (short)Math.Round(Math.Clamp((voice * syllables * 0.25) + noise, -1, 1) * 32767);
        }
        return samples;
    }

    public static byte[] SignalWav() => Wav.Encode(Signal());

    /// <summary>A short, stable label for a model's answer: "fp-" + 6 hex digits.</summary>
    public static string FingerprintId(float[] calibration)
    {
        var rounded = calibration.Select(x => Math.Round(x, 2).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
        var hash = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(string.Join(",", rounded)));
        return "fp-" + Convert.ToHexString(hash)[..6].ToLowerInvariant();
    }
}

/// <summary>A model seen by some PC sharing this people folder.</summary>
public sealed class ModelRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Dim { get; set; }
    public string Calibration { get; set; } = "";
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}

/// <summary>
/// <c>models\&lt;id&gt;.json</c> in the people folder, one file per model so two PCs never write the same file
/// for different models.
/// </summary>
public sealed class ModelRegistry(string peopleFolder)
{
    public string Folder { get; } = Path.Combine(peopleFolder, "models");

    public IReadOnlyList<ModelRecord> All()
    {
        if (!Directory.Exists(Folder)) return [];
        var list = new List<ModelRecord>();
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<ModelRecord>(File.ReadAllText(file), Json.Options) is { Id.Length: > 0 } r) list.Add(r);
            }
            catch (JsonException) { } // a half-synced file; it will be complete next time
        }
        return list;
    }

    /// <summary>The id for this calibration answer, recording the model if it is new.</summary>
    public string Resolve(float[] calibration, string modelName, DateTimeOffset now)
    {
        var known = All().Where(r => r.Dim == calibration.Length)
            .Select(r => (Record: r, Score: Vectors.Cosine(Vectors.FromBase64(r.Calibration), calibration)))
            .Where(x => x.Score >= Calibration.SameModel)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Record)
            .FirstOrDefault();
        if (known is not null)
        {
            if (now - known.LastSeen > TimeSpan.FromDays(1)) // daily at most, to keep sync quiet
            {
                known.LastSeen = now;
                Save(known);
            }
            return known.Id;
        }
        var record = new ModelRecord
        {
            Id = Calibration.FingerprintId(calibration),
            Name = modelName,
            Dim = calibration.Length,
            Calibration = Vectors.ToBase64(calibration),
            FirstSeen = now,
            LastSeen = now,
        };
        Save(record);
        return record.Id;
    }

    void Save(ModelRecord record)
    {
        Directory.CreateDirectory(Folder);
        AtomicFile.WriteJson(Path.Combine(Folder, record.Id + ".json"), record);
    }
}
