using System.Text.Json;
using Anthropic.Exceptions;
using ErrorLensLLM.AI.Claude;
using ErrorLensLLM.Storage.PostgreSql;

namespace ErrorLensLLM.Collector.Explanations;

/// <summary>
/// Asks Claude to explain each new incident once its members have settled, and again when its root cause changes.
/// Only registered when an API key is configured; without one the dashboard says the layer is off.
/// </summary>
public sealed class ExplanationService(
    IncidentExplainer explainer, PostgresExplanationStore explanations, PostgresDashboardStore incidents,
    ILogger<ExplanationService> log) : BackgroundService
{
    /// <summary>An incident is explained this long after it was detected, when the correlator has seen its first wave.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Older incidents (from before the layer was switched on) are not explained retroactively
        var since = DateTimeOffset.UtcNow.AddHours(-1);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        do
        {
            try
            {
                while (await explanations.NextPendingAsync(Settle, RetryAfter, since, stoppingToken) is { } id)
                    await ExplainAsync(id, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Incident explanation failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task ExplainAsync(Guid id, CancellationToken ct)
    {
        var detail = await incidents.GetIncidentAsync(id, ct);
        if (detail is null)
            return;

        var context = await BuildContextAsync(detail, ct);
        try
        {
            var result = await explainer.ExplainAsync(context, ct);
            await explanations.SaveAsync(new ExplanationRow(
                id, detail.RootFingerprint, result.Model, JsonSerializer.Serialize(result.Explanation, Json), null,
                result.InputTokens, result.OutputTokens, DateTimeOffset.UtcNow), ct);
            log.LogInformation("Explained incident {Incident} with {Model} ({Input} in, {Output} out tokens)",
                id, result.Model, result.InputTokens, result.OutputTokens);
        }
        // A request timeout is a TaskCanceledException too; only a shutdown may stop the service
        catch (Exception ex) when (ex is AnthropicException or InvalidOperationException or JsonException or HttpRequestException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            // Stored so that the dashboard can say why, and so that the incident is not retried on every tick
            await explanations.SaveAsync(new ExplanationRow(id, detail.RootFingerprint, explainer.Model, null, ex.Message, 0, 0, DateTimeOffset.UtcNow), ct);
            log.LogWarning(ex, "Could not explain incident {Incident}", id);
        }
    }

    private async Task<IncidentContext> BuildContextAsync(IncidentDetail detail, CancellationToken ct)
    {
        var summary = detail.Summary;
        var chain = await incidents.SampleTraceAsync(summary.Id, detail.RootFingerprint, ct);
        var stack = await incidents.SampleStackTraceAsync(detail.RootFingerprint, ct);
        var anomalies = await incidents.RecentAnomaliesAsync(summary.StartedAt.AddMinutes(-2), 50, ct);

        var deployment = detail.SuspectedDeployment is { } d
            ? $"{d.Application} {d.Version} (commit {d.CommitSha ?? "bilinmiyor"}), {d.StartedAt:u}"
            : null;

        return new IncidentContext(
            summary.Title, summary.Category, summary.StartedAt, summary.LastSeen, summary.Occurrences, summary.Applications, deployment,
            detail.Members.Take(8).ToList(),
            chain.Take(20).Select(e => $"{e.Timestamp:HH:mm:ss.fff} [{e.Application}] {e.Level}: {e.Message}" +
                                       (e.ExceptionType is null ? "" : $" ({e.ExceptionType}: {e.ExceptionMessage})")).ToList(),
            stack is null ? null : string.Join('\n', stack.Split('\n').Take(40)),
            anomalies
                .Where(a => summary.Applications.Contains(a.Application) && a.BucketStart <= summary.LastSeen)
                .OrderBy(a => a.BucketStart).Take(10)
                .Select(a => $"{a.BucketStart:HH:mm:ss} {a.Application}: " +
                             string.Join(", ", new[]
                             {
                                 a.DeepLog ? $"DeepLog beklenmeyen olay oranı %{a.DeepLogShare * 100:0}" : null,
                                 a.Forest ? $"Isolation Forest skoru {a.ForestScore:0.00}" : null,
                                 a.Volume ? $"log hacmi {a.Events}" : null
                             }.Where(s => s is not null)) +
                             (a.UnseenTemplates.Count > 0 ? $"; yeni şablonlar: {string.Join(" | ", a.UnseenTemplates.Take(3))}" : ""))
                .ToList());
    }
}
