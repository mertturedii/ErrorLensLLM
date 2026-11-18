using Npgsql;

namespace ErrorLensLLM.Storage.PostgreSql;

/// <summary>Live anomaly scores of one module, folded into a display bucket.</summary>
/// <param name="Bucket">Start of the display bucket.</param>
/// <param name="Application">The module.</param>
/// <param name="Threshold">DeepLog threshold of the model that scored them.</param>
/// <param name="Scored">Scored buckets inside it.</param>
/// <param name="MaxShare">Highest DeepLog share among the scored buckets inside it.</param>
/// <param name="Anomalous">Scored buckets inside it that any detector flagged.</param>
public sealed record AnomalyPoint(DateTimeOffset Bucket, string Application, double MaxShare, double Threshold, int Scored, int Anomalous);

/// <summary>A flagged bucket with the detectors that fired and the texts of its unseen templates.</summary>
public sealed record AnomalyEvent(
    DateTimeOffset BucketStart, string Application, int Events, int AnomalousEvents, double DeepLogShare, bool DeepLog,
    double ForestScore, bool Forest, bool Volume, IReadOnlyList<string> UnseenTemplates);

/// <summary>
/// All three detectors of one module over a display bucket: the highest DeepLog share and forest score among its scored
/// buckets with the thresholds they were compared with, the event counts, and how often each detector fired.
/// </summary>
public sealed record DetectorPoint(
    DateTimeOffset Bucket, string Application, int Scored, int Anomalous,
    double DeepLogMax, double DeepLogThreshold, int DeepLogHits,
    double ForestMax, double ForestThreshold, int ForestHits,
    int EventsMin, int EventsMax, double EventsAvg, int VolumeHits);

/// <summary>A flagged bucket in full: every detector's value next to its threshold.</summary>
public sealed record AnomalyDetail(
    DateTimeOffset BucketStart, string Application, int Events, int AnomalousEvents,
    double DeepLogShare, double DeepLogThreshold, bool DeepLog,
    double ForestScore, double ForestThreshold, bool Forest, bool Volume, IReadOnlyList<string> UnseenTemplates);

/// <summary>An incident of the same module that was active around a flagged bucket.</summary>
public sealed record IncidentLink(Guid Id, string Title, DateTimeOffset StartedAt);

public sealed partial class PostgresDashboardStore
{
    /// <summary>Length of a scored bucket; see AnomalyMonitor in the collector.</summary>
    public static readonly TimeSpan ScoreBucket = TimeSpan.FromSeconds(10);

    public async Task<IReadOnlyList<DetectorPoint>> DetectorSeriesAsync(
        DateTimeOffset from, DateTimeOffset to, TimeSpan bucket, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            select date_bin($1, bucket_start, $2), application, count(*)::int,
                   count(*) filter (where deeplog_anomaly or forest_anomaly or volume_anomaly)::int,
                   max(deeplog_share), max(deeplog_threshold), count(*) filter (where deeplog_anomaly)::int,
                   max(forest_score), max(forest_threshold), count(*) filter (where forest_anomaly)::int,
                   min(events), max(events), avg(events)::float8, count(*) filter (where volume_anomaly)::int
            from anomaly_scores where bucket_start >= $2 and bucket_start < $3
            group by 1, 2 order by 1
            """);
        command.Parameters.Add(new() { Value = bucket });
        command.Parameters.Add(new() { Value = from.ToUniversalTime() });
        command.Parameters.Add(new() { Value = to.ToUniversalTime() });

        var points = new List<DetectorPoint>();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                points.Add(new DetectorPoint(
                    reader.GetFieldValue<DateTimeOffset>(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3),
                    reader.GetDouble(4), reader.GetDouble(5), reader.GetInt32(6), reader.GetDouble(7), reader.GetDouble(8), reader.GetInt32(9),
                    reader.GetInt32(10), reader.GetInt32(11), reader.GetDouble(12), reader.GetInt32(13)));
            }
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedTable)
        {
        }

        return points;
    }

    /// <summary>Flagged buckets since <paramref name="from"/>, newest first; <paramref name="detector"/> is deeplog, forest or volume.</summary>
    public async Task<IReadOnlyList<AnomalyDetail>> AnomaliesAsync(
        DateTimeOffset from, string? application, string? detector, int limit = 100, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            select s.bucket_start, s.application, s.events, s.anomalous_events, s.deeplog_share, s.deeplog_threshold, s.deeplog_anomaly,
                   s.forest_score, s.forest_threshold, s.forest_anomaly, s.volume_anomaly,
                   coalesce((select array_agg(t.text order by t.id) from log_templates t where t.id = any (s.unseen_templates)), '{}')
            from anomaly_scores s
            where s.bucket_start >= $1 and (s.deeplog_anomaly or s.forest_anomaly or s.volume_anomaly)
              and ($2::text is null or s.application = $2)
              and ($3::text is null or ($3 = 'deeplog' and s.deeplog_anomaly) or ($3 = 'forest' and s.forest_anomaly)
                   or ($3 = 'volume' and s.volume_anomaly))
            order by s.bucket_start desc, s.application limit $4
            """);
        command.Parameters.Add(new() { Value = from.ToUniversalTime() });
        command.Parameters.Add(new() { Value = (object?)application ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        command.Parameters.Add(new() { Value = (object?)detector ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        command.Parameters.Add(new() { Value = limit });

        var result = new List<AnomalyDetail>();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                result.Add(new AnomalyDetail(
                    reader.GetFieldValue<DateTimeOffset>(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3),
                    reader.GetDouble(4), reader.GetDouble(5), reader.GetBoolean(6), reader.GetDouble(7), reader.GetDouble(8),
                    reader.GetBoolean(9), reader.GetBoolean(10), reader.GetFieldValue<string[]>(11)));
            }
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedTable)
        {
        }

        return result;
    }

    /// <summary>The logs one module wrote in one scored bucket, in time order.</summary>
    public async Task<IReadOnlyList<LogEventView>> BucketLogsAsync(string application, DateTimeOffset bucketStart, int limit = 200, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand($"""
            select {EventColumns} from log_events
            where application = $1 and timestamp >= $2 and timestamp < $3
            order by timestamp, id limit $4
            """);
        command.Parameters.Add(new() { Value = application });
        command.Parameters.Add(new() { Value = bucketStart.ToUniversalTime() });
        command.Parameters.Add(new() { Value = (bucketStart + ScoreBucket).ToUniversalTime() });
        command.Parameters.Add(new() { Value = limit });
        return await ReadEventsAsync(command, ct);
    }

    /// <summary>Incidents of the module that were active in the minutes around a bucket, so a flag can be read with its cause.</summary>
    public async Task<IReadOnlyList<IncidentLink>> IncidentsAroundAsync(string application, DateTimeOffset bucketStart, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            select id, title, started_at from incidents
            where status <> 'merged' and $1 = any (applications)
              and started_at <= $2 + interval '2 minutes' and last_seen >= $2 - interval '2 minutes'
            order by started_at desc limit 5
            """);
        command.Parameters.Add(new() { Value = application });
        command.Parameters.Add(new() { Value = bucketStart.ToUniversalTime() });

        var result = new List<IncidentLink>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new IncidentLink(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2)));
        return result;
    }

    public async Task<IReadOnlyList<AnomalyPoint>> AnomalySeriesAsync(
        DateTimeOffset from, DateTimeOffset to, TimeSpan bucket, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            select date_bin($1, bucket_start, $2), application, max(deeplog_share), max(deeplog_threshold), count(*)::int,
                   count(*) filter (where deeplog_anomaly or forest_anomaly or volume_anomaly)::int
            from anomaly_scores where bucket_start >= $2 and bucket_start < $3
            group by 1, 2 order by 1
            """);
        command.Parameters.Add(new() { Value = bucket });
        command.Parameters.Add(new() { Value = from.ToUniversalTime() });
        command.Parameters.Add(new() { Value = to.ToUniversalTime() });

        var points = new List<AnomalyPoint>();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                points.Add(new AnomalyPoint(reader.GetFieldValue<DateTimeOffset>(0), reader.GetString(1), reader.GetDouble(2),
                    reader.GetDouble(3), reader.GetInt32(4), reader.GetInt32(5)));
            }
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // A collector without the anomaly monitor has not created the table yet
        }

        return points;
    }

    /// <summary>The latest flagged buckets since <paramref name="from"/>, newest first.</summary>
    public async Task<IReadOnlyList<AnomalyEvent>> RecentAnomaliesAsync(DateTimeOffset from, int limit = 20, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            select s.bucket_start, s.application, s.events, s.anomalous_events, s.deeplog_share, s.deeplog_anomaly,
                   s.forest_score, s.forest_anomaly, s.volume_anomaly,
                   coalesce((select array_agg(t.text order by t.id) from log_templates t where t.id = any (s.unseen_templates)), '{}')
            from anomaly_scores s
            where s.bucket_start >= $1 and (s.deeplog_anomaly or s.forest_anomaly or s.volume_anomaly)
            order by s.bucket_start desc, s.application limit $2
            """);
        command.Parameters.Add(new() { Value = from.ToUniversalTime() });
        command.Parameters.Add(new() { Value = limit });

        var events = new List<AnomalyEvent>();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                events.Add(new AnomalyEvent(
                    reader.GetFieldValue<DateTimeOffset>(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetDouble(4),
                    reader.GetBoolean(5), reader.GetDouble(6), reader.GetBoolean(7), reader.GetBoolean(8), reader.GetFieldValue<string[]>(9)));
            }
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedTable)
        {
        }

        return events;
    }
}
