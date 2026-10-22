using System.Net;
using System.Text;
using Lemur.Backends;
using Lemur.Config;

namespace Lemur.Tests;

/// <summary>Records requests and answers from a route table.</summary>
sealed class FakeServer : HttpMessageHandler
{
    readonly Func<HttpRequestMessage, string, (HttpStatusCode, string)> _route;
    public readonly List<(string Method, string Path, string Body, string? ApiKey, string? Bearer)> Requests = [];

    public FakeServer(Func<HttpRequestMessage, string, (HttpStatusCode, string)> route) => _route = route;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string body = request.Content switch
        {
            null => "",
            MultipartFormDataContent m => "<multipart>" + string.Join(",", m.Select(p => p.Headers.ContentDisposition?.Name?.Trim('"'))),
            _ => await request.Content.ReadAsStringAsync(ct),
        };
        request.Headers.TryGetValues("X-API-Key", out var key);
        Requests.Add((request.Method.Method, request.RequestUri!.PathAndQuery, body, key?.FirstOrDefault(),
            request.Headers.Authorization?.Parameter));
        var (status, text) = _route(request, body);
        return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}

public class ScriberrBackendTests
{
    static string TempAudio()
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, new byte[64]);
        return path;
    }

    static FakeServer Server(string finalStatus = "completed", int pollsBeforeDone = 1,
        string summary = "<think>hmm</think>\n## Summary\nDone.")
    {
        int polls = 0;
        return new FakeServer((req, _) => req.RequestUri!.AbsolutePath switch
        {
            "/api/v1/profiles/" => (HttpStatusCode.OK, """
                [{"name":"Other","is_default":false,"parameters":{"model_family":"whisper"}},
                 {"name":"Meeting (GPU, pyannote)","is_default":true,"parameters":{"model_family":"nvidia_parakeet","diarize":true,"hf_token":"hf_SECRETSECRET123"}}]
                """),
            "/api/v1/transcription/upload" => (HttpStatusCode.OK, """{"id":"job-1","status":"uploaded"}"""),
            "/api/v1/transcription/job-1/start" => (HttpStatusCode.OK, "{}"),
            "/api/v1/transcription/job-1/status" => ++polls <= pollsBeforeDone
                ? (HttpStatusCode.OK, """{"status":"processing"}""")
                : (HttpStatusCode.OK, finalStatus == "completed"
                    ? """{"status":"completed"}"""
                    : """{"status":"failed","error_message":"ImportError: pyarrow","parameters":{"hf_token":"hf_SECRETSECRET123"}}"""),
            "/api/v1/transcription/job-1/transcript" => (HttpStatusCode.OK, """
                {"transcript":{"segments":[{"start":0.5,"end":2.0,"speaker":"SPEAKER_00","text":" Hello there. "},
                                           {"start":2.1,"end":3.0,"speaker":"SPEAKER_01","text":"Hi."}]}}
                """),
            "/api/v1/transcription/job-1/speakers" => (HttpStatusCode.OK, "[]"),
            "/api/v1/summarize/" => (HttpStatusCode.OK, summary),
            "/api/v1/summaries/settings" => (HttpStatusCode.OK, """{"default_model":"local-model"}"""),
            _ => (HttpStatusCode.NotFound, "{}"),
        });
    }

    static ScriberrBackend Backend(FakeServer server, string profile = "") =>
        new(new ScriberrSettings { BaseUrl = "https://scriberr.test/", ProfileName = profile }, "key-123", server)
        { PollInterval = TimeSpan.Zero };

    [Fact]
    public async Task Transcribes_with_the_default_profile_and_parses_segments()
    {
        var server = Server(pollsBeforeDone: 2);
        using var backend = Backend(server);
        var audio = TempAudio();
        var result = await backend.TranscribeAsync(audio, "Weekly sync", TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Equal("job-1", result.JobId);
        Assert.Equal("https://scriberr.test/audio/job-1", result.WebUrl);
        Assert.Equal(2, result.Segments.Count);
        Assert.Equal("Hello there.", result.Segments[0].Text);
        Assert.Equal("SPEAKER_00", result.Segments[0].Speaker);
        var start = server.Requests.Single(r => r.Path.EndsWith("/start"));
        Assert.Contains("nvidia_parakeet", start.Body); // the default profile's parameters were sent
        Assert.Contains("<multipart>audio,title", server.Requests.Single(r => r.Path.EndsWith("/upload")).Body);
        Assert.All(server.Requests, r => Assert.Equal("key-123", r.ApiKey));
        File.Delete(audio);
    }

    [Fact]
    public void Word_times_go_with_the_lines_they_spell_and_survive_a_save()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            {"transcript":{"segments":[{"start":0.5,"end":2.0,"speaker":"SPEAKER_00","text":" Hello there. "},
                                       {"start":2.1,"end":3.0,"speaker":"SPEAKER_01","text":"Hi."},
                                       {"start":3.1,"end":4.0,"speaker":"SPEAKER_00","text":"Twenty."}],
                           "word_segments":[{"word":"Hello","start":0.5,"end":0.9,"score":0.9,"speaker":"SPEAKER_00"},
                                            {"word":"there.","start":1.0,"end":2.03,"speaker":"SPEAKER_00"},
                                            {"word":"Hey.","start":2.2,"end":2.9,"speaker":"SPEAKER_01"},
                                            {"word":"20","speaker":"SPEAKER_00"}]}}
            """);
        var segments = ScriberrBackend.ParseSegments(doc.RootElement);
        Assert.Equal(["Hello", "there."], segments[0].Words!.Select(w => w.Text)); // 2.03 is within the engine's rounding
        Assert.Equal("SPEAKER_00", segments[0].Words![0].Speaker);
        Assert.Null(segments[1].Words); // "Hey." isn't what the line says
        Assert.Null(segments[2].Words); // a word without times is left out, and the rest no longer spells the line

        var json = System.Text.Json.JsonSerializer.Serialize(new Transcripts.TranscriptResult(segments, "job-1", null), Json.Options);
        var back = System.Text.Json.JsonSerializer.Deserialize<Transcripts.TranscriptResult>(json, Json.Options)!;
        Assert.Equal(1.0, back.Segments[0].Words![1].Start);
        Assert.Null(back.Segments[1].Words);
    }

    [Fact]
    public async Task A_named_profile_is_used_when_configured()
    {
        var server = Server();
        using var backend = Backend(server, "other");
        await backend.TranscribeAsync(TempAudio(), "t", TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Contains("whisper", server.Requests.Single(r => r.Path.EndsWith("/start")).Body);
    }

    [Fact]
    public async Task A_failed_job_reports_the_error_without_the_token()
    {
        var server = Server(finalStatus: "failed");
        using var backend = Backend(server);
        var ex = await Assert.ThrowsAsync<BackendException>(() =>
            backend.TranscribeAsync(TempAudio(), "t", TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.Contains("pyarrow", ex.Message);
        Assert.DoesNotContain("hf_SECRET", ex.Message);
    }

    /// <summary>Scriberr holding job-9 in <paramref name="state"/>, which turns "completed" after a start (if failed) or a poll.</summary>
    static FakeServer Resumable(string? state)
    {
        string? status = state;
        return new FakeServer((req, _) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            switch (path)
            {
                case "/api/v1/transcription/job-9/start":
                    status = "processing";
                    return (HttpStatusCode.OK, "{}");
                case "/api/v1/transcription/job-9/status":
                    if (status is null) return (HttpStatusCode.NotFound, """{"error":"Job not found"}""");
                    var now = status;
                    if (status == "processing") status = "completed"; // each look finds a running job a step further along
                    return (HttpStatusCode.OK, $$"""{"status":"{{now}}"}""");
                case "/api/v1/profiles/":
                    return (HttpStatusCode.OK, """[{"name":"Meeting","is_default":true,"parameters":{"diarize":true}}]""");
                case "/api/v1/transcription/upload":
                    return (HttpStatusCode.OK, """{"id":"job-10","status":"uploaded"}""");
                case "/api/v1/transcription/job-10/start":
                    return (HttpStatusCode.OK, "{}");
                case "/api/v1/transcription/job-10/status":
                    return (HttpStatusCode.OK, """{"status":"completed"}""");
            }
            return path.EndsWith("/transcript")
                ? (HttpStatusCode.OK, """{"transcript":{"segments":[{"start":0,"end":1,"speaker":"SPEAKER_00","text":"Hi."}]}}""")
                : (HttpStatusCode.NotFound, "{}");
        });
    }

    [Theory]
    [InlineData("processing", false)]  // still running: just wait for it
    [InlineData("completed", false)]   // finished while nobody was looking
    [InlineData("failed", true)]       // failed: Scriberr runs it again on request
    public async Task A_retry_picks_up_its_job_instead_of_uploading_again(string state, bool restarted)
    {
        var server = Resumable(state);
        using var backend = Backend(server);
        TranscriptionJob? started = null;
        var result = await backend.TranscribeAsync(TempAudio(), "t", TimeSpan.FromSeconds(10), "job-9", j => started = j, CancellationToken.None);
        Assert.Equal("job-9", result.JobId);
        Assert.DoesNotContain(server.Requests, r => r.Path.EndsWith("/upload"));
        Assert.Equal(restarted, server.Requests.Any(r => r.Path.EndsWith("job-9/start")));
        Assert.Null(started); // nothing new to save
    }

    [Fact]
    public async Task A_job_Scriberr_no_longer_has_is_uploaded_again_and_saved_at_once()
    {
        var server = Resumable(null);
        using var backend = Backend(server);
        TranscriptionJob? started = null;
        int requestsWhenSaved = -1;
        var result = await backend.TranscribeAsync(TempAudio(), "t", TimeSpan.FromSeconds(10), "job-9",
            j => { started = j; requestsWhenSaved = server.Requests.Count; }, CancellationToken.None);
        Assert.Equal("job-10", result.JobId);
        Assert.Equal(new TranscriptionJob("job-10", "https://scriberr.test/audio/job-10"), started);
        Assert.EndsWith("/upload", server.Requests[requestsWhenSaved - 1].Path); // saved straight after the upload, before starting
    }

    [Fact]
    public async Task Summarize_sends_labelled_transcript_and_strips_think_blocks()
    {
        var server = Server();
        using var backend = Backend(server);
        var transcript = new Transcripts.TranscriptResult([], "job-1", null);
        var summary = await backend.SummarizeAsync(transcript, "INSTRUCTIONS", "[00:00:01] Paul: hi", CancellationToken.None);
        Assert.Equal("## Summary\nDone.", summary);
        var req = server.Requests.Single(r => r.Path == "/api/v1/summarize/");
        Assert.Contains("local-model", req.Body);
        Assert.Contains("Paul: hi", req.Body);
        Assert.Contains("job-1", req.Body);
    }

    [Theory]
    [InlineData("")]                                  // Scriberr's 5-minute limit hit before any answer
    [InlineData("<think>\nThe meeting covers")]       // ... or mid-thought
    public async Task A_summary_cut_off_by_Scriberr_is_an_error_to_retry_not_an_empty_note(string reply)
    {
        using var backend = Backend(Server(summary: reply));
        var ex = await Assert.ThrowsAsync<BackendException>(() => backend.SummarizeAsync(
            new Transcripts.TranscriptResult([], "job-1", null), "INSTRUCTIONS", "[00:00:01] Paul: hi", CancellationToken.None));
        Assert.Contains("no summary", ex.Message);
    }

    [Fact]
    public async Task Speaker_names_are_posted_as_mappings()
    {
        var server = Server();
        using var backend = Backend(server);
        await backend.NameSpeakersAsync(new Transcripts.TranscriptResult([], "job-1", null),
            new Dictionary<string, string> { ["SPEAKER_00"] = "Paul" }, CancellationToken.None);
        var body = server.Requests.Single(r => r.Path.EndsWith("/speakers")).Body;
        Assert.Contains("\"original_speaker\":\"SPEAKER_00\"", body);
        Assert.Contains("\"custom_name\":\"Paul\"", body);
    }
}

public class OpenAiBackendTests
{
    [Fact]
    public async Task Parses_diarized_json_and_chat_completions()
    {
        var server = new FakeServer((req, _) => req.RequestUri!.AbsolutePath switch
        {
            "/v1/audio/transcriptions" => (HttpStatusCode.OK, """
                {"text":"x","segments":[{"type":"transcript.text.segment","id":"0","start":1.2,"end":3.4,"text":"Hello","speaker":"A"},
                                        {"start":"0:00:04.000","end":"0:00:05.500","text":"Hi","speaker":"B"}]}
                """),
            "/llm/v1/chat/completions" => (HttpStatusCode.OK, """{"choices":[{"message":{"content":"## Summary\nOk."}}]}"""),
            _ => (HttpStatusCode.NotFound, "{}"),
        });
        var stt = new OpenAiSettings { TranscriptionBaseUrl = "https://wlk.test/v1", ExtraFields = { ["chunking_strategy"] = "auto" } };
        var llm = new SummarySettings { BaseUrl = "https://gw.test/llm/v1", Model = "m" };
        using var backend = new OpenAiBackend(stt, null, llm, "sk-user", server);

        var path = Path.GetTempFileName();
        var t = await backend.TranscribeAsync(path, "t", TimeSpan.FromSeconds(6), CancellationToken.None);
        Assert.Equal(2, t.Segments.Count);
        Assert.Equal(4.0, t.Segments[1].Start, 3);
        Assert.Equal("B", t.Segments[1].Speaker);
        var sttReq = server.Requests[0];
        Assert.Contains("file", sttReq.Body);
        Assert.Contains("response_format", sttReq.Body);
        Assert.Contains("chunking_strategy", sttReq.Body);
        Assert.Null(sttReq.Bearer); // no transcription key configured

        var summary = await backend.SummarizeAsync(t, "INSTR", "[00:00:01] Paul: hi", CancellationToken.None);
        Assert.Equal("## Summary\nOk.", summary);
        Assert.Equal("sk-user", server.Requests[1].Bearer);
        File.Delete(path);
    }

    [Fact]
    public async Task Empty_model_output_is_an_actionable_error()
    {
        var server = new FakeServer((_, _) => (HttpStatusCode.OK, """{"choices":[{"message":{"content":""}}]}"""));
        using var backend = new OpenAiBackend(new OpenAiSettings(), null, new SummarySettings { BaseUrl = "https://x/v1", Model = "m" }, null, server);
        var ex = await Assert.ThrowsAsync<BackendException>(() =>
            backend.SummarizeAsync(new Transcripts.TranscriptResult([], null, null), "i", "t", CancellationToken.None));
        Assert.Contains("maxTokens", ex.Message);
    }

    [Fact]
    public async Task A_names_check_that_ran_out_of_tokens_thinking_is_not_reported_as_no_names()
    {
        var server = new FakeServer((_, _) => (HttpStatusCode.OK,
            """{"choices":[{"message":{"content":null,"reasoning_content":"..."},"finish_reason":"length"}]}"""));
        using var chat = new Speakers.ChatModel(new SummarySettings { BaseUrl = "https://x/v1", Model = "m" }, null, server);
        var ex = await Assert.ThrowsAsync<BackendException>(() => chat.AskAsync("s", "u", CancellationToken.None));
        Assert.Contains("maxTokens", ex.Message);
    }
}

public class SafeSnippetTests
{
    [Theory]
    [InlineData("""{"error":"bad","hf_token":"hf_abcdefghijklmnop"}""")]
    [InlineData("Authorization: Bearer sk-abcdefghijkl12345")]
    [InlineData("""{"api_key": "plain-secret-value"}""")]
    public void Credentials_never_survive(string body)
    {
        var s = Http.SafeSnippet(body);
        Assert.DoesNotContain("abcdefghijk", s);
        Assert.DoesNotContain("plain-secret-value", s);
        Assert.Contains("<redacted>", s);
    }
}
