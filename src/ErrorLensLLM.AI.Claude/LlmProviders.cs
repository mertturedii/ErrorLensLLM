using Anthropic;

namespace ErrorLensLLM.AI.Claude;

/// <summary>A supported LLM provider: its id, how it is shown, where its key can come from and its default model.</summary>
public sealed record LlmProviderInfo(string Name, string DisplayName, string KeyVariable, string DefaultModel, string KeyPage);

/// <summary>
/// The providers the explanation layer can use. A new provider is one entry here and one <see cref="IIncidentExplainer"/>;
/// the settings screen lists every entry, so its key, model and on/off state are set there.
/// </summary>
public static class LlmProviders
{
    public const string Claude = "claude", Gemini = "gemini", OpenAi = "openai";

    public static readonly IReadOnlyList<LlmProviderInfo> All =
    [
        new(Claude, "Claude (Anthropic)", "ANTHROPIC_API_KEY", new ClaudeOptions().Model, "https://console.anthropic.com/settings/keys"),
        new(Gemini, "Gemini (Google)", "GEMINI_API_KEY", new GeminiOptions().Model, "https://aistudio.google.com/apikey"),
        new(OpenAi, "ChatGPT (OpenAI)", "OPENAI_API_KEY", new OpenAiOptions().Model, "https://platform.openai.com/api-keys")
    ];

    public static LlmProviderInfo? Find(string name) => All.FirstOrDefault(p => p.Name == name);

    /// <summary>An explainer for one provider with the given key, model and output limit.</summary>
    public static IIncidentExplainer Create(string name, string apiKey, string model, int maxOutputTokens, HttpClient http) => name switch
    {
        Claude => new IncidentExplainer(new AnthropicClient { ApiKey = apiKey },
            new ClaudeOptions { ApiKey = apiKey, Model = model, MaxTokens = maxOutputTokens }),
        Gemini => new GeminiExplainer(http, new GeminiOptions { ApiKey = apiKey, Model = model, MaxOutputTokens = maxOutputTokens }),
        OpenAi => new OpenAiExplainer(http, new OpenAiOptions { ApiKey = apiKey, Model = model, MaxOutputTokens = maxOutputTokens }),
        _ => throw new ArgumentException($"Unknown LLM provider '{name}'.", nameof(name))
    };
}
