using Microsoft.AspNetCore.Server.Kestrel.Core;
using Voiceprint.Service;
using Voiceprint.Service.Api;
using Voiceprint.Service.Embedding;

var builder = WebApplication.CreateBuilder(args);

// One line per request comes from RequestLog; the framework's own request lines add nothing.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});

// Options and the model are resolved lazily so tests can swap configuration and the embedder.
builder.Services.AddSingleton(sp => VoiceprintOptions.From(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<IEmbedder>(sp => SherpaEmbedder.Create(sp.GetRequiredService<VoiceprintOptions>()));
builder.Services.AddOptions<KestrelServerOptions>()
    .Configure<VoiceprintOptions>((kestrel, o) => kestrel.Limits.MaxRequestBodySize = o.MaxBodyBytes);

var app = builder.Build();

IEmbedder embedder;
VoiceprintOptions options;
try
{
    options = app.Services.GetRequiredService<VoiceprintOptions>();
    embedder = app.Services.GetRequiredService<IEmbedder>(); // load the model now, not on the first request
}
catch (ConfigException ex)
{
    app.Logger.LogCritical("{Problem}", ex.Message);
    return 1;
}
app.Logger.LogInformation("Model {Model} loaded: dim {Dim}, {Threads} threads, provider {Provider}, auth {Auth}",
    embedder.ModelId, embedder.Dim, options.Threads, options.Provider, options.ApiKey is null ? "off" : "on");

app.UseMiddleware<RequestLog>();
app.UseMiddleware<ApiKeyAuth>();
app.MapVoiceprintApi();
app.Run();
return 0;

public partial class Program;
