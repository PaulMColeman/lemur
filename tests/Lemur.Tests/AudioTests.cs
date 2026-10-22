using NAudio.Wave;
using Lemur.Audio;

namespace Lemur.Tests;

sealed class CountingSink : ISampleSink
{
    public readonly List<float> Samples = [];
    public void Write(ReadOnlySpan<float> samples) => Samples.AddRange(samples.ToArray());
    public void WriteSilence(long count) => Samples.AddRange(new float[count]);
    public void Dispose() { }
}

public class AnchoredTrackWriterTests
{
    const int Rate = AnchoredTrackWriter.SampleRate;
    static float[] Block(int n, float value = 0.5f) => Enumerable.Repeat(value, n).ToArray();
    static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void First_buffer_is_placed_at_its_clock_time()
    {
        var sink = new CountingSink();
        var w = new AnchoredTrackWriter(sink);
        w.Append(Block(1600), At(0.3)); // 100 ms of audio arriving at 300 ms → 200 ms of start-up latency
        Assert.Equal(Rate * 3 / 10, w.Written);
        Assert.Equal(3200, w.PaddedSamples);
        Assert.Equal(0f, sink.Samples[0]);
        Assert.Equal(0.5f, sink.Samples[^1]);
    }

    [Fact]
    public void Steady_stream_with_jitter_is_left_alone()
    {
        var w = new AnchoredTrackWriter(new CountingSink());
        var rng = new Random(1);
        // The first buffer can't arrive before its own audio was captured, so it lands on time;
        // after that, 60 s of 100 ms buffers are delivered up to ±40 ms late/early.
        w.Append(Block(1600), At(0.1));
        for (int i = 2; i <= 600; i++)
            w.Append(Block(1600), At(i * 0.1 + (rng.NextDouble() - 0.5) * 0.08));
        Assert.Equal(0, w.TrimmedSamples);
        Assert.True(w.PaddedSamples < Rate * 0.05, $"padded {w.PaddedSamples}");
    }

    [Fact]
    public void A_stall_is_padded_so_later_audio_lands_at_the_right_time()
    {
        var w = new AnchoredTrackWriter(new CountingSink());
        for (int i = 1; i <= 10; i++) w.Append(Block(1600), At(i * 0.1));
        // Loopback delivered nothing for 3 s (nothing was playing), then resumes.
        w.Append(Block(1600), At(4.1));
        Assert.InRange(w.Written, (long)(4.1 * Rate) - 160, (long)(4.1 * Rate) + 160);
    }

    [Fact]
    public void A_fast_device_clock_is_trimmed_back_to_the_wall_clock()
    {
        var w = new AnchoredTrackWriter(new CountingSink());
        // The device delivers 0.5 % more audio than real time: 3 s ahead after 10 minutes, uncorrected.
        for (int i = 1; i <= 6000; i++) w.Append(Block(1608), At(i * 0.1));
        Assert.True(w.TrimmedSamples > 0);
        double driftSeconds = (w.Written - 600.0 * Rate) / Rate;
        Assert.InRange(driftSeconds, -0.2, 0.2);
    }

    [Fact]
    public void PadTo_makes_tracks_end_together()
    {
        var w = new AnchoredTrackWriter(new CountingSink());
        w.Append(Block(1600), At(0.1));
        w.PadTo(Rate * 2);
        Assert.Equal(Rate * 2, w.Written);
    }
}

public class EnvelopeAndMixerTests
{
    [Fact]
    public void SoftLimit_is_linear_below_0_9_and_never_reaches_1()
    {
        Assert.Equal(0.5f, Mixer.SoftLimit(0.5f));
        Assert.Equal(-0.5f, Mixer.SoftLimit(-0.5f));
        Assert.InRange(Mixer.SoftLimit(5f), 0.9f, 1.0f);
        Assert.InRange(Mixer.SoftLimit(-5f), -1.0f, -0.9f);
    }

    [Fact]
    public void Gain_lifts_quiet_speech_and_leaves_silence_alone()
    {
        var quiet = new Envelope(0.05, Enumerable.Repeat(-40f, 100).ToArray());
        Assert.Equal(22, Mixer.GainFor(quiet), 3);
        var silent = new Envelope(0.05, Enumerable.Repeat(-90f, 100).ToArray());
        Assert.Equal(0, Mixer.GainFor(silent));
    }

    [Fact]
    public void MeanDb_averages_power_not_decibels()
    {
        var env = new Envelope(0.5, [-20f, -60f]); // one loud frame, one near-silent
        Assert.InRange(env.MeanDb(0, 1.0), -23.1, -22.9); // ≈ -20 - 3 dB, not -40
    }

    [Fact]
    public void Mixing_two_tracks_measures_both_and_writes_one_file()
    {
        var dir = Directory.CreateTempSubdirectory("llmmr-mix").FullName;
        try
        {
            string mic = Path.Combine(dir, "mic.wav"), others = Path.Combine(dir, "others.wav"), mix = Path.Combine(dir, "mix.wav");
            WriteTone(mic, seconds: 2, amplitude: 0.05f, startAt: 0, length: 1);     // you, first second
            WriteTone(others, seconds: 2, amplitude: 0.3f, startAt: 1, length: 1);   // them, second second
            var result = Mixer.MixTracks(mic, others, mix);
            Assert.Equal(2, result.DurationSeconds, 2);
            Assert.True(result.MicGainDb > result.OthersGainDb); // the quieter track is lifted more
            Assert.True(result.Mic.MeanDb(0, 1) > result.Mic.MeanDb(1, 2) + 40);
            using var reader = new WaveFileReader(mix);
            Assert.Equal(16000, reader.WaveFormat.SampleRate);
            Assert.Equal(1, reader.WaveFormat.Channels);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    static void WriteTone(string path, double seconds, float amplitude, double startAt, double length)
    {
        int rate = AnchoredTrackWriter.SampleRate;
        var s = new float[(int)(seconds * rate)];
        for (int i = (int)(startAt * rate); i < (int)((startAt + length) * rate); i++)
            s[i] = amplitude * MathF.Sin(2 * MathF.PI * 440 * i / rate);
        using var sink = new WavSampleSink(path);
        sink.Write(s);
    }
}
