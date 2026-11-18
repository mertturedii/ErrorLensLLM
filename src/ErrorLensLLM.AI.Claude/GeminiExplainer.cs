using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ErrorLensLLM.AI.Claude;

public sealed class GeminiOptions
{
    public const string Section = "Gemini";

    /// <summary>Taken from <c>GEMINI_API_KEY</c> when not set here.</summary>
    public string? ApiKey { get; set; }

    /// <summary>A stable model with structured output support (Gemini API models page, 2026-11).</summary>
    public string Model { get; set; } = "gemini-3.5-flash-lite";

    public string BaseUrl { get; set; } = "https://generativelanguage.googleapis.com/v1beta/";

    public int MaxOutputTokens { get; set; } = 8000;

    /// <summary>Tries per call when Gemini is overloaded (429/500/503).</summary>
    public int MaxAttempts { get; set; } = 4;

    /// <summary>Wait before the first retry; doubles after each.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// The same explanation as <see cref="IncidentExplainer"/>, from Google's Gemini API (REST <c>generateContent</c>).
/// The prompt, the masked incident text and the JSON Schema of the answer are shared, so the two providers can be
/// compared on the same incidents.
/// </summary>
public sealed class GeminiExplainer(HttpClient http, GeminiOptions options) : IIncidentExplainer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Model => options.Model;

    public async Task<ExplanationResult> ExplainAsync(IncidentContext incident, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = IncidentExplainer.SystemPrompt }) },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = IncidentExplainer.Describe(incident) })
            }),
            ["generationConfig"] = new JsonObject
            {
                ["responseMimeType"] = "application/json",
                ["responseJsonSchema"] = JsonSerializer.SerializeToNode(IncidentExplainer.Schema()),
                ["maxOutputTokens"] = options.MaxOutputTokens
            }
        };

        // 429 and 503 ("high demand") are usually over within seconds; a few retries with backoff save the incident a 10-minute wait
        HttpResponseMessage response;
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(options.BaseUrl), $"models/{options.Model}:generateContent"))
            {
                Content = JsonContent.Create(body)
            };
            // The key goes in a header, never in the URL, so that it does not end up in request logs
            request.Headers.Add("x-goog-api-key", options.ApiKey);

            response = await http.SendAsync(request, ct);
            if (attempt >= options.MaxAttempts || (int)response.StatusCode is not (429 or 500 or 503))
                break;
            response.Dispose();
            await Task.Delay(options.RetryDelay * Math.Pow(2, attempt - 1), ct);
        }

        using var _ = response;
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Gemini API {(int)response.StatusCode}: {Shorten(text)}");

        var root = JsonNode.Parse(text)!;
        if (root["promptFeedback"]?["blockReason"]?.GetValue<string>() is { } blocked)
            throw new InvalidOperationException($"The model declined to explain the incident ({blocked}).");

        var candidate = root["candidates"]?[0] ?? throw new InvalidOperationException("The model returned no candidate.");
        var finish = candidate["finishReason"]?.GetValue<string>();
        if (finish == "MAX_TOKENS")
            throw new InvalidOperationException("The explanation was cut off at the token limit.");
        if (finish is not null and not "STOP")
            throw new InvalidOperationException($"The model stopped without an explanation ({finish}).");

        var answer = string.Concat((candidate["content"]?["parts"]?.AsArray() ?? []).Select(p => p?["text"]?.GetValue<string>()));
        var explanation = JsonSerializer.Deserialize<IncidentExplanation>(answer, Json)
                          ?? throw new InvalidOperationException("The model returned no explanation.");

        var usage = root["usageMetadata"];
        return new ExplanationResult(explanation, root["modelVersion"]?.GetValue<string>() ?? options.Model,
            usage?["promptTokenCount"]?.GetValue<long>() ?? 0, usage?["candidatesTokenCount"]?.GetValue<long>() ?? 0, answer);
    }

    private static string Shorten(string text) => text.Length <= 500 ? text : text[..500] + "…";
}
