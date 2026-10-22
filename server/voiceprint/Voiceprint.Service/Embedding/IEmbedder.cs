namespace Voiceprint.Service.Embedding;

/// <summary>A speaker model. Seam for tests: the API never touches sherpa-onnx directly.</summary>
public interface IEmbedder
{
    int Dim { get; }
    string ModelId { get; }

    /// <summary>One raw (not normalised) vector of <see cref="Dim"/> floats per 16 kHz mono clip.</summary>
    float[][] Embed(IReadOnlyList<float[]> clips16k);
}
