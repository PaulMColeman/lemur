using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Lemur.Speakers;

public sealed record SampleCheck(IReadOnlyList<float[]> Clips, float[] Mean, double SpeechSeconds, bool MaybeTwoVoices, string Model);

/// <summary>
/// "Add sample from file…" (design 7.3): someone talking alone, from a WAV, MP3 or M4A — a voice memo they
/// sent, or "Record a sample" for yourself. Needs at least 10 s of speech; warns when the pieces sound
/// like more than one person.
/// </summary>
public static class SampleImporter
{
    public const double MinSpeechSeconds = 10;

    /// <summary>Pieces this unlike their own average suggest a second voice in the file.</summary>
    public const double SameVoice = 0.5;

    /// <summary>The file as 16 kHz mono samples, whatever it was.</summary>
    public static short[] Load(string path)
    {
        using WaveStream reader = Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase)
            ? new WaveFileReader(path)
            : new MediaFoundationReader(path);
        ISampleProvider samples = reader.ToSampleProvider();
        if (samples.WaveFormat.Channels == 2) samples = new StereoToMonoSampleProvider(samples);
        else if (samples.WaveFormat.Channels > 2) throw new InvalidDataException("more than two channels");
        if (samples.WaveFormat.SampleRate != Wav.SampleRate) samples = new WdlResamplingSampleProvider(samples, Wav.SampleRate);

        var result = new List<short>();
        var buffer = new float[Wav.SampleRate];
        int n;
        while ((n = samples.Read(buffer, 0, buffer.Length)) > 0)
            for (int i = 0; i < n; i++) result.Add((short)Math.Round(Math.Clamp(buffer[i], -1f, 1f) * 32767));
        return [.. result];
    }

    /// <summary>
    /// Stretches louder than the file's quiet parts, cut into pieces of up to 6 s — a crude speech finder
    /// that is enough for "someone talking alone".
    /// </summary>
    public static IReadOnlyList<(int Start, int Length)> Pieces(short[] samples)
    {
        const int frame = Wav.SampleRate / 20; // 50 ms
        int frames = samples.Length / frame;
        if (frames == 0) return [];
        var db = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int i = f * frame; i < (f + 1) * frame; i++) sum += (double)samples[i] * samples[i];
            db[f] = 10 * Math.Log10(sum / frame / (32768.0 * 32768.0) + 1e-10);
        }
        double floor = db.Order().ElementAt(frames / 10);
        var pieces = new List<(int, int)>();
        int start = -1, quiet = 0;
        for (int f = 0; f <= frames; f++)
        {
            bool talking = f < frames && db[f] > floor + 10;
            if (talking) { if (start < 0) start = f; quiet = 0; }
            else if (start >= 0 && (++quiet > 6 || f == frames))
            {
                int end = f - quiet + 1;
                for (int a = start; a < end; a += 120) // 6 s pieces
                {
                    int len = Math.Min(120, end - a);
                    if (len >= 40) pieces.Add((a * frame, len * frame)); // at least 2 s
                }
                start = -1;
                quiet = 0;
            }
        }
        return pieces;
    }

    public static async Task<SampleCheck> CheckAsync(string path, IVoiceprintClient client, PeopleStore store, string modelName, CancellationToken ct)
    {
        var samples = Load(path);
        var pieces = Pieces(samples);
        double speech = pieces.Sum(p => p.Length) / (double)Wav.SampleRate;
        if (speech < MinSpeechSeconds)
            throw new InvalidDataException($"only {speech:0} s of speech found; at least {MinSpeechSeconds:0} s of them talking alone is needed");

        var wavs = pieces.Select(p => Wav.Encode(samples.AsSpan(p.Start, p.Length))).ToList();
        var calibration = (await client.EmbedAsync([Calibration.SignalWav()], ct))[0];
        var model = store.Models.Resolve(calibration, modelName, DateTimeOffset.Now);
        var vectors = await client.EmbedAsync(wavs, ct);
        var mean = Vectors.WeightedMean(vectors.Select((v, i) => (v, (double)pieces[i].Length)).ToList());
        bool two = vectors.Count >= 2 && vectors.Any(v => Vectors.Cosine(v, mean) < SameVoice);
        return new SampleCheck(vectors, mean, speech, two, model);
    }

    /// <summary>Stores the sample as a confirmed voiceprint with its first 10 s as the reference clip.</summary>
    public static bool Add(PeopleStore store, Person person, SampleCheck check, string path, VoiceKind kind, DateTimeOffset now)
    {
        var print = new Voiceprint
        {
            Id = "pr-" + Guid.NewGuid().ToString("N")[..8],
            Kind = kind,
            Model = check.Model,
            Vector = Vectors.ToBase64(check.Mean),
            Seconds = Math.Round(check.SpeechSeconds, 1),
            From = new PrintSource { Recording = "file", Title = Path.GetFileName(path), At = now },
            Confirmed = true,
            Added = now,
        };
        if (!person.Prints.Any(p => p.Kind == kind && p.Model == check.Model && p.Clip is not null))
        {
            var samples = Load(path);
            var first = Pieces(samples).Take(2).SelectMany(p => samples.Skip(p.Start).Take(p.Length)).Take(10 * Wav.SampleRate).ToArray();
            print.Clip = store.SaveClip(person.Id, print.Id, Wav.Encode(first));
        }
        bool kept = PeopleStore.AddPrint(person, print);
        store.Save(person, now);
        return kept;
    }
}
