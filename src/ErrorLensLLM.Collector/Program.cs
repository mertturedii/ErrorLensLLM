using System.Security.Cryptography;
using System.Text;
using ErrorLensLLM.Collector;
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

var app = builder.Build();

await app.Services.GetRequiredService<PostgresLogStore>().EnsureSchemaAsync();
await app.Services.GetRequiredService<PostgresIncidentStore>().EnsureSchemaAsync();

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
