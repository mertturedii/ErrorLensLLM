using System.Collections.Concurrent;
using ErrorLensLLM.Core.Ingestion;
using ErrorLensLLM.Storage.PostgreSql;

namespace ErrorLensLLM.Collector.Ingestion;

public sealed record IngestResult(int Events, int Errors, Guid DeploymentId);

/// <summary>Stores a prepared batch, resolving template ids and the deployment of its source (both cached).</summary>
public sealed class IngestService(PostgresLogStore store, IngestPipeline pipeline)
{
    private readonly ConcurrentDictionary<(string Text, string Source), int> _templateIds = new();
    private readonly ConcurrentDictionary<string, Guid> _deployments = new();

    public async Task<IngestResult> IngestAsync(IngestBatch batch, CancellationToken ct)
    {
        var deploymentId = await DeploymentAsync(batch.Source, ct);
        var prepared = pipeline.Prepare(batch);

        var rows = new List<LogEventRow>(prepared.Events.Count);
        foreach (var e in prepared.Events)
        {
            var templateId = await TemplateIdAsync(e.TemplateText, e.TemplateSource, ct);
            rows.Add(e.Row with { TemplateId = templateId, DeploymentId = deploymentId });
        }

        await store.SaveAsync(rows, prepared.Errors, ct);
        return new IngestResult(rows.Count, prepared.Errors.Count, deploymentId);
    }

    private async Task<int> TemplateIdAsync(string text, string source, CancellationToken ct)
    {
        if (_templateIds.TryGetValue((text, source), out var id))
            return id;

        id = await store.GetOrCreateTemplateAsync(text, source, ct);
        _templateIds[(text, source)] = id;
        return id;
    }

    private async Task<Guid> DeploymentAsync(IngestSource source, CancellationToken ct)
    {
        var startedAt = source.StartedAt.ToUniversalTime();
        var key = string.Join('|', source.Application, source.Environment, source.Host, source.Version, source.CommitSha, startedAt.ToString("O"));
        if (_deployments.TryGetValue(key, out var id))
            return id;

        id = await store.RecordDeploymentAsync(source.Application, source.Environment, source.Host, source.Version, source.CommitSha, startedAt, ct);
        _deployments[key] = id;
        return id;
    }
}
