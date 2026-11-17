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

public sealed partial class PostgresDashboardStore
{
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
