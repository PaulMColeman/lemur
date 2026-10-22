using SherpaOnnx;
using Voiceprint.Service.Audio;

namespace Voiceprint.Service.Embedding;

/// <summary>
/// sherpa-onnx speaker-embedding extractor. One instance behind a lock: ONNX Runtime already spreads
/// each clip over <c>VOICEPRINT_THREADS</c> cores, and a second copy would double the model's memory.
/// </summary>
public sealed class SherpaEmbedder : IEmbedder, IDisposable
{
    readonly SpeakerEmbeddingExtractor _extractor;
    readonly Lock _gate = new();

    public int Dim { get; }
    public string ModelId { get; }

    SherpaEmbedder(SpeakerEmbeddingExtractor extractor, string modelId)
    {
        _extractor = extractor;
        ModelId = modelId;
        Dim = extractor.Dim;
    }

    public static SherpaEmbedder Create(VoiceprintOptions o)
    {
        if (o.ModelPath is null)
            throw new ConfigException("VOICEPRINT_MODEL_PATH is not set (path to a sherpa-onnx speaker model .onnx).");
        // The native constructor returns a null handle for a missing file and Dim then crashes the process.
        if (!File.Exists(o.ModelPath))
            throw new ConfigException($"VOICEPRINT_MODEL_PATH: no file at {o.ModelPath}");

        var config = new SpeakerEmbeddingExtractorConfig
        {
            Model = o.ModelPath,
            NumThreads = o.Threads,
            Provider = o.Provider,
            Debug = 0,
        };
        var id = o.ModelId ?? Path.GetFileNameWithoutExtension(o.ModelPath);
        return new SherpaEmbedder(new SpeakerEmbeddingExtractor(config), id);
    }

    public float[][] Embed(IReadOnlyList<float[]> clips16k)
    {
        var result = new float[clips16k.Count][];
        for (var i = 0; i < result.Length; i++)
            result[i] = EmbedOne(clips16k[i]);
        return result;
    }

    // Locked per clip, not per request, so a 64-clip batch doesn't starve a concurrent 1-clip call.
    float[] EmbedOne(float[] samples)
    {
        lock (_gate)
        {
            using var stream = _extractor.CreateStream();
            stream.AcceptWaveform(ClipDecoder.SampleRate, samples);
            stream.InputFinished();
            if (!_extractor.IsReady(stream))
                throw new InvalidOperationException("clip is too short for the model");
            return _extractor.Compute(stream);
        }
    }

    public void Dispose()
    {
        lock (_gate) _extractor.Dispose();
    }
}
