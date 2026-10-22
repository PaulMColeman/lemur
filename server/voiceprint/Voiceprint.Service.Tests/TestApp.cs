using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Voiceprint.Service.Embedding;

namespace Voiceprint.Service.Tests;

/// <summary>The real app on a TestServer. Pass no embedder to load the real model from settings.</summary>
sealed class TestApp : IDisposable
{
    readonly WebApplicationFactory<Program> _factory;
    public HttpClient Client { get; }

    public TestApp(Dictionary<string, string?>? settings = null, IEmbedder? embedder = null)
    {
        // Blank first, so a VOICEPRINT_* variable in the developer's environment can't change a test.
        var config = new Dictionary<string, string?>
        {
            ["VOICEPRINT_API_KEY"] = "",
            ["VOICEPRINT_MODEL_PATH"] = "",
        };
        foreach (var (k, v) in settings ?? []) config[k] = v;

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(config));
            if (embedder is not null)
                b.ConfigureTestServices(s => s.AddSingleton(embedder));
        });
        Client = _factory.CreateClient();
    }

    public void Dispose()
    {
        Client.Dispose();
        _factory.Dispose();
    }
}

/// <summary>Deterministic, deliberately not normalised, so the tests see the service normalise.</summary>
sealed class FakeEmbedder : IEmbedder
{
    public int Dim => 4;
    public string ModelId => "fake-model";
    public int Calls { get; private set; }

    public float[][] Embed(IReadOnlyList<float[]> clips16k)
    {
        Calls++;
        return clips16k.Select(c => new[] { c.Length / 1000f, 1f, 2f, 3f }).ToArray();
    }
}
