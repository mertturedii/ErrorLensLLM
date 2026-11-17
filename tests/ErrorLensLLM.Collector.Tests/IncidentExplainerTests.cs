using System.Net;
using System.Text;
using System.Text.Json;
using Anthropic;
using ErrorLensLLM.AI.Claude;
using ErrorLensLLM.Core.Incidents;

namespace ErrorLensLLM.Collector.Tests;

/// <summary>The explanation layer against a stubbed Messages API: what is sent, and how the answer is read.</summary>
public class IncidentExplainerTests
{
    private static readonly IncidentContext Incident = new(
        "kayit 1.1.0 sonrası PostgresException — 3 modül, 890 hata", ErrorCategory.DatabaseSchema,
        DateTimeOffset.Parse("2026-11-17T10:00:00Z"), DateTimeOffset.Parse("2026-11-17T10:00:25Z"), 890, ["kayit", "not", "odeme"],
        "kayit 1.1.0 (commit abc123), 2026-11-17 09:59:58Z",
        [
            new IncidentMember("f1", "kayit", "Npgsql.PostgresException", "<num>: column \"donem_id\" does not exist", "KayitService.GetirAsync",
                ErrorCategory.DatabaseSchema, 558, DateTimeOffset.Parse("2026-11-17T10:00:00Z"), DateTimeOffset.Parse("2026-11-17T10:00:25Z"), 0.88,
                ["909 paylaşılan trace'in 909'unda zincirdeki ilk hata", "kayit 1.1.0 deploy'undan 0 sn sonra başladı"]),
            new IncidentMember("f2", "not", "System.Net.Http.HttpRequestException", "Response status code does not indicate success: <num>",
                "KayitClient.GetirAsync", ErrorCategory.Dependency, 218, DateTimeOffset.Parse("2026-11-17T10:00:01Z"),
                DateTimeOffset.Parse("2026-11-17T10:00:25Z"), 0.08, ["909 trace'te kayit hatasından sonra geldi"])
        ],
        ["10:00:01.100 [not] Error: istek basarisiz (HttpRequestException: 500)"],
        "Npgsql.PostgresException (0x80004005): 42703: column \"donem_id\" does not exist",
        ["10:00:00 kayit: DeepLog beklenmeyen olay oranı %50; yeni şablonlar: HTTP {Method} {Path} failed"]);

    private sealed class StubHandler(string responseBody) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") };
        }
    }

    private static string Response(string stopReason, string text) => JsonSerializer.Serialize(new
    {
        id = "msg_test",
        type = "message",
        role = "assistant",
        model = "claude-opus-5-5",
        content = new[] { new { type = "text", text } },
        stop_reason = stopReason,
        stop_sequence = (string?)null,
        usage = new { input_tokens = 1200, output_tokens = 300 }
    });

    private static IncidentExplainer Explainer(StubHandler handler) =>
        new(new AnthropicClient { ApiKey = "test", HttpClient = new HttpClient(handler), MaxRetries = 0 }, new ClaudeOptions());

    [Fact]
    public void Prompt_carries_candidates_evidence_deployment_chain_and_anomalies()
    {
        var prompt = IncidentExplainer.Describe(Incident);

        Assert.Contains("Şüpheli deploy: kayit 1.1.0 (commit abc123)", prompt);
        Assert.Contains("1. [kayit] Npgsql.PostgresException", prompt);
        Assert.Contains("kanıt: 909 paylaşılan trace'in 909'unda zincirdeki ilk hata", prompt);
        Assert.Contains("[not] Error: istek basarisiz", prompt);
        Assert.Contains("42703", prompt);
        Assert.Contains("DeepLog beklenmeyen olay oranı %50", prompt);
    }

    [Fact]
    public async Task Structured_answer_is_parsed_and_the_request_asks_for_json_with_fallbacks()
    {
        var answer = JsonSerializer.Serialize(new
        {
            summary = "kayit 1.1.0 yeni bir kolonu okuyor ama migration çalışmamış.",
            rootCause = "donem_id kolonu yok.",
            impact = "not ve odeme kayit'a bağlı olduğu için onlar da hata veriyor.",
            nextSteps = new[] { "kayit'ı 1.0.0'a geri al", "migration'ı çalıştır" },
            confidence = "yüksek",
            disagreement = (string?)null
        });
        var handler = new StubHandler(Response("end_turn", answer));

        var result = await Explainer(handler).ExplainAsync(Incident);

        Assert.Equal("yüksek", result.Explanation.Confidence);
        Assert.Equal(2, result.Explanation.NextSteps.Count);
        Assert.Null(result.Explanation.Disagreement);
        Assert.Equal(1200, result.InputTokens);

        using var request = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("claude-opus-5-5", request.RootElement.GetProperty("model").GetString());
        Assert.Equal("json_schema", request.RootElement.GetProperty("output_config").GetProperty("format").GetProperty("type").GetString());
        Assert.Equal("medium", request.RootElement.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal("default", request.RootElement.GetProperty("fallbacks").GetString());
    }

    [Fact]
    public async Task Refusal_is_an_error_not_an_explanation()
    {
        var handler = new StubHandler(Response("refusal", ""));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Explainer(handler).ExplainAsync(Incident));
    }
}
