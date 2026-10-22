using System.Text;

namespace Lemur.Audio;

/// <summary>
/// A WAV whose writer never closed (the app was killed mid-recording, or the PC lost power) still has the sizes
/// it was created with — zero — so readers see no audio. The samples are all there: this sets the sizes from the
/// file's length.
/// </summary>
public static class WavRepair
{
    /// <returns>True if the header needed fixing.</returns>
    public static bool FixSizes(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var reader = new BinaryReader(fs, Encoding.ASCII, leaveOpen: true);
        using var writer = new BinaryWriter(fs, Encoding.ASCII, leaveOpen: true);
        if (fs.Length < 12 || Id(reader) != "RIFF") throw new InvalidDataException($"{Path.GetFileName(path)} is not a WAV file");
        uint riffSize = reader.ReadUInt32();
        if (Id(reader) != "WAVE") throw new InvalidDataException($"{Path.GetFileName(path)} is not a WAV file");

        int blockAlign = 1;
        while (fs.Position + 8 <= fs.Length)
        {
            var id = Id(reader);
            uint size = reader.ReadUInt32();
            long start = fs.Position;
            if (id == "data")
            {
                long available = fs.Length - start;
                available -= available % blockAlign;
                bool changed = size != available || riffSize != fs.Length - 8;
                if (changed)
                {
                    fs.Position = 4;
                    writer.Write((uint)Math.Min(uint.MaxValue, fs.Length - 8));
                    fs.Position = start - 4;
                    writer.Write((uint)Math.Min(uint.MaxValue, available));
                }
                return changed;
            }
            if (id == "fmt " && size >= 14)
            {
                fs.Position = start + 12;
                blockAlign = Math.Max(1, (int)reader.ReadUInt16());
            }
            fs.Position = start + size + (size & 1);
        }
        throw new InvalidDataException($"{Path.GetFileName(path)} has no audio data");
    }

    static string Id(BinaryReader reader) => new(reader.ReadChars(4));
}
