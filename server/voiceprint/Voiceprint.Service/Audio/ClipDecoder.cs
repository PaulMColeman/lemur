using System.Buffers.Binary;

namespace Voiceprint.Service.Audio;

/// <summary>
/// One <c>input</c> string → samples. Only 16 kHz mono 16-bit PCM WAV is accepted: resampling here
/// would hide a client bug and make the same voice embed differently depending on who cut the clip.
/// </summary>
public static class ClipDecoder
{
    public const int SampleRate = 16_000;
    const string Expected = "send 16 kHz mono 16-bit PCM WAV";
    static readonly string[] WavMediaTypes = ["audio/wav", "audio/x-wav", "audio/wave"];

    public static float[] Decode(string dataUrl) => DecodeWav(DecodeDataUrl(dataUrl).Span);

    public static ReadOnlyMemory<byte> DecodeDataUrl(string s)
    {
        if (!s.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            throw new ClipFormatException("expected a data URL: data:audio/wav;base64,...");
        var comma = s.IndexOf(',');
        if (comma < 0)
            throw new ClipFormatException("data URL has no ',' before its payload");

        var meta = s[5..comma].Split(';', StringSplitOptions.TrimEntries);
        if (!WavMediaTypes.Contains(meta[0], StringComparer.OrdinalIgnoreCase))
            throw new ClipFormatException($"media type \"{Short(meta[0])}\" is not WAV; use audio/wav");
        if (!meta.Skip(1).Contains("base64", StringComparer.OrdinalIgnoreCase))
            throw new ClipFormatException("data URL must be base64-encoded (data:audio/wav;base64,...)");

        var payload = s.AsSpan(comma + 1);
        var bytes = new byte[(payload.Length + 3) / 4 * 3];
        if (!Convert.TryFromBase64Chars(payload, bytes, out var n))
            throw new ClipFormatException("payload is not valid base64");
        return bytes.AsMemory(0, n);
    }

    public static float[] DecodeWav(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav[8..12].SequenceEqual("WAVE"u8))
            throw new ClipFormatException($"not a WAV file (no RIFF/WAVE header); {Expected}");

        WavFormat? fmt = null;
        ReadOnlySpan<byte> data = default;
        var haveData = false;
        var pos = 12;
        while (pos + 8 <= wav.Length && !(fmt is not null && haveData))
        {
            var id = wav.Slice(pos, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(wav[(pos + 4)..]);
            var body = pos + 8;
            var available = wav.Length - body;

            if (id.SequenceEqual("data"u8))
            {
                // 0xFFFFFFFF = size unknown (written as a stream): take what is there.
                if (size != uint.MaxValue && size > available)
                    throw new ClipFormatException($"WAV is truncated: data chunk declares {size} bytes, {available} present");
                var length = size == uint.MaxValue ? available : (int)size;
                data = wav.Slice(body, length);
                haveData = true;
                pos = body + length + (length & 1);
                continue;
            }

            if (size > available)
                throw new ClipFormatException("WAV is truncated: a header chunk runs past the end");
            if (id.SequenceEqual("fmt "u8))
                fmt = WavFormat.Parse(wav.Slice(body, (int)size));
            pos = body + (int)size + ((int)size & 1); // chunks are word-aligned
        }

        if (fmt is not { } f)
            throw new ClipFormatException($"WAV is truncated or has no fmt chunk; {Expected}");
        if (!haveData)
            throw new ClipFormatException("WAV is truncated or has no data chunk");

        if (f.Tag != 1)
            throw new ClipFormatException($"audio is not integer PCM (WAV format tag {f.Tag}); {Expected}");
        if (f.Bits != 16)
            throw new ClipFormatException($"audio is {f.Bits}-bit; {Expected}");
        if (f.Channels != 1)
            throw new ClipFormatException($"audio has {f.Channels} channels, not mono; {Expected}");
        if (f.SampleRate != SampleRate)
            throw new ClipFormatException($"sample rate is {f.SampleRate} Hz, not {SampleRate} Hz; {Expected}");

        var samples = new float[data.Length / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(data[(i * 2)..]) / 32768f;
        return samples;
    }

    static string Short(string s) => s.Length <= 40 ? s : s[..40] + "…";

    readonly record struct WavFormat(int Tag, int Channels, long SampleRate, int Bits)
    {
        public static WavFormat Parse(ReadOnlySpan<byte> c)
        {
            if (c.Length < 16) throw new ClipFormatException("WAV fmt chunk is too short");
            int tag = BinaryPrimitives.ReadUInt16LittleEndian(c);
            // WAVE_FORMAT_EXTENSIBLE keeps the real format in the first two bytes of its sub-format GUID.
            if (tag == 0xFFFE && c.Length >= 26) tag = BinaryPrimitives.ReadUInt16LittleEndian(c[24..]);
            return new WavFormat(
                tag,
                BinaryPrimitives.ReadUInt16LittleEndian(c[2..]),
                BinaryPrimitives.ReadUInt32LittleEndian(c[4..]),
                BinaryPrimitives.ReadUInt16LittleEndian(c[14..]));
        }
    }
}

/// <summary>A clip the service can't use; the message is safe to return to the client.</summary>
public sealed class ClipFormatException(string message) : Exception(message);
