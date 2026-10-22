using NAudio.Wave;

namespace Lemur.Audio;

public interface ISampleSink : IDisposable
{
    void Write(ReadOnlySpan<float> samples);
    void WriteSilence(long count);
}

/// <summary>16 kHz mono 16-bit PCM WAV.</summary>
public sealed class WavSampleSink : ISampleSink
{
    readonly WaveFileWriter _writer;
    readonly byte[] _buffer = new byte[AnchoredTrackWriter.SampleRate * 2];

    public WavSampleSink(string path) =>
        _writer = new WaveFileWriter(path, new WaveFormat(AnchoredTrackWriter.SampleRate, 16, 1));

    public void Write(ReadOnlySpan<float> samples)
    {
        int i = 0;
        while (i < samples.Length)
        {
            int n = Math.Min(samples.Length - i, _buffer.Length / 2);
            for (int k = 0; k < n; k++)
            {
                short q = (short)Math.Round(Math.Clamp(samples[i + k], -1f, 1f) * 32767f);
                _buffer[2 * k] = (byte)q;
                _buffer[2 * k + 1] = (byte)(q >> 8);
            }
            _writer.Write(_buffer, 0, n * 2);
            i += n;
        }
    }

    public void WriteSilence(long count)
    {
        Array.Clear(_buffer);
        while (count > 0)
        {
            int n = (int)Math.Min(count, _buffer.Length / 2);
            _writer.Write(_buffer, 0, n * 2);
            count -= n;
        }
    }

    public void Dispose() => _writer.Dispose();
}

/// <summary>
/// Keeps a track on the wall clock. Each buffer arrives with the clock time at which it was
/// delivered; the writer pads silence when the track falls behind (a device switch, or loopback
/// that stalled because nothing was playing) and trims when it runs ahead (device clock drift),
/// so the mic and "everyone else" tracks stay aligned to within <see cref="ToleranceSeconds"/>.
/// Small jitter is smoothed out rather than corrected, so steady streams pass through untouched.
/// </summary>
public sealed class AnchoredTrackWriter : IDisposable
{
    public const int SampleRate = 16000;

    readonly ISampleSink _sink;
    readonly object _gate = new();
    long _written;
    double _smoothedDrift;
    bool _first = true;

    public AnchoredTrackWriter(ISampleSink sink) => _sink = sink;

    /// <summary>Drift (smoothed) beyond this is corrected by padding or trimming.</summary>
    public double ToleranceSeconds { get; init; } = 0.15;

    /// <summary>A single buffer this far behind is a real gap and is padded immediately.</summary>
    public double GapSeconds { get; init; } = 0.5;

    /// <summary>Weight of each new drift reading in the running average.</summary>
    public double Smoothing { get; init; } = 0.05;

    public long Written { get { lock (_gate) return _written; } }
    public long PaddedSamples { get; private set; }
    public long TrimmedSamples { get; private set; }

    /// <param name="samples">16 kHz mono samples.</param>
    /// <param name="arrival">Recording-clock time when this buffer was delivered (≈ capture time of its last sample).</param>
    public void Append(ReadOnlySpan<float> samples, TimeSpan arrival)
    {
        lock (_gate)
        {
            long expectedEnd = (long)Math.Round(arrival.TotalSeconds * SampleRate);
            long drift = expectedEnd - (_written + samples.Length); // > 0: behind the clock

            if (_first)
            {
                // Start-up latency differs per device: line both tracks up on the shared t0.
                _first = false;
                if (drift > 0) Pad(drift);
                else if (drift < 0) samples = Trim(samples, -drift);
            }
            else if (drift > GapSeconds * SampleRate)
            {
                Pad(drift);
                _smoothedDrift = 0;
            }
            else
            {
                _smoothedDrift += Smoothing * (drift - _smoothedDrift);
                long tolerance = (long)(ToleranceSeconds * SampleRate);
                if (_smoothedDrift > tolerance)
                {
                    Pad((long)_smoothedDrift);
                    _smoothedDrift = 0;
                }
                else if (_smoothedDrift < -tolerance)
                {
                    samples = Trim(samples, (long)-_smoothedDrift);
                    _smoothedDrift = 0;
                }
            }

            _sink.Write(samples);
            _written += samples.Length;
        }
    }

    /// <summary>Pads with silence up to <paramref name="totalSamples"/> (used at stop so both tracks end together).</summary>
    public void PadTo(long totalSamples)
    {
        lock (_gate)
        {
            if (totalSamples > _written) Pad(totalSamples - _written);
        }
    }

    void Pad(long count)
    {
        _sink.WriteSilence(count);
        _written += count;
        PaddedSamples += count;
    }

    ReadOnlySpan<float> Trim(ReadOnlySpan<float> samples, long count)
    {
        int k = (int)Math.Min(count, samples.Length);
        TrimmedSamples += k;
        return samples[k..];
    }

    public void Dispose()
    {
        lock (_gate) _sink.Dispose();
    }
}
