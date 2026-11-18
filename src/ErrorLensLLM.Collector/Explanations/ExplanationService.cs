using System.Text.Json;
using Anthropic.Exceptions;
using ErrorLensLLM.AI.Claude;
using ErrorLensLLM.Collector.Settings;
using ErrorLensLLM.Storage.PostgreSql;

namespace ErrorLensLLM.Collector.Explanations;

/// <summary>
/// Asks the default LLM provider of the settings screen to explain each new incident once its members have settled, and
/// again when its root cause changes. Without a provider with a key it waits; the dashboard says the layer is off.
/// </summary>
public sealed class ExplanationService(
    ExplanationRouter explainer, RuntimeSettings settings, PostgresExplanationStore explanations, PostgresDashboardStore incidents,
    ILogger<ExplanationService> log) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Older incidents (from before the layer was switched on) are not explained retroactively
        var started = DateTimeOffset.UtcNow;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        do
        {
            try
            {
                var values = await settings.GetAsync(stoppingToken);
                var settle = TimeSpan.FromSeconds(values[SettingsCatalog.Incidents, "ExplainSettleSeconds"]);
                var retryAfter = TimeSpan.FromMinutes(values[SettingsCatalog.Incidents, "ExplainRetryMinutes"]);
                var since = started - TimeSpan.FromMinutes(values[SettingsCatalog.Incidents, "ExplainLookbackMinutes"]);

                while (await explainer.ActiveAsync(stoppingToken) is not null
                       && await explanations.NextPendingAsync(settle, retryAfter, since, stoppingToken) is { } id)
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
        var model = (await explainer.ActiveAsync(ct))?.Model ?? "-";
        try
        {
            var result = await explainer.ExplainAsync(id, context, ct);
            await explanations.SaveAsync(new ExplanationRow(
                id, detail.RootFingerprint, result.Model, JsonSerializer.Serialize(result.Explanation, Json), null,
                result.InputTokens, result.OutputTokens, DateTimeOffset.UtcNow), ct);
            log.LogInformation("Explained incident {Incident} with {Model} ({Input} in, {Output} out tokens)",
                id, result.Model, result.InputTokens, result.OutputTokens);
        }
        // A request timeout is a TaskCanceledException too; only a shutdown may stop the service
        catch (Exception ex) when (ex is AnthropicException or InvalidOperationException or JsonException or HttpRequestException or ArgumentException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            // Stored so that the dashboard can say why, and so that the incident is not retried on every tick
            await explanations.SaveAsync(new ExplanationRow(id, detail.RootFingerprint, model, null, ex.Message, 0, 0, DateTimeOffset.UtcNow), ct);
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
