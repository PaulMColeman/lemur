using NAudio.Wave;

namespace Lemur.Audio;

public sealed record MixResult(Envelope Mic, Envelope Others, double MicGainDb, double OthersGainDb, double DurationSeconds);

/// <summary>
/// Turns the two recorded tracks into the single file the transcription engine gets, and
/// measures each track's loudness over time (which is how "me" gets named later).
/// Streams in one-second blocks: memory stays flat however long the meeting was.
/// </summary>
public static class Mixer
{
    public const double FrameSeconds = 0.05;

    /// <summary>Speech peaks in each track are brought to about this level before summing.</summary>
    public const double TargetSpeechDb = -18;

    /// <summary>A track whose loud moments stay below this has nothing in it worth amplifying.</summary>
    public const double SilentTrackDb = -60;

    public static Envelope MeasureEnvelope(string wavPath)
    {
        using var reader = new WaveFileReader(wavPath);
        var samples = reader.ToSampleProvider();
        int frame = (int)(reader.WaveFormat.SampleRate * FrameSeconds);
        var db = new List<float>();
        var block = new float[frame];
        while (true)
        {
            int read = ReadFully(samples, block);
            if (read == 0) break;
            double sum = 0;
            for (int i = 0; i < read; i++) sum += block[i] * block[i];
            db.Add((float)Envelope.ToDb(sum / read));
            if (read < frame) break;
        }
        return new Envelope(FrameSeconds, db.ToArray());
    }

    /// <summary>Gain (dB) that brings a track's speech peaks (95th-percentile frame) to the target.</summary>
    public static double GainFor(Envelope envelope)
    {
        double peak = envelope.Percentile(95);
        if (peak < SilentTrackDb) return 0;
        return Math.Clamp(TargetSpeechDb - peak, -12, 24);
    }

    public static MixResult MixTracks(string micWav, string othersWav, string outWav)
    {
        var micEnv = MeasureEnvelope(micWav);
        var othersEnv = MeasureEnvelope(othersWav);
        double micGainDb = GainFor(micEnv), othersGainDb = GainFor(othersEnv);
        float gm = (float)Math.Pow(10, micGainDb / 20), go = (float)Math.Pow(10, othersGainDb / 20);

        using var mic = new WaveFileReader(micWav);
        using var others = new WaveFileReader(othersWav);
        var micSamples = mic.ToSampleProvider();
        var othersSamples = others.ToSampleProvider();
        int rate = mic.WaveFormat.SampleRate;
        if (others.WaveFormat.SampleRate != rate || mic.WaveFormat.Channels != 1 || others.WaveFormat.Channels != 1)
            throw new InvalidDataException("tracks must both be mono at the same sample rate");

        using var writer = new WaveFileWriter(outWav, new WaveFormat(rate, 16, 1));
        var a = new float[rate];
        var b = new float[rate];
        var outBytes = new byte[rate * 2];
        long total = 0;
        while (true)
        {
            int na = ReadFully(micSamples, a);
            int nb = ReadFully(othersSamples, b);
            int n = Math.Max(na, nb);
            if (n == 0) break;
            for (int i = 0; i < n; i++)
            {
                float x = (i < na ? a[i] * gm : 0) + (i < nb ? b[i] * go : 0);
                short q = (short)Math.Round(SoftLimit(x) * 32767f);
                outBytes[2 * i] = (byte)q;
                outBytes[2 * i + 1] = (byte)(q >> 8);
            }
            writer.Write(outBytes, 0, n * 2);
            total += n;
            if (n < rate) break;
        }
        return new MixResult(micEnv, othersEnv, micGainDb, othersGainDb, total / (double)rate);
    }

    /// <summary>Linear below 0.9; above it, eases toward 1.0 instead of clipping.</summary>
    public static float SoftLimit(float x)
    {
        float ax = Math.Abs(x);
        if (ax <= 0.9f) return x;
        float limited = 0.9f + 0.1f * MathF.Tanh((ax - 0.9f) / 0.1f);
        return MathF.CopySign(limited, x);
    }

    static int ReadFully(ISampleProvider source, float[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = source.Read(buffer, total, buffer.Length - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
