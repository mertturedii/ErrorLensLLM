using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ErrorLensLLM.AI.Claude;
using ErrorLensLLM.Storage.PostgreSql;

namespace ErrorLensLLM.Collector.Explanations;

/// <summary>The provider incidents are explained with right now.</summary>
public sealed record ActiveProvider(string Name, string DisplayName, string Model, IIncidentExplainer Explainer);

/// <summary>
/// Sends an incident to the default provider of the settings screen and records every request (prompt, answer, tokens,
/// duration, error) in <c>llm_requests</c>. The provider, model and key are read again every few seconds, so switching
/// provider or entering a key needs no restart. A key entered on the screen wins over the environment variable.
/// </summary>
public sealed class ExplanationRouter(
    PostgresSettingsStore store, ApiKeyProtector protector, IConfiguration configuration, ILogger<ExplanationRouter> log)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private readonly Dictionary<string, IIncidentExplainer> _explainers = [];
    private readonly SemaphoreSlim _lock = new(1, 1);
    private ActiveProvider? _active;
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;
    private string? _lastState;

    /// <summary>The default provider when it is on and has a key; null when the layer is off.</summary>
    public async Task<ActiveProvider?> ActiveAsync(CancellationToken ct = default)
    {
        if (DateTimeOffset.UtcNow - _loadedAt < CacheFor)
            return _active;

        await _lock.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow - _loadedAt >= CacheFor)
            {
                _active = await ResolveAsync(ct);
                _loadedAt = DateTimeOffset.UtcNow;

                var state = _active is null ? "off" : $"{_active.Name}/{_active.Model}";
                if (state != _lastState)
                {
                    if (_active is null)
                        log.LogInformation("LLM explanations are off: no default provider with a key (settings screen or environment)");
                    else
                        log.LogInformation("LLM explanations are on with {Provider} {Model}", _active.DisplayName, _active.Model);
                    _lastState = state;
                }
            }

            return _active;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ExplanationResult> ExplainAsync(Guid incidentId, IncidentContext incident, CancellationToken ct)
    {
        var active = await ActiveAsync(ct) ?? throw new InvalidOperationException("No LLM provider is configured.");
        var userPrompt = IncidentExplainer.Describe(incident);
        var watch = Stopwatch.StartNew();
        try
        {
            var result = await active.Explainer.ExplainAsync(incident, ct);
            await LogAsync(active, result.Model, incidentId, userPrompt, result.RawResponse, result.InputTokens, result.OutputTokens, watch, null);
            return result;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await LogAsync(active, active.Model, incidentId, userPrompt, null, 0, 0, watch, ex.Message);
            throw;
        }
    }

    private async Task LogAsync(
        ActiveProvider active, string model, Guid incidentId, string userPrompt, string? response, long input, long output, Stopwatch watch, string? error)
    {
        try
        {
            await store.LogRequestAsync(new LlmRequestRow(
                0, DateTimeOffset.UtcNow, active.Name, model, incidentId, "Olay açıklama", IncidentExplainer.SystemPrompt, userPrompt, response,
                input, output, (int)watch.ElapsedMilliseconds, error));
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException)
        {
            // The explanation itself is stored separately; a lost log line must not lose it
            log.LogWarning(ex, "LLM request could not be logged");
        }
    }

    private async Task<ActiveProvider?> ResolveAsync(CancellationToken ct)
    {
        var rows = await store.ProvidersAsync(ct);
        var row = rows.FirstOrDefault(r => r.IsDefault && r.Enabled);
        if (row is null || LlmProviders.Find(row.Name) is not { } info)
            return null;

        var key = protector.Unprotect(row.ProtectedApiKey)
                  ?? configuration[$"{SectionOf(row.Name)}:ApiKey"].NullIfEmpty()
                  ?? Environment.GetEnvironmentVariable(info.KeyVariable).NullIfEmpty();
        if (key is null)
            return null;

        // One explainer per provider, model, limit and key; a changed key or model gets a new one
        var cacheKey = $"{row.Name}|{row.Model}|{row.MaxOutputTokens}|{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))}";
        if (!_explainers.TryGetValue(cacheKey, out var explainer))
            _explainers[cacheKey] = explainer = LlmProviders.Create(row.Name, key, row.Model, row.MaxOutputTokens, _http);

        return new ActiveProvider(row.Name, row.DisplayName, row.Model, explainer);
    }

    private static string SectionOf(string provider) => provider switch
    {
        LlmProviders.Claude => ClaudeOptions.Section,
        LlmProviders.Gemini => GeminiOptions.Section,
        LlmProviders.OpenAi => OpenAiOptions.Section,
        _ => provider
    };
}

internal static class StringExtensions
{
    public static string? NullIfEmpty(this string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
