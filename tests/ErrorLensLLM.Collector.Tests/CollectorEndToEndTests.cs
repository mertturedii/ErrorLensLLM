using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using ErrorLensLLM.Core.Ingestion;
using ErrorLensLLM.Core.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using ErrorLensLLM.Serilog;
using Npgsql;
using Serilog;
using Serilog.Configuration;

namespace ErrorLensLLM.Collector.Tests;

/// <summary>
/// Runs the collector in-process against the local PostgreSQL (deploy/docker-compose.yml) using a throwaway
/// database, so the development data in "errorlens" is never touched.
/// </summary>
public sealed class CollectorEndToEndTests : IAsyncLifetime
{
    private const string Admin = "Host=127.0.0.1;Port=5432;Database=errorlens;Username=errorlens;Password=errorlens_dev";
    private readonly string _database = $"errorlens_test_{Guid.NewGuid():N}"[..30];
    private WebApplicationFactory<Program> _factory = null!;

    private string ConnectionString => new NpgsqlConnectionStringBuilder(Admin) { Database = _database }.ConnectionString;

    public async Task InitializeAsync()
    {
        await using var admin = NpgsqlDataSource.Create(Admin);
        await using var create = admin.CreateCommand($"create database {_database}");
        await create.ExecuteNonQueryAsync();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:ErrorLens", ConnectionString);
            b.UseSetting("Collector:ApiKey", "test-key");
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
    public async Task Batch_is_masked_templated_grouped_and_stored()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-ErrorLens-Key", "test-key");

        var source = new IngestSource { Application = "kayit", Environment = "test", Version = "1.1.0", CommitSha = "abc123", StartedAt = DateTimeOffset.UtcNow };
        var error = new IngestException("Npgsql.PostgresException", "column \"donem_id\" does not exist", "trace",
            [new StackFrameInfo("Demo.KayitService", "Kaydet", null, null, "Demo", true)]);

        var response = await client.PostAsJsonAsync("/api/ingest/logs", new IngestBatch
        {
            Source = source,
            Events =
            [
                new() { Timestamp = DateTimeOffset.UtcNow, Level = "Information", Message = "Kayit 42 acildi", MessageTemplate = "Kayit {Id} acildi", TraceId = "4bf92f3577b34da6a3ce929d0e0e4736" },
                new() { Timestamp = DateTimeOffset.UtcNow, Level = "Error", Message = "Kayit hatasi ali@ornek.edu.tr", Exception = error },
                new() { Timestamp = DateTimeOffset.UtcNow, Level = "Error", Message = "Kayit hatasi veli@ornek.edu.tr", Exception = error }
            ]
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // A second batch from the same process start must reuse the deployment
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/ingest/logs", new IngestBatch { Source = source, Events = [] })).StatusCode);

        await using var db = NpgsqlDataSource.Create(ConnectionString);
        Assert.Equal(3L, await Scalar(db, "select count(*) from log_events"));
        Assert.Equal(1L, await Scalar(db, "select count(*) from deployments"));
        Assert.Equal(2L, await Scalar(db, "select occurrence_count from error_groups"));
        Assert.Equal(0L, await Scalar(db, "select count(*) from log_events where message like '%@%'"));
        Assert.Equal("Kayit {Id} acildi", await Scalar(db, "select t.text from log_events e join log_templates t on t.id = e.template_id where e.level = 'Information'"));
        Assert.Equal(1L, await Scalar(db, "select count(*) from log_events where trace_id = '4bf92f3577b34da6a3ce929d0e0e4736'"));
    }

    [Fact]
    public async Task Serilog_sink_delivers_structured_logs_with_trace_ids()
    {
        var sink = new ErrorLensSink(
            new ErrorLensSinkOptions { CollectorUrl = new Uri("http://localhost/"), Application = "not", Environment = "test", ApiKey = "test-key" },
            _factory.Server.CreateHandler());

        string traceId;
        using (var logger = new LoggerConfiguration()
                   .WriteTo.Sink(sink, new BatchingOptions { BatchSizeLimit = 100, BufferingTimeLimit = TimeSpan.FromMilliseconds(50) })
                   .CreateLogger())
        {
            using var activity = new Activity("GET /notlar").Start();
            traceId = activity.TraceId.ToHexString();

            logger.Information("Not girisi {DersKodu} icin acildi", "BLM101");
            logger.Warning("Ders \"{DersKodu}\" bulunamadi", "XYZ999");
            try { throw new InvalidOperationException("Donem kapali"); }
            catch (Exception ex) { logger.Error(ex, "Not kaydedilemedi {DersKodu}", "BLM101"); }
        } // disposing the logger flushes the batch

        await using var db = NpgsqlDataSource.Create(ConnectionString);
        Assert.Equal(3L, await Scalar(db, $"select count(*) from log_events where application = 'not' and trace_id = '{traceId}'"));
        Assert.Equal("Ders \"XYZ999\" bulunamadi", await Scalar(db, "select message from log_events where level = 'Warning'"));
        Assert.Equal("Not girisi {DersKodu} icin acildi", await Scalar(db, "select t.text from log_events e join log_templates t on t.id = e.template_id where e.level = 'Information'"));
        Assert.Equal("BLM101", await Scalar(db, "select properties ->> 'DersKodu' from log_events where level = 'Error'"));
        Assert.Equal("System.InvalidOperationException", await Scalar(db, "select exception_type from error_groups"));
        Assert.Contains(nameof(Serilog_sink_delivers_structured_logs_with_trace_ids), (string)(await Scalar(db, "select top_frame from error_groups"))!);
    }

    [Theory]
    [InlineData("1.4.0+3f2a9c1", "1.4.0", "3f2a9c1")]
    [InlineData("1.4.0", "1.4.0", null)]
    public void Informational_version_splits_into_version_and_commit(string informational, string version, string? commit)
    {
        Assert.Equal((version, commit), ErrorLensSink.ParseInformationalVersion(informational));
    }

    [Fact]
    public async Task Missing_api_key_is_rejected()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/ingest/logs",
            new IngestBatch { Source = new IngestSource { Application = "kayit", StartedAt = DateTimeOffset.UtcNow }, Events = [] });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Health_reports_database_status()
    {
        var response = await _factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Roll_back_within_minutes_is_a_new_deployment()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-ErrorLens-Key", "test-key");
        var t0 = DateTimeOffset.UtcNow;

        async Task Announce(string version, DateTimeOffset startedAt) =>
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/ingest/logs", new IngestBatch
            {
                Source = new IngestSource { Application = "kayit", Environment = "test", Version = version, StartedAt = startedAt },
                Events = []
            })).StatusCode);

        await Announce("1.0.0", t0);
        await Announce("1.0.0", t0.AddSeconds(2));   // a second instance of the same release
        await Announce("1.1.0", t0.AddSeconds(60));
        await Announce("1.0.0", t0.AddSeconds(120)); // roll-back, within five minutes of the first 1.0.0

        await using var db = NpgsqlDataSource.Create(ConnectionString);
        Assert.Equal(3L, await Scalar(db, "select count(*) from deployments"));
        Assert.Equal("1.0.0", await Scalar(db, "select version from deployments order by started_at desc limit 1"));
    }

    private static async Task<object?> Scalar(NpgsqlDataSource db, string sql)
    {
        await using var command = db.CreateCommand(sql);
        return await command.ExecuteScalarAsync();
    }
}
