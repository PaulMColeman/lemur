using System.Text.Json;
using NAudio.Utils;
using NAudio.Wave;
using Lemur.Config;

namespace Lemur.Speakers;

/// <summary>16 kHz mono 16-bit WAV — the format the recorder writes and the voiceprint service takes.</summary>
public static class Wav
{
    public const int SampleRate = 16000;

    public static byte[] Encode(ReadOnlySpan<short> samples)
    {
        using var ms = new MemoryStream(44 + samples.Length * 2);
        using (var writer = new WaveFileWriter(new IgnoreDisposeStream(ms), new WaveFormat(SampleRate, 16, 1)))
        {
            var bytes = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                bytes[2 * i] = (byte)samples[i];
                bytes[2 * i + 1] = (byte)(samples[i] >> 8);
            }
            writer.Write(bytes, 0, bytes.Length);
        }
        return ms.ToArray();
    }

    /// <summary>Samples between two times of a recorder track (or any 16 kHz mono 16-bit WAV).</summary>
    public static short[] Read(string path, double startSeconds, double endSeconds)
    {
        using var reader = new WaveFileReader(path);
        var f = reader.WaveFormat;
        if (f.SampleRate != SampleRate || f.Channels != 1 || f.BitsPerSample != 16 || f.Encoding != WaveFormatEncoding.Pcm)
            throw new InvalidDataException($"{Path.GetFileName(path)} is not 16 kHz mono 16-bit PCM");
        long total = reader.Length / 2;
        long a = Math.Clamp((long)(startSeconds * SampleRate), 0, total);
        long b = Math.Clamp((long)(endSeconds * SampleRate), a, total);
        reader.Position = a * 2;
        var bytes = new byte[(b - a) * 2];
        int read = 0;
        while (read < bytes.Length)
        {
            int n = reader.Read(bytes, read, bytes.Length - read);
            if (n <= 0) break;
            read += n;
        }
        var samples = new short[read / 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = (short)(bytes[2 * i] | (bytes[2 * i + 1] << 8));
        return samples;
    }

    public static string DataUrl(byte[] wav) => "data:audio/wav;base64," + Convert.ToBase64String(wav);
}

/// <summary>Write to a temporary file, then swap it in: a sync client or a crash never sees half a file.</summary>
public static class AtomicFile
{
    public static void WriteJson<T>(string path, T value) =>
        WriteBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, Json.Options));

    public static void WriteBytes(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }
}
