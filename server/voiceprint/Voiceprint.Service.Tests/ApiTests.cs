using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Voiceprint.Service.Tests;

public class ApiTests
{
    [Fact]
    public async Task Single_string_input_returns_one_unit_vector()
    {
        using var app = new TestApp(embedder: new FakeEmbedder());

        var (status, json) = await Post(app, new { model = "anything", input = TestWav.Clip(1.5) });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("list", (string?)json["object"]);
        Assert.Equal("fake-model", (string?)json["model"]);
        var item = json["data"]![0]!;
        Assert.Equal("embedding", (string?)item["object"]);
        Assert.Equal(0, (int)item["index"]!);
        var vector = item["embedding"]!.AsArray().Select(x => (float)x!).ToArray();
        Assert.Equal(4, vector.Length);
        AssertUnit(vector);
        Assert.Equal(2, (int)json["usage"]!["prompt_tokens"]!); // 1.5 s → 2
        Assert.Equal(2, (int)json["usage"]!["total_tokens"]!);
    }

    [Fact]
    public async Task Array_input_returns_vectors_in_order_and_rounds_total_seconds_up()
    {
        using var app = new TestApp(embedder: new FakeEmbedder());

        var (status, json) = await Post(app, new
        {
            model = "x",
            input = new[] { TestWav.Clip(1.2), TestWav.Clip(1.2), TestWav.Clip(2.0) },
        });

        Assert.Equal(HttpStatusCode.OK, status);
        var data = json["data"]!.AsArray();
        Assert.Equal([0, 1, 2], data.Select(d => (int)d!["index"]!));
        Assert.Equal(5, (int)json["usage"]!["prompt_tokens"]!); // 4.4 s → 5
        // The fake's first component grows with clip length, so order is visible after normalising.
        Assert.True((float)data[2]!["embedding"]![0]! > (float)data[0]!["embedding"]![0]!);
    }

    [Fact]
    public async Task Base64_encoding_returns_little_endian_float32_of_the_same_vector()
    {
        using var app = new TestApp(embedder: new FakeEmbedder());
        var clip = TestWav.Clip(1.0);

        var (_, asFloats) = await Post(app, new { model = "x", input = clip });
        var (status, asBase64) = await Post(app, new { model = "x", input = clip, encoding_format = "base64" });

        Assert.Equal(HttpStatusCode.OK, status);
        var bytes = Convert.FromBase64String((string)asBase64["data"]![0]!["embedding"]!);
        var decoded = Enumerable.Range(0, bytes.Length / 4).Select(i => BitConverter.ToSingle(bytes, i * 4)).ToArray();
        var floats = asFloats["data"]![0]!["embedding"]!.AsArray().Select(x => (float)x!).ToArray();
        Assert.Equal(floats, decoded);
        AssertUnit(decoded);
    }

    [Fact]
    public async Task Unknown_encoding_format_is_rejected()
    {
        using var app = new TestApp(embedder: new FakeEmbedder());
        var (status, json) = await Post(app, new { input = TestWav.Clip(1), encoding_format = "int8" });
        AssertError(status, json, HttpStatusCode.BadRequest, "encoding_format");
    }

    [Fact]
    public async Task Bad_clip_is_named_by_index_and_nothing_is_embedded()
    {
        var fake = new FakeEmbedder();
        using var app = new TestApp(embedder: fake);
        var stereo = TestWav.DataUrl(TestWav.Build(new byte[6400], channels: 2));

        var (status, json) = await Post(app, new { input = new[] { TestWav.Clip(1), TestWav.Clip(1), stereo } });

        AssertError(status, json, HttpStatusCode.BadRequest, "input[2]");
        Assert.Contains("mono", (string?)json["error"]!["message"]);
        Assert.Equal(0, fake.Calls);
    }

    [Theory]
    [InlineData("""{"model":"x"}""", "'input' is required")]
    [InlineData("""{"input":[]}""", "empty")]
    [InlineData("""{"input":[[1,2,3]]}""", "input[0]")]
    [InlineData("""{"input":42}""", "string or an array")]
    [InlineData("""[1,2]""", "JSON object")]
    [InlineData("""{"input": """, "not valid JSON")]
    public async Task Malformed_bodies_get_a_json_error(string body, string expected)
    {
        using var app = new TestApp(embedder: new FakeEmbedder());
        var (status, json) = await PostRaw(app, body);
        AssertError(status, json, HttpStatusCode.BadRequest, expected);
    }

    [Fact]
    public async Task Too_many_clips_is_rejected()
    {
        using var app = new TestApp(new() { ["VOICEPRINT_MAX_CLIPS"] = "3" }, new FakeEmbedder());
        var (status, json) = await Post(app, new { input = Enumerable.Repeat(TestWav.Clip(0.6), 4).ToArray() });
        AssertError(status, json, HttpStatusCode.BadRequest, "max 3");
    }

    [Theory]
    [InlineData(0.3)]
    [InlineData(15.5)]
    public async Task Clips_outside_the_length_limits_are_rejected(double seconds)
    {
        using var app = new TestApp(embedder: new FakeEmbedder());
        var (status, json) = await Post(app, new { input = new[] { TestWav.Clip(1), TestWav.Clip(seconds) } });
        AssertError(status, json, HttpStatusCode.BadRequest, "input[1]");
        Assert.Contains("0.5-15 s", (string?)json["error"]!["message"]);
    }

    [Fact]
    public async Task Clip_limits_come_from_the_environment()
    {
        using var app = new TestApp(new() { ["VOICEPRINT_MAX_CLIP_SECONDS"] = "2" }, new FakeEmbedder());
        var (status, json) = await Post(app, new { input = TestWav.Clip(3) });
        AssertError(status, json, HttpStatusCode.BadRequest, "0.5-2 s");
    }

    [Fact]
    public async Task Oversized_body_gets_413_with_a_json_error()
    {
        using var app = new TestApp(new() { ["VOICEPRINT_MAX_BODY_BYTES"] = "20000" }, new FakeEmbedder());
        var (status, json) = await Post(app, new { input = TestWav.Clip(1) }); // ~43 KB as base64
        AssertError(status, json, HttpStatusCode.RequestEntityTooLarge, "20000 bytes");
    }

    [Fact]
    public async Task Oversized_body_without_content_length_gets_413()
    {
        using var app = new TestApp(new() { ["VOICEPRINT_MAX_BODY_BYTES"] = "20000" }, new FakeEmbedder());
        var body = JsonSerializer.SerializeToUtf8Bytes(new { input = TestWav.Clip(1) });
        using var content = new StreamContent(new UnsizedStream(body));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var response = await app.Client.PostAsync("/v1/embeddings", content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("invalid_request_error", (string?)(await ReadJson(response))["error"]!["type"]);
    }

    [Fact]
    public async Task Auth_off_lets_v1_through()
    {
        using var app = new TestApp(embedder: new FakeEmbedder());
        var response = await app.Client.GetAsync("/v1/models");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer wrong-key")]
    [InlineData("Bearer s3cret-key-extra")]
    [InlineData("Basic czNjcmV0LWtleQ==")]
    public async Task Auth_on_rejects_missing_or_wrong_keys(string? authorization)
    {
        using var app = new TestApp(new() { ["VOICEPRINT_API_KEY"] = "s3cret-key" }, new FakeEmbedder());
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/embeddings")
        {
            Content = JsonContent.Create(new { input = TestWav.Clip(1) }),
        };
        if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);

        var response = await app.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var error = (await ReadJson(response))["error"]!;
        Assert.Equal("invalid_request_error", (string?)error["type"]);
        Assert.DoesNotContain("s3cret", error.ToJsonString());
    }

    [Fact]
    public async Task Auth_on_accepts_the_right_key_and_leaves_healthz_open()
    {
        using var app = new TestApp(new() { ["VOICEPRINT_API_KEY"] = "s3cret-key" }, new FakeEmbedder());
        app.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "s3cret-key");

        var (status, _) = await Post(app, new { input = TestWav.Clip(1) });
        Assert.Equal(HttpStatusCode.OK, status);

        app.Client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.OK, (await app.Client.GetAsync("/healthz")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client.GetAsync("/v1/models")).StatusCode);
    }

    [Fact]
    public async Task Healthz_reports_model_and_dim()
    {
        using var app = new TestApp(embedder: new FakeEmbedder());
        var json = await ReadJson(await app.Client.GetAsync("/healthz"));
        Assert.True((bool)json["ok"]!);
        Assert.Equal("fake-model", (string?)json["model"]);
        Assert.Equal(4, (int)json["dim"]!);
    }

    [Fact]
    public async Task Models_lists_the_one_model()
    {
        using var app = new TestApp(embedder: new FakeEmbedder());
        var json = await ReadJson(await app.Client.GetAsync("/v1/models"));
        Assert.Equal("list", (string?)json["object"]);
        var model = Assert.Single(json["data"]!.AsArray())!;
        Assert.Equal("fake-model", (string?)model["id"]);
        Assert.Equal("model", (string?)model["object"]);
        Assert.Equal("voiceprint", (string?)model["owned_by"]);
    }

    static async Task<(HttpStatusCode, JsonNode)> Post(TestApp app, object body)
    {
        var response = await app.Client.PostAsJsonAsync("/v1/embeddings", body);
        return (response.StatusCode, await ReadJson(response));
    }

    static async Task<(HttpStatusCode, JsonNode)> PostRaw(TestApp app, string body)
    {
        var response = await app.Client.PostAsync("/v1/embeddings",
            new StringContent(body, Encoding.UTF8, "application/json"));
        return (response.StatusCode, await ReadJson(response));
    }

    static async Task<JsonNode> ReadJson(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    static void AssertError(HttpStatusCode status, JsonNode json, HttpStatusCode expected, string messagePart)
    {
        Assert.Equal(expected, status);
        Assert.Equal("invalid_request_error", (string?)json["error"]!["type"]);
        Assert.Contains(messagePart, (string?)json["error"]!["message"]);
    }

    static void AssertUnit(float[] v) =>
        Assert.Equal(1.0, Math.Sqrt(v.Sum(x => (double)x * x)), precision: 5);

    /// <summary>A body with no Content-Length, as with chunked uploads.</summary>
    sealed class UnsizedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
