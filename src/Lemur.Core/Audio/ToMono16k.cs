using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Lemur.Audio;

/// <summary>
/// Converts whatever a WASAPI device delivers (usually 48 kHz float, 2–8 channels) into
/// 16 kHz mono float — what both transcription engines want, at a third of the size.
/// Feed it raw capture buffers with <see cref="Add"/>, then drain with <see cref="Read"/>.
/// </summary>
public sealed class ToMono16k
{
    readonly BufferedWaveProvider _buffer;
    readonly ISampleProvider _chain;

    public ToMono16k(WaveFormat source)
    {
        _buffer = new BufferedWaveProvider(source)
        {
            ReadFully = false, // return only what is there; never invent silence
            BufferDuration = TimeSpan.FromSeconds(30),
            DiscardOnBufferOverflow = true,
        };
        ISampleProvider chain = _buffer.ToSampleProvider();
        if (chain.WaveFormat.Channels > 1) chain = new DownmixToMono(chain);
        if (chain.WaveFormat.SampleRate != AnchoredTrackWriter.SampleRate)
            chain = new WdlResamplingSampleProvider(chain, AnchoredTrackWriter.SampleRate);
        _chain = chain;
    }

    public void Add(byte[] data, int count) => _buffer.AddSamples(data, 0, count);

    /// <summary>Returns the number of 16 kHz mono samples written to <paramref name="into"/>; 0 when drained.</summary>
    public int Read(float[] into) => _chain.Read(into, 0, into.Length);
}

/// <summary>Averages all channels into one.</summary>
public sealed class DownmixToMono : ISampleProvider
{
    readonly ISampleProvider _source;
    readonly int _channels;
    float[] _scratch = [];

    public DownmixToMono(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int needed = count * _channels;
        if (_scratch.Length < needed) _scratch = new float[needed];
        int read = _source.Read(_scratch, 0, needed);
        int frames = read / _channels;
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            int baseIndex = f * _channels;
            for (int c = 0; c < _channels; c++) sum += _scratch[baseIndex + c];
            buffer[offset + f] = sum / _channels;
        }
        return frames;
    }
}
