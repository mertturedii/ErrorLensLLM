using System.Net.Http.Json;
using ErrorLensLLM.Collector.Anomalies;
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

/// <summary>Trains the live anomaly model on a stored stream and scores a faulty period, in a throwaway database.</summary>
public sealed class AnomalyMonitorTests : IAsyncLifetime
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
            // The test drives the monitor itself, with its own clock
            b.UseSetting("Anomaly:Enabled", "false");
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
    public async Task Healthy_stream_trains_a_model_and_a_new_error_template_is_flagged()
    {
        var client = _factory.CreateClient();
        var t0 = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60 * 60 - 600);
        var source = new IngestSource { Application = "kayit", Version = "1.0.0", StartedAt = t0 };

        // Five healthy minutes: every request logs "fetched" then "listed", two requests per second
        var healthy = Enumerable.Range(0, 600).SelectMany(i => new[]
        {
            new IngestLogEvent { Timestamp = t0.AddMilliseconds(i * 500), Level = "Information", Message = $"Ogrenci {i} getirildi", MessageTemplate = "Ogrenci {Id} getirildi" },
            new IngestLogEvent { Timestamp = t0.AddMilliseconds(i * 500 + 10), Level = "Information", Message = $"Ogrenci {i} listelendi", MessageTemplate = "Ogrenci {Id} listelendi" }
        }).ToList();

        // Then thirty seconds in which the query fails instead of listing
        var error = new IngestException("Npgsql.PostgresException", "column \"donem_id\" does not exist", "",
            [new StackFrameInfo("Demo.KayitService", "GetirAsync", null, null, "Demo", true)]);
        var faulty = Enumerable.Range(600, 60).SelectMany(i => new[]
        {
            new IngestLogEvent { Timestamp = t0.AddMilliseconds(i * 500), Level = "Information", Message = $"Ogrenci {i} getirildi", MessageTemplate = "Ogrenci {Id} getirildi" },
            new IngestLogEvent { Timestamp = t0.AddMilliseconds(i * 500 + 10), Level = "Error", Message = "Sorgu basarisiz", Exception = error }
        }).ToList();

        foreach (var chunk in healthy.Concat(faulty).Chunk(1000))
            (await client.PostAsJsonAsync("/api/ingest/logs", new IngestBatch { Source = source, Events = chunk })).EnsureSuccessStatusCode();

        var store = _factory.Services.GetRequiredService<PostgresAnomalyStore>();
        var clock = new FixedClock { Now = t0.AddMinutes(5).AddSeconds(5) };
        var monitor = new AnomalyMonitor(store, Options.Create(new AnomalyOptions()), clock, NullLogger<AnomalyMonitor>.Instance);

        // First run: trains on the healthy minutes, then scores the last healthy minute
        await monitor.RunOnceAsync(CancellationToken.None);
        var model = Assert.Single(await store.ModelsAsync());
        Assert.Equal("kayit", model.Application);
        Assert.Equal(2, model.Templates);

        var healthyScores = await store.ScoresAsync(t0, t0.AddMinutes(5));
        Assert.Equal(6, healthyScores.Count);
        Assert.All(healthyScores, s => Assert.False(s.DeepLogAnomaly || s.VolumeAnomaly, $"{s.BucketStart:HH:mm:ss} share {s.DeepLogShare}"));

        // Second run: the faulty half minute
        clock.Now = t0.AddMinutes(5).AddSeconds(35);
        await monitor.RunOnceAsync(CancellationToken.None);
        var faultyScores = await store.ScoresAsync(t0.AddMinutes(5), t0.AddMinutes(6));

        Assert.Equal(3, faultyScores.Count);
        Assert.All(faultyScores, s =>
        {
            Assert.True(s.DeepLogAnomaly);
            Assert.Single(s.UnseenTemplates);
            Assert.InRange(s.DeepLogShare, 0.45, 0.55);
        });
    }
}
