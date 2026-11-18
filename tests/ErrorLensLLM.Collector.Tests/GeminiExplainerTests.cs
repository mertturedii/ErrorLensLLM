using System.Net;
using System.Text;
using System.Text.Json;
using ErrorLensLLM.AI.Claude;
using ErrorLensLLM.Core.Incidents;

namespace ErrorLensLLM.Collector.Tests;

/// <summary>The Gemini provider against a stubbed generateContent endpoint.</summary>
public class GeminiExplainerTests
{
    private static readonly IncidentContext Incident = new(
        "kayit 1.1.0 sonrası PostgresException — 3 modül, 890 hata", ErrorCategory.DatabaseSchema,
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

    private static string Answer(string finishReason, object? feedback = null) => JsonSerializer.Serialize(new
    {
        candidates = new[]
        {
            new
            {
                content = new { role = "model", parts = new[] { new { text = JsonSerializer.Serialize(new
                {
                    summary = "kayit 1.1.0 yeni bir kolonu okuyor.", rootCause = "donem_id kolonu yok.", impact = "not ve odeme etkileniyor.",
                    nextSteps = new[] { "geri al" }, confidence = "yüksek", disagreement = (string?)null
                }) } } },
                finishReason
            }
        },
        promptFeedback = feedback,
        usageMetadata = new { promptTokenCount = 900, candidatesTokenCount = 150 },
        modelVersion = "gemini-3.5-flash-lite"
    });

    private static GeminiExplainer Explainer(StubHandler handler) =>
        new(new HttpClient(handler), new GeminiOptions { ApiKey = "test-key" });

    [Fact]
    public async Task Request_carries_the_shared_prompt_and_schema_and_the_key_in_a_header()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Answer("STOP"));

        var result = await Explainer(handler).ExplainAsync(Incident);

        Assert.Equal("yüksek", result.Explanation.Confidence);
        Assert.Equal(900, result.InputTokens);
        Assert.Equal(150, result.OutputTokens);
        Assert.Equal("gemini-3.5-flash-lite", result.Model);

        Assert.EndsWith("/v1beta/models/gemini-3.5-flash-lite:generateContent", handler.Request!.RequestUri!.AbsolutePath);
        Assert.DoesNotContain("test-key", handler.Request.RequestUri.ToString());
        Assert.Equal("test-key", handler.Request.Headers.GetValues("x-goog-api-key").Single());

        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal(IncidentExplainer.SystemPrompt, body.RootElement.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
        var config = body.RootElement.GetProperty("generationConfig");
        Assert.Equal("application/json", config.GetProperty("responseMimeType").GetString());
        Assert.Equal("object", config.GetProperty("responseJsonSchema").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Blocked_prompt_is_an_error_not_an_explanation()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Answer("STOP", new { blockReason = "SAFETY" }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Explainer(handler).ExplainAsync(Incident));
    }

    [Fact]
    public async Task Api_error_reports_status_and_message()
    {
        var handler = new StubHandler(HttpStatusCode.BadRequest, """{"error":{"code":400,"message":"API key not valid"}}""");

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Explainer(handler).ExplainAsync(Incident));
        Assert.Contains("400", error.Message);
        Assert.Contains("API key not valid", error.Message);
    }
}
