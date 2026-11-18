using System.Security.Cryptography;
using System.Text;
using ErrorLensLLM.Collector;
using Anthropic;
using ErrorLensLLM.AI.Claude;
using ErrorLensLLM.Collector.Anomalies;
using ErrorLensLLM.Collector.Explanations;
using ErrorLensLLM.Collector.Incidents;
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
builder.Services.AddSingleton(new IncidentCorrelator());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IncidentAnalyzer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IncidentAnalyzer>());

builder.Services.Configure<AnomalyOptions>(builder.Configuration.GetSection(AnomalyOptions.Section));
builder.Services.AddSingleton<PostgresAnomalyStore>();
builder.Services.AddSingleton<AnomalyMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AnomalyMonitor>());

// LLM explanation layer: Claude (ANTHROPIC_API_KEY or Claude:ApiKey) or Gemini (GEMINI_API_KEY or Gemini:ApiKey).
// Explanation:Provider picks one when both keys are set; without a key the layer is off.
var claude = builder.Configuration.GetSection(ClaudeOptions.Section).Get<ClaudeOptions>() ?? new ClaudeOptions();
claude.ApiKey ??= Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
var gemini = builder.Configuration.GetSection(GeminiOptions.Section).Get<GeminiOptions>() ?? new GeminiOptions();
gemini.ApiKey ??= Environment.GetEnvironmentVariable("GEMINI_API_KEY");
var provider = builder.Configuration["Explanation:Provider"]?.ToLowerInvariant()
               ?? (!string.IsNullOrWhiteSpace(claude.ApiKey) ? "claude" : !string.IsNullOrWhiteSpace(gemini.ApiKey) ? "gemini" : null);
IIncidentExplainer? explainer = provider switch
{
    "claude" when !string.IsNullOrWhiteSpace(claude.ApiKey) => new IncidentExplainer(new AnthropicClient { ApiKey = claude.ApiKey }, claude),
    "gemini" when !string.IsNullOrWhiteSpace(gemini.ApiKey) => new GeminiExplainer(new HttpClient { Timeout = TimeSpan.FromMinutes(2) }, gemini),
    _ => null
};
builder.Services.AddSingleton<PostgresExplanationStore>();
builder.Services.AddSingleton<PostgresDashboardStore>();
if (explainer is not null)
{
    builder.Services.AddSingleton(explainer);
    builder.Services.AddHostedService<ExplanationService>();
}

var app = builder.Build();

await app.Services.GetRequiredService<PostgresLogStore>().EnsureSchemaAsync();
await app.Services.GetRequiredService<PostgresIncidentStore>().EnsureSchemaAsync();
await app.Services.GetRequiredService<PostgresAnomalyStore>().EnsureSchemaAsync();
await app.Services.GetRequiredService<PostgresExplanationStore>().EnsureSchemaAsync();
app.Logger.LogInformation(explainer is null
    ? "LLM explanations are off: set ANTHROPIC_API_KEY or GEMINI_API_KEY to turn them on"
    : "LLM explanations are on with {Model}", explainer?.Model);

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

/// <summary>Entry point marker for WebApplicationFactory in tests.</summary>
public partial class Program;
