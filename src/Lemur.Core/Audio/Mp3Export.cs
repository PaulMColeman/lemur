using NAudio.MediaFoundation;
using NAudio.Wave;

namespace Lemur.Audio;

/// <summary>
/// The meeting's mix as a small MP3 kept beside its note, so the note's page can play any moment back.
/// Uses Windows' own encoder (Media Foundation — missing only on "N" editions without the Media Feature
/// Pack). The mix is already 16 kHz mono, which the encoder takes as is: at 32 kbps that is about 14 MB
/// an hour, 4 s to encode 40 minutes, and the audio lands 40–60 ms after the transcript's times.
/// </summary>
public static class Mp3Export
{
    public const int BitRate = 32_000;

    /// <summary>Writes <paramref name="mp3Path"/> whole or not at all: it encodes to a temporary file first.</summary>
    public static void Encode(string wavPath, string mp3Path)
    {
        var tmp = mp3Path + ".tmp";
        try
        {
            MediaFoundationApi.Startup();
            using (var reader = new WaveFileReader(wavPath))
            using (var output = File.Create(tmp))
                MediaFoundationEncoder.EncodeToMp3(reader, output, BitRate);
            File.Move(tmp, mp3Path);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
