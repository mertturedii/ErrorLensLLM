using System.Security.Cryptography;
using System.Text;
using ErrorLensLLM.Collector;
using Anthropic;
using ErrorLensLLM.AI.Claude;
using ErrorLensLLM.Collector.Anomalies;
using ErrorLensLLM.Collector.Explanations;
using ErrorLensLLM.Collector.Incidents;
using ErrorLensLLM.Collector.Settings;
using ErrorLensLLM.Collector.Ingestion;
using ErrorLensLLM.Core.Incidents;
using ErrorLensLLM.Core.Ingestion;
using ErrorLensLLM.Core.Processing;
using ErrorLensLLM.Storage.PostgreSql;
using Microsoft.Extensions.Options;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<CollectorOptions>(builder.Configuration.GetSection(CollectorOptions.Section));
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(
    builder.Configuration.GetConnectionString("ErrorLens") ?? throw new InvalidOperationException("ConnectionStrings:ErrorLens is missing.")));
builder.Services.AddSingleton<PostgresLogStore>();
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<CollectorOptions>>().Value;
    return new IngestPipeline(new SensitiveDataMasker(options.SensitiveFields), options.DrainSimilarity);
});
builder.Services.AddSingleton<IngestService>();

builder.Services.Configure<AnalysisOptions>(builder.Configuration.GetSection(AnalysisOptions.Section));
builder.Services.AddSingleton<PostgresIncidentStore>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IncidentAnalyzer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IncidentAnalyzer>());

builder.Services.Configure<AnomalyOptions>(builder.Configuration.GetSection(AnomalyOptions.Section));
builder.Services.AddSingleton<PostgresAnomalyStore>();
builder.Services.AddSingleton<AnomalyMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AnomalyMonitor>());

// Settings screen: tunable values and LLM providers live in the database and are read again every few seconds
ApiKeyProtector.AddTo(builder.Services, builder.Configuration);
builder.Services.AddSingleton<PostgresSettingsStore>();
builder.Services.AddSingleton<RuntimeSettings>();

// LLM explanation layer: the default provider of the settings screen (Claude, Gemini or ChatGPT). Its key is the one
// entered there, or else Claude:ApiKey / ANTHROPIC_API_KEY, Gemini:ApiKey / GEMINI_API_KEY, OpenAI:ApiKey / OPENAI_API_KEY.
builder.Services.AddSingleton<PostgresExplanationStore>();
builder.Services.AddSingleton<PostgresDashboardStore>();
builder.Services.AddSingleton<ExplanationRouter>();
builder.Services.AddSingleton<ExplanationService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ExplanationService>());

var app = builder.Build();

await app.Services.GetRequiredService<PostgresLogStore>().EnsureSchemaAsync();
await app.Services.GetRequiredService<PostgresIncidentStore>().EnsureSchemaAsync();
await app.Services.GetRequiredService<PostgresAnomalyStore>().EnsureSchemaAsync();
await app.Services.GetRequiredService<PostgresExplanationStore>().EnsureSchemaAsync();
await app.Services.GetRequiredService<PostgresSettingsStore>().EnsureSchemaAsync();
await SeedProvidersAsync(app.Services.GetRequiredService<PostgresSettingsStore>(), app.Configuration);
_ = await app.Services.GetRequiredService<ExplanationRouter>().ActiveAsync(); // logs whether the layer is on

app.MapGet("/health", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    try
    {
        await using var command = db.CreateCommand("select 1");
        await command.ExecuteScalarAsync(ct);
        return Results.Ok(new { status = "ok" });
    }
    catch (NpgsqlException)
    {
        return Results.Json(new { status = "database unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

// Explains one incident now, e.g. one that started before the layer was switched on and is therefore not explained on its own
app.MapPost("/api/incidents/{id:guid}/explain", async (Guid id, HttpRequest request, IServiceProvider services, IOptions<CollectorOptions> options, CancellationToken ct) =>
{
    var config = options.Value;
    if (!string.IsNullOrEmpty(config.ApiKey) && !KeyMatches(request.Headers["X-ErrorLens-Key"].ToString(), config.ApiKey))
        return Results.Unauthorized();
    if (await services.GetRequiredService<ExplanationRouter>().ActiveAsync(ct) is null)
        return Results.Json(new { error = "LLM explanations are off" }, statusCode: StatusCodes.Status409Conflict);
    var explanations = services.GetRequiredService<ExplanationService>();

    await explanations.ExplainAsync(id, ct);
    return Results.Accepted();
});

app.MapPost("/api/ingest/logs", async (HttpRequest request, IngestBatch batch, IngestService ingest, IOptions<CollectorOptions> options, CancellationToken ct) =>
{
    var config = options.Value;
    if (!string.IsNullOrEmpty(config.ApiKey) && !KeyMatches(request.Headers["X-ErrorLens-Key"].ToString(), config.ApiKey))
        return Results.Unauthorized();

    if (string.IsNullOrWhiteSpace(batch.Source?.Application))
        return Results.BadRequest(new { error = "source.application is required" });
    if (batch.Events is null || batch.Events.Count > config.MaxEventsPerBatch)
        return Results.BadRequest(new { error = $"events must contain at most {config.MaxEventsPerBatch} items" });

    // An empty batch is how a sender announces a deployment at startup
    var result = await ingest.IngestAsync(batch, ct);
    return Results.Accepted(value: result);
});

app.Run();

static bool KeyMatches(string given, string expected) =>
    CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected));

// Every supported provider gets a row on the settings screen; the default is Explanation:Provider, else the first one
// whose key is in the configuration or the environment. Rows already there keep what was set on the screen.
static async Task SeedProvidersAsync(PostgresSettingsStore store, IConfiguration configuration)
{
    string? Key(LlmProviderInfo p) =>
        configuration[$"{SectionOf(p.Name)}:ApiKey"] is { Length: > 0 } k ? k : Environment.GetEnvironmentVariable(p.KeyVariable);
    var preferred = configuration["Explanation:Provider"]?.ToLowerInvariant()
                    ?? LlmProviders.All.FirstOrDefault(p => !string.IsNullOrWhiteSpace(Key(p)))?.Name
                    ?? LlmProviders.Gemini;

    foreach (var provider in LlmProviders.All)
    {
        var section = configuration.GetSection(SectionOf(provider.Name));
        await store.SeedProviderAsync(provider.Name, provider.DisplayName, section["Model"] ?? provider.DefaultModel,
            int.TryParse(section["MaxOutputTokens"] ?? section["MaxTokens"], out var max) ? max : 8000, provider.Name == preferred);
    }
}

static string SectionOf(string provider) => provider switch
{
    LlmProviders.Claude => ClaudeOptions.Section,
    LlmProviders.Gemini => GeminiOptions.Section,
    _ => OpenAiOptions.Section
};

/// <summary>Entry point marker for WebApplicationFactory in tests.</summary>
public partial class Program;
