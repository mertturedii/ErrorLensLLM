using System.Net;
using System.Text;
using System.Text.Json;
using ErrorLensLLM.AI.Claude;
using ErrorLensLLM.Core.Incidents;

namespace ErrorLensLLM.Collector.Tests;

/// <summary>The ChatGPT (OpenAI) provider against a stubbed Chat Completions endpoint.</summary>
public class OpenAiExplainerTests
{
    private static readonly IncidentContext Incident = new(
        "kayit 1.1.0 sonrası PostgresException — 3 servis, 890 hata", ErrorCategory.DatabaseSchema,
        DateTimeOffset.Parse("2026-11-17T10:00:00Z"), DateTimeOffset.Parse("2026-11-17T10:00:25Z"), 890, ["kayit", "not", "odeme"],
        "kayit 1.1.0 (commit abc123)", [], [], null, []);

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            RequestBody = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static readonly string Explanation = JsonSerializer.Serialize(new
    {
        summary = "kayit 1.1.0 yeni bir kolonu okuyor.", rootCause = "donem_id kolonu yok.", impact = "not ve odeme etkileniyor.",
        nextSteps = new[] { "geri al" }, confidence = "yüksek", disagreement = (string?)null
    });

    private static string Answer(string finishReason, string? refusal = null) => JsonSerializer.Serialize(new
    {
        model = "gpt-5-mini-2025-08-07",
        choices = new[] { new { index = 0, message = new { role = "assistant", content = refusal is null ? Explanation : null, refusal }, finish_reason = finishReason } },
        usage = new { prompt_tokens = 900, completion_tokens = 150 }
    });

    private static OpenAiExplainer Explainer(StubHandler handler) =>
        new(new HttpClient(handler), new OpenAiOptions { ApiKey = "test-key", RetryDelay = TimeSpan.Zero });

    [Fact]
    public async Task Request_carries_the_shared_prompt_a_strict_schema_and_a_bearer_key()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Answer("stop"));

        var result = await Explainer(handler).ExplainAsync(Incident);

        Assert.Equal("yüksek", result.Explanation.Confidence);
        Assert.Equal((900L, 150L), (result.InputTokens, result.OutputTokens));
        Assert.Equal("gpt-5-mini-2025-08-07", result.Model);
        Assert.Equal(Explanation, result.RawResponse);

        Assert.EndsWith("/v1/chat/completions", handler.Request!.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer test-key", handler.Request.Headers.Authorization!.ToString());

        using var body = JsonDocument.Parse(handler.RequestBody!);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(IncidentExplainer.SystemPrompt, messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        var format = body.RootElement.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("json_schema").GetProperty("strict").GetBoolean());
    }

    [Fact]
    public async Task Refusal_and_cut_off_answers_are_errors()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Explainer(new StubHandler(HttpStatusCode.OK, Answer("stop", "I can't help with that."))).ExplainAsync(Incident));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Explainer(new StubHandler(HttpStatusCode.OK, Answer("length"))).ExplainAsync(Incident));
    }

    [Fact]
    public async Task Api_error_reports_status_and_message()
    {
        var handler = new StubHandler(HttpStatusCode.Unauthorized, """{"error":{"message":"Incorrect API key provided"}}""");

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Explainer(handler).ExplainAsync(Incident));
        Assert.Contains("401", error.Message);
        Assert.Contains("Incorrect API key", error.Message);
    }
}
