using ErrorLensLLM.Core.Incidents;
using ErrorLensLLM.Storage.PostgreSql;
using Microsoft.Extensions.Options;

namespace ErrorLensLLM.Collector.Incidents;

public sealed class AnalysisOptions
{
    public const string Section = "Analysis";

    public int IntervalSeconds { get; set; } = 10;

    /// <summary>Errors and deployments of this period are correlated on every run.</summary>
    public int WindowMinutes { get; set; } = 60;

    /// <summary>An incident with no new errors for this long is closed.</summary>
    public int CloseAfterMinutes { get; set; } = 15;
}

/// <summary>Periodically correlates recent errors into incidents and stores them.</summary>
public sealed class IncidentAnalyzer(
    PostgresIncidentStore store, IncidentCorrelator correlator, IOptions<AnalysisOptions> options, TimeProvider clock, ILogger<IncidentAnalyzer> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.IntervalSeconds), clock);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed run is retried on the next tick; collection keeps working meanwhile
                log.LogError(ex, "Incident analysis failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var since = now.AddMinutes(-options.Value.WindowMinutes);

        var errors = await store.LoadErrorsAsync(since, ct);
        // Deployments a little older than the window can still explain its first errors
        var deployments = await store.LoadDeploymentsAsync(since.AddMinutes(-10), ct);

        var quiet = TimeSpan.FromMinutes(options.Value.CloseAfterMinutes);
        await store.CloseQuietAsync(now - quiet, ct);

        // Oldest first, so that a recurrence finds the earlier incident already stored (and only joins it if close in time)
        var incidents = correlator.Correlate(errors, deployments);
        foreach (var incident in incidents.OrderBy(i => i.StartedAt))
            await store.SaveAsync(incident, Title(incident), activeSince: incident.StartedAt - quiet, ct);

        return incidents.Count;
    }

    /// <summary>"kayit 1.1.0 sonrası Npgsql.PostgresException — 3 modül, 920 hata".</summary>
    public static string Title(IncidentDraft incident)
    {
        var root = incident.RootCause;
        var trigger = incident.SuspectedDeployment is { } d ? $"{d.Application} {d.Version} sonrası " : $"{root.Application}: ";
        var type = root.ExceptionType[(root.ExceptionType.LastIndexOf('.') + 1)..];
        var modules = incident.Applications.Count();
        return $"{trigger}{type} — {(modules > 1 ? $"{modules} modül, " : "")}{incident.Occurrences} hata";
    }
}
