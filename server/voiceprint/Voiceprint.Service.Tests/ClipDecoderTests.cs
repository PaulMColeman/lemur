using Voiceprint.Service.Audio;

namespace Voiceprint.Service.Tests;

public class ClipDecoderTests
{
    [Fact]
    public void Valid_clip_decodes_to_scaled_samples()
    {
        var data = new byte[] { 0x00, 0x40, 0x00, 0xC0, 0xFF, 0x7F }; // 16384, -16384, 32767
        var samples = ClipDecoder.Decode(TestWav.DataUrl(TestWav.Build(data)));

        Assert.Equal([0.5f, -0.5f, 32767 / 32768f], samples);
    }

    [Theory]
    [InlineData("audio/wav")]
    [InlineData("audio/x-wav")]
    [InlineData("audio/wave")]
    [InlineData("AUDIO/WAV")]
    public void Accepts_wav_media_types(string mediaType)
    {
        var samples = ClipDecoder.Decode(TestWav.DataUrl(TestWav.Build(new byte[3200]), mediaType));
        Assert.Equal(1600, samples.Length);
    }

    [Fact]
    public void Skips_chunks_before_data()
    {
        byte[] list = [.. "LIST"u8, 3, 0, 0, 0, 1, 2, 3, 0]; // odd size, padded to a word
        var samples = ClipDecoder.Decode(TestWav.DataUrl(TestWav.Build([0, 0x40], chunkBeforeData: list)));
        Assert.Equal([0.5f], samples);
    }

    [Fact]
    public void Wrong_sample_rate_is_rejected()
    {
        var ex = Reject(TestWav.DataUrl(TestWav.Build(new byte[3200], rate: 44_100)));
        Assert.Contains("44100 Hz", ex.Message);
        Assert.Contains("16000", ex.Message);
    }

    [Fact]
    public void Stereo_is_rejected()
    {
        var ex = Reject(TestWav.DataUrl(TestWav.Build(new byte[3200], channels: 2)));
        Assert.Contains("mono", ex.Message);
    }

    [Fact]
    public void Eight_bit_is_rejected()
    {
        var ex = Reject(TestWav.DataUrl(TestWav.Build(new byte[1600], bits: 8)));
        Assert.Contains("8-bit", ex.Message);
    }

    [Fact]
    public void Float_pcm_is_rejected()
    {
        var ex = Reject(TestWav.DataUrl(TestWav.Build(new byte[3200], bits: 32, tag: 3)));
        Assert.Contains("not integer PCM", ex.Message);
    }

    [Fact]
    public void Truncated_data_is_rejected()
    {
        var wav = TestWav.Build(new byte[3200]);
        var ex = Reject(TestWav.DataUrl(wav[..^100]));
        Assert.Contains("truncated", ex.Message);
    }

    [Fact]
    public void Truncated_header_is_rejected()
    {
        var wav = TestWav.Build(new byte[3200]);
        var ex = Reject(TestWav.DataUrl(wav[..30]));
        Assert.Contains("truncated", ex.Message);
    }

    [Fact]
    public void Non_wav_bytes_are_rejected()
    {
        var ex = Reject(TestWav.DataUrl("ID3 this is an mp3"u8.ToArray()));
        Assert.Contains("not a WAV", ex.Message);
    }

    [Fact]
    public void Not_base64_is_rejected()
    {
        var ex = Reject("data:audio/wav;base64,@@not base64@@");
        Assert.Contains("base64", ex.Message);
    }

    [Fact]
    public void Missing_base64_marker_is_rejected()
    {
        var ex = Reject("data:audio/wav,UklGRg==");
        Assert.Contains("base64-encoded", ex.Message);
    }

    [Theory]
    [InlineData("data:audio/mpeg;base64,AAAA")]
    [InlineData("data:text/plain;base64,AAAA")]
    public void Wrong_media_type_is_rejected(string input)
    {
        var ex = Reject(input);
        Assert.Contains("audio/wav", ex.Message);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("https://example.com/a.wav")]
    [InlineData("data:audio/wav;base64")]
    public void Not_a_data_url_is_rejected(string input)
    {
        Reject(input);
    }

    static ClipFormatException Reject(string input) =>
        Assert.Throws<ClipFormatException>(() => ClipDecoder.Decode(input));
}
