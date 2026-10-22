using System.Buffers.Binary;

namespace Voiceprint.Service.Tests;

/// <summary>Builds WAV files and data URLs in memory.</summary>
static class TestWav
{
    public static byte[] Build(byte[] data, int rate = 16_000, int channels = 1, int bits = 16, int tag = 1,
        byte[]? chunkBeforeData = null)
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        var extra = chunkBeforeData ?? [];
        w.Write("RIFF"u8);
        w.Write(4 + 24 + extra.Length + 8 + data.Length);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)tag);
        w.Write((short)channels);
        w.Write(rate);
        w.Write(rate * channels * bits / 8);
        w.Write((short)(channels * bits / 8));
        w.Write((short)bits);
        w.Write(extra);
        w.Write("data"u8);
        w.Write(data.Length);
        w.Write(data);
        return ms.ToArray();
    }

    public static byte[] Pcm16(float[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2),
                (short)Math.Clamp(samples[i] * 32767f, short.MinValue, short.MaxValue));
        return bytes;
    }

    public static float[] Tone(double seconds, double hz = 220, double amplitude = 0.3)
    {
        var samples = new float[(int)(seconds * 16_000)];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / 16_000));
        return samples;
    }

    public static string DataUrl(byte[] wav, string mediaType = "audio/wav") =>
        $"data:{mediaType};base64,{Convert.ToBase64String(wav)}";

    public static string Clip(double seconds, double hz = 220) => DataUrl(Build(Pcm16(Tone(seconds, hz))));
}
