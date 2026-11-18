using System.Net.Http.Json;
using ErrorLensLLM.Collector.Incidents;
using ErrorLensLLM.Core.Ingestion;
using ErrorLensLLM.Core.Models;
using ErrorLensLLM.Storage.PostgreSql;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ErrorLensLLM.Collector.Tests;

/// <summary>The incident analyzer against a throwaway database, driven with its own clock.</summary>
public sealed class IncidentAnalyzerTests : IAsyncLifetime
{
    private const string Admin = "Host=127.0.0.1;Port=5432;Database=errorlens;Username=errorlens;Password=errorlens_dev";
    private readonly string _database = $"errorlens_test_{Guid.NewGuid():N}"[..30];
    private WebApplicationFactory<Program> _factory = null!;

    private string ConnectionString => new NpgsqlConnectionStringBuilder(Admin) { Database = _database }.ConnectionString;

    private sealed class FixedClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public async Task InitializeAsync()
    {
        await using var admin = NpgsqlDataSource.Create(Admin);
        await using var create = admin.CreateCommand($"create database {_database}");
        await create.ExecuteNonQueryAsync();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:ErrorLens", ConnectionString);
            b.UseSetting("Anomaly:Enabled", "false");
            // The background analyzer runs once at start and then not for an hour; the test drives its own
            b.UseSetting("Analysis:IntervalSeconds", "3600");
        });
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var admin = NpgsqlDataSource.Create(Admin);
        await using var drop = admin.CreateCommand($"drop database if exists {_database} with (force)");
        await drop.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Closed_incident_is_not_stored_again_while_its_errors_are_in_the_window()
    {
        var client = _factory.CreateClient();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-40);
        var error = new IngestException("System.TimeoutException", "Banka yanit vermedi", "",
            [new StackFrameInfo("Demo.OdemeService", "TahsilEtAsync", null, null, "Demo", true)]);
        var events = Enumerable.Range(0, 10)
            .Select(i => new IngestLogEvent { Timestamp = t0.AddSeconds(i), Level = "Error", Message = "Odeme basarisiz", Exception = error })
            .ToList();
        (await client.PostAsJsonAsync("/api/ingest/logs", new IngestBatch
        {
            Source = new IngestSource { Application = "odeme", Version = "1.0.0", StartedAt = t0.AddMinutes(-5) },
            Events = events
        })).EnsureSuccessStatusCode();

        var clock = new FixedClock();
        var analyzer = new IncidentAnalyzer(_factory.Services.GetRequiredService<PostgresIncidentStore>(),
            Options.Create(new AnalysisOptions()), clock, NullLogger<IncidentAnalyzer>.Instance);

        // Every run within the hour sees the same errors; the incident closes 15 min after them and must stay one incident
        for (var minutes = 1; minutes <= 45; minutes += 4)
        {
            clock.Now = t0.AddMinutes(minutes);
            await analyzer.RunOnceAsync(CancellationToken.None);
        }

        await using var db = NpgsqlDataSource.Create(ConnectionString);
        await using var count = db.CreateCommand("select count(*), min(status), max(status) from incidents where status <> 'merged'");
        await using var reader = await count.ExecuteReaderAsync();
        await reader.ReadAsync();
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal("closed", reader.GetString(1));
    }
}
