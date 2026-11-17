using Npgsql;

namespace ErrorLensLLM.Storage.PostgreSql;

/// <summary>One log event as the anomaly monitor sees it: when, which module, which template, which level.</summary>
public sealed record StreamEvent(DateTimeOffset Timestamp, string Application, int TemplateId, string Level);

/// <summary>A stored bucket score; see <c>BucketScore</c> in ErrorLensLLM.Ml.</summary>
public sealed record AnomalyScoreRow(
    DateTimeOffset BucketStart, string Application, int Events, int AnomalousEvents,
    double DeepLogShare, double DeepLogThreshold, bool DeepLogAnomaly,
    double ForestScore, double ForestThreshold, bool ForestAnomaly,
    bool VolumeAnomaly, int[] UnseenTemplates)
{
    public bool IsAnomaly => DeepLogAnomaly || ForestAnomaly || VolumeAnomaly;
}

/// <summary>What a module's current model was trained on and the thresholds it chose.</summary>
public sealed record AnomalyModelRow(
    string Application, DateTimeOffset TrainedAt, DateTimeOffset TrainedFrom, DateTimeOffset TrainedTo, int Buckets, int Events,
    int Templates, int TopG, double HoldoutEventRate, double DeepLogThreshold, double ForestThreshold, double MeanEvents, double StdEvents);

/// <summary>Reads module streams for the live anomaly monitor and stores its scores and models.</summary>
public sealed class PostgresAnomalyStore(NpgsqlDataSource dataSource)
{
    private const string Schema = """
        create table if not exists anomaly_scores (
            application       text not null,
            bucket_start      timestamptz not null,
            events            int not null,
            anomalous_events  int not null,
            deeplog_share     double precision not null,
            deeplog_threshold double precision not null,
            deeplog_anomaly   boolean not null,
            forest_score      double precision not null,
            forest_threshold  double precision not null,
            forest_anomaly    boolean not null,
            volume_anomaly    boolean not null,
            unseen_templates  int[] not null,
            primary key (application, bucket_start)
        );
        create index if not exists ix_anomaly_scores_bucket on anomaly_scores (bucket_start);

        create table if not exists anomaly_models (
            application        text primary key,
            trained_at         timestamptz not null,
            trained_from       timestamptz not null,
            trained_to         timestamptz not null,
            buckets            int not null,
            events             int not null,
            templates          int not null,
            top_g              int not null,
            holdout_event_rate double precision not null,
            deeplog_threshold  double precision not null,
            forest_threshold   double precision not null,
            mean_events        double precision not null,
            std_events         double precision not null
        );
        """;

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand(Schema);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Events with a timestamp in [from, to), in log order per module.</summary>
    public async Task<IReadOnlyList<StreamEvent>> LoadEventsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            select timestamp, application, template_id, level from log_events
            where timestamp >= $1 and timestamp < $2
            order by application, timestamp, id
            """);
        command.Parameters.Add(new() { Value = from.ToUniversalTime() });
        command.Parameters.Add(new() { Value = to.ToUniversalTime() });

        var events = new List<StreamEvent>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            events.Add(new StreamEvent(reader.GetFieldValue<DateTimeOffset>(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3)));

        return events;
    }

    public async Task SaveScoresAsync(IReadOnlyList<AnomalyScoreRow> scores, CancellationToken ct = default)
    {
        if (scores.Count == 0)
            return;

        await using var batch = dataSource.CreateBatch();
        foreach (var s in scores)
        {
            var command = new NpgsqlBatchCommand("""
                insert into anomaly_scores (application, bucket_start, events, anomalous_events, deeplog_share, deeplog_threshold,
                                            deeplog_anomaly, forest_score, forest_threshold, forest_anomaly, volume_anomaly, unseen_templates)
                values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)
                on conflict (application, bucket_start) do nothing
                """);
            command.Parameters.Add(new() { Value = s.Application });
            command.Parameters.Add(new() { Value = s.BucketStart.ToUniversalTime() });
            command.Parameters.Add(new() { Value = s.Events });
            command.Parameters.Add(new() { Value = s.AnomalousEvents });
            command.Parameters.Add(new() { Value = s.DeepLogShare });
            command.Parameters.Add(new() { Value = s.DeepLogThreshold });
            command.Parameters.Add(new() { Value = s.DeepLogAnomaly });
            command.Parameters.Add(new() { Value = s.ForestScore });
            command.Parameters.Add(new() { Value = s.ForestThreshold });
            command.Parameters.Add(new() { Value = s.ForestAnomaly });
            command.Parameters.Add(new() { Value = s.VolumeAnomaly });
            command.Parameters.Add(new() { Value = s.UnseenTemplates });
            batch.BatchCommands.Add(command);
        }

        await batch.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveModelAsync(AnomalyModelRow m, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            insert into anomaly_models (application, trained_at, trained_from, trained_to, buckets, events, templates, top_g,
                                        holdout_event_rate, deeplog_threshold, forest_threshold, mean_events, std_events)
            values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13)
            on conflict (application) do update set
                trained_at = excluded.trained_at, trained_from = excluded.trained_from, trained_to = excluded.trained_to,
                buckets = excluded.buckets, events = excluded.events, templates = excluded.templates, top_g = excluded.top_g,
                holdout_event_rate = excluded.holdout_event_rate, deeplog_threshold = excluded.deeplog_threshold,
                forest_threshold = excluded.forest_threshold, mean_events = excluded.mean_events, std_events = excluded.std_events
            """);
        foreach (var value in new object[]
                 {
                     m.Application, m.TrainedAt.ToUniversalTime(), m.TrainedFrom.ToUniversalTime(), m.TrainedTo.ToUniversalTime(), m.Buckets,
                     m.Events, m.Templates, m.TopG, m.HoldoutEventRate, m.DeepLogThreshold, m.ForestThreshold, m.MeanEvents, m.StdEvents
                 })
            command.Parameters.Add(new() { Value = value });

        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Models of every module, for the dashboard.</summary>
    public async Task<IReadOnlyList<AnomalyModelRow>> ModelsAsync(CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            select application, trained_at, trained_from, trained_to, buckets, events, templates, top_g, holdout_event_rate,
                   deeplog_threshold, forest_threshold, mean_events, std_events
            from anomaly_models order by application
            """);
        var models = new List<AnomalyModelRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            models.Add(new AnomalyModelRow(
                reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetFieldValue<DateTimeOffset>(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7),
                reader.GetDouble(8), reader.GetDouble(9), reader.GetDouble(10), reader.GetDouble(11), reader.GetDouble(12)));
        }

        return models;
    }

    /// <summary>Scores of every module with a bucket in [from, to), oldest first.</summary>
    public async Task<IReadOnlyList<AnomalyScoreRow>> ScoresAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            select bucket_start, application, events, anomalous_events, deeplog_share, deeplog_threshold, deeplog_anomaly,
                   forest_score, forest_threshold, forest_anomaly, volume_anomaly, unseen_templates
            from anomaly_scores where bucket_start >= $1 and bucket_start < $2
            order by bucket_start, application
            """);
        command.Parameters.Add(new() { Value = from.ToUniversalTime() });
        command.Parameters.Add(new() { Value = to.ToUniversalTime() });

        var scores = new List<AnomalyScoreRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            scores.Add(new AnomalyScoreRow(
                reader.GetFieldValue<DateTimeOffset>(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3),
                reader.GetDouble(4), reader.GetDouble(5), reader.GetBoolean(6), reader.GetDouble(7), reader.GetDouble(8),
                reader.GetBoolean(9), reader.GetBoolean(10), reader.GetFieldValue<int[]>(11)));
        }

        return scores;
    }

    /// <summary>The newest scored bucket, so that a restarted monitor carries on instead of rescoring.</summary>
    public async Task<DateTimeOffset?> LastBucketAsync(CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("select max(bucket_start) from anomaly_scores");
        return await command.ExecuteScalarAsync(ct) is DateTime last ? new DateTimeOffset(last, TimeSpan.Zero) : null;
    }
}
