using System.Text.Json;
using System.Text.Json.Serialization;
using ErrorLensLLM.Core.Incidents;
using Npgsql;
using NpgsqlTypes;

namespace ErrorLensLLM.Storage.PostgreSql;

public sealed record IncidentSummary(
    Guid Id, string Status, DateTimeOffset StartedAt, DateTimeOffset LastSeen, string Title, ErrorCategory Category,
    IReadOnlyList<string> Applications, long Occurrences, string? Verdict);

public sealed record DeploymentView(string Application, string? Version, string? CommitSha, DateTimeOffset StartedAt);

public sealed record IncidentDetail(
    IncidentSummary Summary, string RootFingerprint, DeploymentView? SuspectedDeployment, IReadOnlyList<IncidentMember> Members);

public sealed record TimelinePoint(DateTimeOffset Bucket, string Application, int Errors);

public sealed record LogEventView(
    DateTimeOffset Timestamp, string Application, string Level, string Message, string? ExceptionType, string? ExceptionMessage,
    string? TraceId, string? Fingerprint);

public sealed record FeedbackView(string Verdict, string? CorrectFingerprint, string? Comment, DateTimeOffset CreatedAt);

/// <summary>Read side for the dashboard, plus the developer feedback it records.</summary>
public sealed partial class PostgresDashboardStore(NpgsqlDataSource dataSource)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private const string SummaryColumns = """
        i.id, i.status, i.started_at, i.last_seen, i.title, i.category, i.applications, i.occurrences,
        (select f.verdict from incident_feedback f where f.incident_id = i.id order by f.created_at desc limit 1)
        """;

    /// <summary>Most recently active incidents; <paramref name="status"/> is "open", "closed" or null for all (merged ones are never listed).</summary>
    public async Task<IReadOnlyList<IncidentSummary>> ListIncidentsAsync(string? status, int limit = 100, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand($"""
            select {SummaryColumns} from incidents i
            where i.status <> 'merged' and ($1::text is null or i.status = $1)
            order by i.last_seen desc limit $2
            """);
        command.Parameters.Add(new() { Value = (object?)status ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        command.Parameters.Add(new() { Value = limit });

        var result = new List<IncidentSummary>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(ReadSummary(reader));
        return result;
    }

    public async Task<IncidentDetail?> GetIncidentAsync(Guid id, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand($"""
            select {SummaryColumns}, i.root_fingerprint, i.members::text,
                   d.application, d.version, d.commit_sha, d.started_at
            from incidents i left join deployments d on d.id = i.suspected_deployment_id
            where i.id = $1
            """);
        command.Parameters.Add(new() { Value = id });

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var deployment = reader.IsDBNull(11)
            ? null
            : new DeploymentView(reader.GetString(11), NullableString(reader, 12), NullableString(reader, 13), reader.GetFieldValue<DateTimeOffset>(14));

        return new IncidentDetail(
            ReadSummary(reader),
            reader.GetString(9),
            deployment,
            JsonSerializer.Deserialize<List<IncidentMember>>(reader.GetString(10), Json) ?? []);
    }

    /// <summary>Error counts per application over the incident, in buckets sized to its duration.</summary>
    public async Task<(TimeSpan Bucket, IReadOnlyList<TimelinePoint> Points)> TimelineAsync(IncidentSummary incident, CancellationToken ct = default)
    {
        var span = incident.LastSeen - incident.StartedAt;
        var bucket = span <= TimeSpan.FromMinutes(5) ? TimeSpan.FromSeconds(10)
            : span <= TimeSpan.FromHours(2) ? TimeSpan.FromMinutes(1)
            : TimeSpan.FromMinutes(10);

        await using var command = dataSource.CreateCommand("""
            select date_bin($2, e.timestamp, $3) as bucket, e.application, count(*)::int
            from log_events e
            where e.fingerprint in (select fingerprint from incident_groups where incident_id = $1)
              and e.timestamp between $3 - $2 and $4 + $2
            group by 1, 2 order by 1
            """);
        command.Parameters.Add(new() { Value = incident.Id });
        command.Parameters.Add(new() { Value = bucket });
        command.Parameters.Add(new() { Value = incident.StartedAt.ToUniversalTime() });
        command.Parameters.Add(new() { Value = incident.LastSeen.ToUniversalTime() });

        var points = new List<TimelinePoint>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            points.Add(new TimelinePoint(reader.GetFieldValue<DateTimeOffset>(0), reader.GetString(1), reader.GetInt32(2)));
        return (bucket, points);
    }

    public async Task<IReadOnlyList<DeploymentView>> DeploymentsAroundAsync(IncidentSummary incident, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            select application, version, commit_sha, started_at from deployments
            where started_at between $1 - interval '10 minutes' and $2
            order by started_at
            """);
        command.Parameters.Add(new() { Value = incident.StartedAt.ToUniversalTime() });
        command.Parameters.Add(new() { Value = incident.LastSeen.ToUniversalTime() });

        var result = new List<DeploymentView>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new DeploymentView(reader.GetString(0), NullableString(reader, 1), NullableString(reader, 2), reader.GetFieldValue<DateTimeOffset>(3)));
        return result;
    }

    /// <summary>All logs of one trace in which the root cause and at least one other member failed: the failure as it spread.</summary>
    public async Task<IReadOnlyList<LogEventView>> SampleTraceAsync(Guid incidentId, string rootFingerprint, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            with members as (select fingerprint from incident_groups where incident_id = $1),
            candidate as (
                select e.trace_id from log_events e
                where e.trace_id is not null and e.fingerprint in (select fingerprint from members)
                group by e.trace_id
                having bool_or(e.fingerprint = $2) and count(distinct e.fingerprint) > 1
                order by max(e.timestamp) desc limit 1
            )
            select timestamp, application, level, message, exception_type, exception_message, trace_id, fingerprint
            from log_events where trace_id = (select trace_id from candidate)
            order by timestamp
            """);
        command.Parameters.Add(new() { Value = incidentId });
        command.Parameters.Add(new() { Value = rootFingerprint });
        return await ReadEventsAsync(command, ct);
    }

    public async Task<IReadOnlyList<LogEventView>> RecentErrorsAsync(Guid incidentId, int limit = 25, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            select timestamp, application, level, message, exception_type, exception_message, trace_id, fingerprint
            from log_events
            where fingerprint in (select fingerprint from incident_groups where incident_id = $1)
            order by timestamp desc limit $2
            """);
        command.Parameters.Add(new() { Value = incidentId });
        command.Parameters.Add(new() { Value = limit });
        return await ReadEventsAsync(command, ct);
    }

    public async Task<FeedbackView?> LatestFeedbackAsync(Guid incidentId, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            select verdict, correct_fingerprint, comment, created_at from incident_feedback
            where incident_id = $1 order by created_at desc limit 1
            """);
        command.Parameters.Add(new() { Value = incidentId });

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new FeedbackView(reader.GetString(0), NullableString(reader, 1), NullableString(reader, 2), reader.GetFieldValue<DateTimeOffset>(3))
            : null;
    }

    /// <summary>
    /// Records whether the suggested root cause was right (<paramref name="verdict"/> "correct" or "wrong"); when wrong,
    /// <paramref name="correctFingerprint"/> is the member the developer identified as the real root cause.
    /// </summary>
    public async Task SaveFeedbackAsync(Guid incidentId, string verdict, string? correctFingerprint, string? comment, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            insert into incident_feedback (incident_id, verdict, suggested_fingerprint, correct_fingerprint, comment)
            select id, $2, root_fingerprint, $3, $4 from incidents where id = $1
            """);
        command.Parameters.Add(new() { Value = incidentId });
        command.Parameters.Add(new() { Value = verdict });
        command.Parameters.Add(new() { Value = (object?)correctFingerprint ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        command.Parameters.Add(new() { Value = string.IsNullOrWhiteSpace(comment) ? DBNull.Value : comment.Trim(), NpgsqlDbType = NpgsqlDbType.Text });
        await command.ExecuteNonQueryAsync(ct);
    }

    private static IncidentSummary ReadSummary(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetString(1),
        reader.GetFieldValue<DateTimeOffset>(2),
        reader.GetFieldValue<DateTimeOffset>(3),
        reader.GetString(4),
        Enum.TryParse<ErrorCategory>(reader.GetString(5), out var category) ? category : ErrorCategory.Unknown,
        reader.GetFieldValue<string[]>(6),
        reader.GetInt64(7),
        NullableString(reader, 8));

    private static async Task<IReadOnlyList<LogEventView>> ReadEventsAsync(NpgsqlCommand command, CancellationToken ct)
    {
        var result = new List<LogEventView>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new LogEventView(
                reader.GetFieldValue<DateTimeOffset>(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                NullableString(reader, 4), NullableString(reader, 5), NullableString(reader, 6), NullableString(reader, 7)));
        }

        return result;
    }

    private static string? NullableString(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
