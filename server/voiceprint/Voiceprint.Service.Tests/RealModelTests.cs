using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Xunit.Abstractions;

namespace Voiceprint.Service.Tests;

/// <summary>Runs only when VOICEPRINT_TEST_MODEL points at a sherpa-onnx speaker model.</summary>
public sealed class ModelFactAttribute : FactAttribute
{
    public static string? ModelPath => Environment.GetEnvironmentVariable("VOICEPRINT_TEST_MODEL");

    public ModelFactAttribute()
    {
        if (string.IsNullOrEmpty(ModelPath) || !File.Exists(ModelPath))
            Skip = "set VOICEPRINT_TEST_MODEL to a sherpa-onnx speaker model (.onnx) to run";
    }
}

public class RealModelTests(ITestOutputHelper output)
{
    [ModelFact]
    public async Task Real_model_returns_unit_vectors_that_tell_two_synthetic_voices_apart()
    {
        using var app = new TestApp(new() { ["VOICEPRINT_MODEL_PATH"] = ModelFactAttribute.ModelPath });
        var dim = (int)JsonNode.Parse(await app.Client.GetStringAsync("/healthz"))!["dim"]!;

        // Same "voice" (pitch + timbre) twice with different phases and noise, then a different one.
        var clips = new[] { Voice(110, 1.0, seed: 1), Voice(110, 1.0, seed: 2), Voice(230, 0.4, seed: 3) }
            .Select(s => TestWav.DataUrl(TestWav.Build(TestWav.Pcm16(s))))
            .ToArray();
        var response = await app.Client.PostAsJsonAsync("/v1/embeddings", new { model = "x", input = clips });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var vectors = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["data"]!.AsArray()
            .Select(d => d!["embedding"]!.AsArray().Select(x => (float)x!).ToArray())
            .ToArray();
        Assert.All(vectors, v =>
        {
            Assert.Equal(dim, v.Length);
            Assert.Equal(1.0, Math.Sqrt(v.Sum(x => (double)x * x)), precision: 4);
        });
        double same = Cosine(vectors[0], vectors[1]), different = Cosine(vectors[0], vectors[2]);
        output.WriteLine($"dim {dim}, cosine same voice {same:0.000}, different voice {different:0.000}");
        Assert.True(same > different);
    }

    /// <summary>3 s of a crude voiced sound: harmonics falling off as k^-tilt, vibrato, syllable envelope.</summary>
    static float[] Voice(double f0, double tilt, int seed)
    {
        var rng = new Random(seed);
        var harmonics = (int)Math.Min(30, 7_500 / f0);
        var phases = Enumerable.Range(0, harmonics).Select(_ => rng.NextDouble() * 2 * Math.PI).ToArray();
        var gains = Enumerable.Range(1, harmonics).Select(k => Math.Pow(k, -tilt)).ToArray();
        var scale = 0.1 / Math.Sqrt(gains.Sum(g => g * g)); // ~0.07 RMS whatever the timbre
        var samples = new float[3 * 16_000];
        double phase = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var t = i / 16_000.0;
            phase += 2 * Math.PI * f0 * (1 + 0.02 * Math.Sin(2 * Math.PI * 5 * t)) / 16_000;
            double x = 0;
            for (var k = 0; k < harmonics; k++)
                x += gains[k] * Math.Sin((k + 1) * phase + phases[k]);
            var envelope = 0.55 + 0.45 * Math.Sin(2 * Math.PI * 3.5 * t + seed);
            samples[i] = (float)(scale * envelope * x + 0.005 * (rng.NextDouble() - 0.5));
        }
        return samples;
    }

    static double Cosine(float[] a, float[] b) => a.Zip(b, (x, y) => (double)x * y).Sum(); // unit vectors
}
