using ErrorLensLLM.Collector.Settings;
using ErrorLensLLM.Ml.Streaming;
using ErrorLensLLM.Storage.PostgreSql;
using Microsoft.Extensions.Options;

namespace ErrorLensLLM.Collector.Anomalies;

public sealed class AnomalyOptions
{
    public const string Section = "Anomaly";

    public bool Enabled { get; set; } = true;

    /// <summary>Each module's stream is scored in buckets of this length.</summary>
    public int BucketSeconds { get; set; } = 10;

    /// <summary>A bucket is scored this long after it ends, so that events still in a sender's batch arrive first.</summary>
    public int LagSeconds { get; set; } = 5;

    /// <summary>A model trains on the error-free buckets of this period ...</summary>
    public int TrainingMinutes { get; set; } = 10;

    /// <summary>... once there are at least this many of them ...</summary>
    public int MinTrainingBuckets { get; set; } = 18;

    /// <summary>... with at least this many events in total.</summary>
    public int MinTrainingEvents { get; set; } = 200;

    /// <summary>Models are retrained this often, so that a healthy release's new lines become normal.</summary>
    public int RetrainMinutes { get; set; } = 30;
}

/// <summary>
/// Live anomaly detection: trains a <see cref="StreamAnomalyModel"/> (DeepLog + Isolation Forest + volume rule) per module
/// on its recent error-free log stream and scores every new bucket of that stream. Scores go to <c>anomaly_scores</c>,
/// where the dashboard shows them next to the incidents. Training treats buckets without Error / Fatal lines as normal;
/// a fault that logs no error can therefore be learned as normal at the next retrain.
/// </summary>
public sealed class AnomalyMonitor(
    PostgresAnomalyStore store, IOptions<AnomalyOptions> options, TimeProvider clock, ILogger<AnomalyMonitor> log,
    RuntimeSettings? settings = null) : BackgroundService
{
    private sealed class ModuleState(StreamAnomalyModel model, DateTimeOffset trainedAt, List<int> context)
    {
        public StreamAnomalyModel Model { get; } = model;
        public DateTimeOffset TrainedAt { get; } = trainedAt;
        public List<int> Context { get; set; } = context;
    }

    private readonly Dictionary<string, ModuleState> _modules = new(StringComparer.Ordinal);
    private DateTimeOffset? _nextBucket;
    private DateTimeOffset _lastTrainingCheck = DateTimeOffset.MinValue;

    private TimeSpan Bucket => TimeSpan.FromSeconds(options.Value.BucketSeconds);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
            return;

        using var timer = new PeriodicTimer(Bucket, clock);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Anomaly scoring failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public override void Dispose()
    {
        foreach (var module in _modules.Values)
            module.Model.Dispose();
        base.Dispose();
    }

    /// <summary>Trains the modules that are due, then scores every complete bucket since the last run.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        var end = Floor(clock.GetUtcNow() - TimeSpan.FromSeconds(options.Value.LagSeconds));

        // A restarted monitor carries on from the stored scores, but does not backfill more than a minute
        var backfillFrom = end - TimeSpan.FromMinutes(1);
        _nextBucket ??= Max((await store.LastBucketAsync(ct))?.Add(Bucket) ?? backfillFrom, backfillFrom);

        await TrainDueAsync(end, ct);

        if (_nextBucket >= end || _modules.Count == 0)
        {
            _nextBucket = Max(_nextBucket.Value, end);
            return;
        }

        var from = _nextBucket.Value;
        var events = (await store.LoadEventsAsync(from, end, ct)).ToLookup(e => e.Application);
        var rows = new List<AnomalyScoreRow>();

        foreach (var (application, module) in _modules)
        {
            var stream = events[application].ToList();
            for (var bucket = from; bucket < end; bucket += Bucket)
            {
                var templates = stream.Where(e => e.Timestamp >= bucket && e.Timestamp < bucket + Bucket).Select(e => e.TemplateId).ToArray();
                var score = module.Model.Score(templates, module.Context);
                module.Context = module.Context.Concat(templates).TakeLast(module.Model.ContextSize).ToList();

                rows.Add(new AnomalyScoreRow(
                    bucket, application, score.Events, score.AnomalousEvents,
                    score.DeepLogShare, module.Model.DeepLogThreshold, score.DeepLogAnomaly,
                    score.ForestScore, module.Model.ForestThreshold, score.ForestAnomaly,
                    score.VolumeAnomaly, score.UnseenTemplates.ToArray()));
            }
        }

        await store.SaveScoresAsync(rows, ct);
        _nextBucket = end;

        foreach (var row in rows.Where(r => r.IsAnomaly))
        {
            log.LogInformation("Anomaly in {Application} at {Bucket:HH:mm:ss}: deeplog {Share:P0}, forest {Forest:F2}, volume {Volume}, {Unseen} unseen templates",
                row.Application, row.BucketStart, row.DeepLogShare, row.ForestScore, row.VolumeAnomaly, row.UnseenTemplates.Length);
        }
    }

    private async Task TrainDueAsync(DateTimeOffset end, CancellationToken ct)
    {
        var config = options.Value;
        // Training period, retrain interval, minimums and thresholds come from the settings screen when it has values
        var values = settings is null ? null : await settings.GetAsync(ct);
        double Value(string key, double fallback) =>
            values is null || values.IsDefault(SettingsCatalog.Anomaly, key) ? fallback : values[SettingsCatalog.Anomaly, key];
        var retrain = TimeSpan.FromMinutes(Value("RetrainMinutes", config.RetrainMinutes));
        var trainingMinutes = Value("TrainingMinutes", config.TrainingMinutes);
        var minBuckets = Value("MinTrainingBuckets", config.MinTrainingBuckets);
        var minEvents = Value("MinTrainingEvents", config.MinTrainingEvents);
        var streamOptions = values is null ? null : RuntimeSettings.Stream(values);
        var now = clock.GetUtcNow();

        // Loading the training period is the expensive part; look for new or stale modules once a minute
        if (now - _lastTrainingCheck < TimeSpan.FromMinutes(1))
            return;
        _lastTrainingCheck = now;

        var from = end - TimeSpan.FromMinutes(trainingMinutes);
        var events = await store.LoadEventsAsync(from, end, ct);

        foreach (var stream in events.GroupBy(e => e.Application))
        {
            if (_modules.TryGetValue(stream.Key, out var existing) && now - existing.TrainedAt < retrain)
                continue;

            // From the module's first bucket in the period, so that a module started midway does not look silent before it
            var first = Floor(stream.First().Timestamp);
            var buckets = new List<(DateTimeOffset Start, int[] Templates)>();
            for (var bucket = first; bucket < end; bucket += Bucket)
            {
                var inBucket = stream.Where(e => e.Timestamp >= bucket && e.Timestamp < bucket + Bucket).ToList();
                if (inBucket.Any(e => e.Level is "Error" or "Fatal"))
                    continue;
                buckets.Add((bucket, inBucket.Select(e => e.TemplateId).ToArray()));
            }

            var eventCount = buckets.Sum(b => b.Templates.Length);
            if (buckets.Count < minBuckets || eventCount < minEvents)
                continue;

            var model = await Task.Run(() => StreamAnomalyModel.Train(buckets.Select(b => b.Templates).ToList(), streamOptions), ct);
            var context = buckets.SelectMany(b => b.Templates).TakeLast(model.ContextSize).ToList();

            if (existing is not null)
            {
                existing.Model.Dispose();
                // Keep scoring from where the old model left off
                context = existing.Context;
            }

            _modules[stream.Key] = new ModuleState(model, now, context);
            await store.SaveModelAsync(new AnomalyModelRow(
                stream.Key, now, buckets[0].Start, buckets[^1].Start + Bucket, buckets.Count, eventCount, model.Templates, model.TopG,
                model.HoldoutEventRate, model.DeepLogThreshold, model.ForestThreshold, model.MeanEvents, model.StdEvents), ct);

            log.LogInformation("Anomaly model for {Application} trained on {Buckets} buckets ({Events} events, {Templates} templates): top-g {TopG}, " +
                               "deeplog threshold {DeepLog:P0}, forest threshold {Forest:F3}",
                stream.Key, buckets.Count, eventCount, model.Templates, model.TopG, model.DeepLogThreshold, model.ForestThreshold);
        }
    }

    private DateTimeOffset Floor(DateTimeOffset time) =>
        new(time.UtcTicks - time.UtcTicks % Bucket.Ticks, TimeSpan.Zero);

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
