using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ErrorLensLLM.AI.Claude;

public sealed class OpenAiOptions
{
    public const string Section = "OpenAI";

    /// <summary>Taken from <c>OPENAI_API_KEY</c> when not set here or on the settings screen.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Any chat model with structured outputs; changeable on the settings screen.</summary>
    public string Model { get; set; } = "gpt-5-mini";

    public string BaseUrl { get; set; } = "https://api.openai.com/v1/";

    public int MaxOutputTokens { get; set; } = 8000;

    /// <summary>Tries per call when the API is overloaded (429/500/503).</summary>
    public int MaxAttempts { get; set; } = 4;

    /// <summary>Wait before the first retry; doubles after each.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// The same explanation from OpenAI (ChatGPT) through the Chat Completions API with a strict JSON Schema
/// (<c>response_format: json_schema</c>). Prompt, input and schema are shared with the other providers.
/// </summary>
public sealed class OpenAiExplainer(HttpClient http, OpenAiOptions options) : IIncidentExplainer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Model => options.Model;

    public async Task<ExplanationResult> ExplainAsync(IncidentContext incident, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["model"] = options.Model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = IncidentExplainer.SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = IncidentExplainer.Describe(incident) }),
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = "incident_explanation",
                    ["strict"] = true,
                    ["schema"] = JsonSerializer.SerializeToNode(IncidentExplainer.Schema())
                }
            },
            ["max_completion_tokens"] = options.MaxOutputTokens
        };

        HttpResponseMessage response;
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(options.BaseUrl), "chat/completions"))
            {
                Content = JsonContent.Create(body)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

            response = await http.SendAsync(request, ct);
            if (attempt >= options.MaxAttempts || (int)response.StatusCode is not (429 or 500 or 503))
                break;
            response.Dispose();
            await Task.Delay(options.RetryDelay * Math.Pow(2, attempt - 1), ct);
        }

        using var _ = response;
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"OpenAI API {(int)response.StatusCode}: {Shorten(text)}");

        var root = JsonNode.Parse(text)!;
        var choice = root["choices"]?[0] ?? throw new InvalidOperationException("The model returned no choice.");
        var message = choice["message"];
        if (message?["refusal"]?.GetValue<string>() is { Length: > 0 } refusal)
            throw new InvalidOperationException($"The model declined to explain the incident ({refusal}).");

        var finish = choice["finish_reason"]?.GetValue<string>();
        if (finish == "length")
            throw new InvalidOperationException("The explanation was cut off at the token limit.");
        if (finish is not null and not "stop")
            throw new InvalidOperationException($"The model stopped without an explanation ({finish}).");

        var answer = message?["content"]?.GetValue<string>() ?? throw new InvalidOperationException("The model returned no explanation.");
        var explanation = JsonSerializer.Deserialize<IncidentExplanation>(answer, Json)
                          ?? throw new InvalidOperationException("The model returned no explanation.");

        var usage = root["usage"];
        return new ExplanationResult(explanation, root["model"]?.GetValue<string>() ?? options.Model,
            usage?["prompt_tokens"]?.GetValue<long>() ?? 0, usage?["completion_tokens"]?.GetValue<long>() ?? 0, answer);
    }

    private static string Shorten(string text) => text.Length <= 500 ? text : text[..500] + "…";
}
