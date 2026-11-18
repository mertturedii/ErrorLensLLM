using ErrorLensLLM.Core.Incidents;
using Npgsql;
using NpgsqlTypes;

namespace ErrorLensLLM.Storage.PostgreSql;

public sealed record OverviewCounts(int OpenIncidents, int NewIncidents, long Logs, long Errors, int ActiveGroups, int Deployments);

public sealed record VolumePoint(DateTimeOffset Bucket, string Application, string Level, int Count);

public sealed record ModuleView(string Application, string? Environment, string? Version, string? CommitSha, DateTimeOffset DeployedAt);

public sealed record ErrorGroupView(
    string Fingerprint, string ExceptionType, string NormalizedMessage, string? TopFrame, DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen, long Occurrences, IReadOnlyList<string> Applications, Guid? IncidentId, string? IncidentTitle,
    string? IncidentStatus)
{
    public ErrorCategory Category => ErrorCategorizer.Categorize(ExceptionType, NormalizedMessage);
}

public sealed record DeploymentRow(
    Guid Id, string Application, string? Environment, string? Host, string? Version, string? CommitSha, DateTimeOffset StartedAt,
    string? PreviousVersion, long Errors, Guid? IncidentId, string? IncidentTitle)
{
    /// <summary>Same rule as <see cref="DeploymentInfo.IsRelease"/>: only a version change ships something new.</summary>
    public bool IsRelease => PreviousVersion is not null && PreviousVersion != Version;
}

public sealed record TemplateView(
    int Id, string Text, string Source, DateTimeOffset FirstSeen, long Count, long Errors, IReadOnlyList<string> Applications);

public sealed record FeedbackRow(
    Guid IncidentId, string Title, ErrorCategory Category, string Verdict, string? Comment, DateTimeOffset CreatedAt,
    string? SuggestedType, IReadOnlyList<string> SuggestedApplications, string? CorrectType,
    IReadOnlyList<string> CorrectApplications, int? CorrectRank);

public sealed record CategoryCount(ErrorCategory Category, int Incidents);

public sealed partial class PostgresDashboardStore
{
    private const string ErrorLevels = "('Error', 'Fatal')";

    private const string EventColumns = "timestamp, application, level, message, exception_type, exception_message, trace_id, fingerprint, id";

    /// <summary>Bucket size that keeps a chart of <paramref name="span"/> at a few dozen bars.</summary>
    public static TimeSpan BucketFor(TimeSpan span) =>
        span <= TimeSpan.FromMinutes(5) ? TimeSpan.FromSeconds(10)
        : span <= TimeSpan.FromMinutes(20) ? TimeSpan.FromSeconds(20)
        : span <= TimeSpan.FromHours(2) ? TimeSpan.FromMinutes(1)
        : span <= TimeSpan.FromHours(30) ? TimeSpan.FromMinutes(30)
        : TimeSpan.FromHours(3);

    public async Task<DateTimeOffset?> LatestLogAsync(CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("select max(timestamp) from log_events");
        return await command.ExecuteScalarAsync(ct) is DateTime value ? new DateTimeOffset(value, TimeSpan.Zero) : null;
    }

    public async Task<OverviewCounts> OverviewAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand($"""
            select
                (select count(*)::int from incidents where status = 'open'),
                (select count(*)::int from incidents where status <> 'merged' and started_at >= $1),
                (select count(*) from log_events where timestamp >= $1),
                (select count(*) from log_events where timestamp >= $1 and level in {ErrorLevels}),
                (select count(distinct fingerprint)::int from log_events where timestamp >= $1 and fingerprint is not null),
                (select count(*)::int from deployments where started_at >= $1)
            """);
        command.Parameters.Add(new() { Value = since.ToUniversalTime() });

        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new OverviewCounts(
            reader.GetInt32(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt32(4), reader.GetInt32(5));
    }

    /// <summary>Log counts per bucket, application and level; every chart of all logs (not only errors) reads from this.</summary>
    public async Task<IReadOnlyList<VolumePoint>> LogVolumeAsync(
        DateTimeOffset from, DateTimeOffset to, TimeSpan bucket, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            select date_bin($1, timestamp, $2) as bucket, application, level, count(*)::int
            from log_events where timestamp >= $2 and timestamp < $3
            group by 1, 2, 3 order by 1
            """);
        command.Parameters.Add(new() { Value = bucket });
        command.Parameters.Add(new() { Value = from.ToUniversalTime() });
        command.Parameters.Add(new() { Value = to.ToUniversalTime() });

        var points = new List<VolumePoint>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            points.Add(new VolumePoint(reader.GetFieldValue<DateTimeOffset>(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        return points;
    }

    /// <summary>Every application that ever announced itself, with the deployment it is running now.</summary>
    public async Task<IReadOnlyList<ModuleView>> ModulesAsync(CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            select distinct on (application) application, environment, version, commit_sha, started_at
            from deployments order by application, started_at desc
            """);

        var result = new List<ModuleView>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new ModuleView(
                reader.GetString(0), NullableString(reader, 1), NullableString(reader, 2), NullableString(reader, 3),
                reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return result;
    }

    /// <summary>Error counts of each incident in <paramref name="slots"/> equal slices of its own duration, for a row sparkline.</summary>
    public async Task<IReadOnlyDictionary<Guid, int[]>> IncidentSparksAsync(
        IReadOnlyCollection<Guid> incidentIds, int slots = 24, CancellationToken ct = default)
    {
        var result = incidentIds.ToDictionary(id => id, _ => new int[slots]);
        if (result.Count == 0)
            return result;

        await using var command = dataSource.CreateCommand("""
            select i.id,
                   width_bucket(extract(epoch from e.timestamp), extract(epoch from i.started_at), extract(epoch from i.last_seen) + 1, $2),
                   count(*)::int
            from incidents i
            join incident_groups g on g.incident_id = i.id
            join log_events e on e.fingerprint = g.fingerprint and e.timestamp between i.started_at and i.last_seen
            where i.id = any($1)
            group by 1, 2
            """);
        command.Parameters.Add(new() { Value = result.Keys.ToArray() });
        command.Parameters.Add(new() { Value = slots });

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[reader.GetGuid(0)][Math.Clamp(reader.GetInt32(1), 1, slots) - 1] = reader.GetInt32(2);
        return result;
    }

    /// <summary>Error groups by last occurrence, each with the incident it currently belongs to; one group when <paramref name="fingerprint"/> is given.</summary>
    public async Task<IReadOnlyList<ErrorGroupView>> ListErrorGroupsAsync(string? fingerprint = null, int limit = 200, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            select g.fingerprint, g.exception_type, g.normalized_message, g.top_frame, g.first_seen, g.last_seen,
                   g.occurrence_count, g.applications, inc.id, inc.title, inc.status
            from error_groups g
            left join lateral (
                select i.id, i.title, i.status from incident_groups ig join incidents i on i.id = ig.incident_id
                where ig.fingerprint = g.fingerprint and i.status <> 'merged'
                order by i.last_seen desc limit 1
            ) inc on true
            where $1::text is null or g.fingerprint = $1
            order by g.last_seen desc limit $2
            """);
        command.Parameters.Add(new() { Value = (object?)fingerprint ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        command.Parameters.Add(new() { Value = limit });

        var result = new List<ErrorGroupView>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new ErrorGroupView(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), NullableString(reader, 3),
                reader.GetFieldValue<DateTimeOffset>(4), reader.GetFieldValue<DateTimeOffset>(5), reader.GetInt64(6),
                reader.GetFieldValue<string[]>(7), reader.IsDBNull(8) ? null : reader.GetGuid(8), NullableString(reader, 9),
                NullableString(reader, 10)));
        }

        return result;
    }

    /// <summary>Occurrences of one error group over its whole life, per application.</summary>
    public async Task<(TimeSpan Bucket, IReadOnlyList<TimelinePoint> Points)> GroupTimelineAsync(ErrorGroupView group, CancellationToken ct = default)
    {
        var bucket = BucketFor(group.LastSeen - group.FirstSeen);
        await using var command = dataSource.CreateCommand("""
            select date_bin($2, timestamp, $3) as bucket, application, count(*)::int
            from log_events where fingerprint = $1
            group by 1, 2 order by 1
            """);
        command.Parameters.Add(new() { Value = group.Fingerprint });
        command.Parameters.Add(new() { Value = bucket });
        command.Parameters.Add(new() { Value = group.FirstSeen.ToUniversalTime() });

        var points = new List<TimelinePoint>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            points.Add(new TimelinePoint(reader.GetFieldValue<DateTimeOffset>(0), reader.GetString(1), reader.GetInt32(2)));
        return (bucket, points);
    }

    public async Task<IReadOnlyList<LogEventView>> GroupEventsAsync(string fingerprint, int limit = 25, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand(
            $"select {EventColumns} from log_events where fingerprint = $1 order by timestamp desc limit $2");
        command.Parameters.Add(new() { Value = fingerprint });
        command.Parameters.Add(new() { Value = limit });
        return await ReadEventsAsync(command, ct);
    }

    /// <summary>Stack trace of the latest occurrence that carried one.</summary>
    public async Task<string?> SampleStackTraceAsync(string fingerprint, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            select stack_trace from log_events where fingerprint = $1 and stack_trace is not null
            order by timestamp desc limit 1
            """);
        command.Parameters.Add(new() { Value = fingerprint });
        return await command.ExecuteScalarAsync(ct) as string;
    }

    public async Task<IReadOnlyList<IncidentSummary>> IncidentsOfGroupAsync(string fingerprint, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand($"""
            select {SummaryColumns} from incidents i
            where i.status <> 'merged' and i.id in (select incident_id from incident_groups where fingerprint = $1)
            order by i.last_seen desc
            """);
        command.Parameters.Add(new() { Value = fingerprint });

        var result = new List<IncidentSummary>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(ReadSummary(reader));
        return result;
    }

    /// <summary>Deployment history, newest first, with the version each one replaced and the incident it is suspected of.</summary>
    public async Task<IReadOnlyList<DeploymentRow>> ListDeploymentsAsync(int limit = 200, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            select d.id, d.application, d.environment, d.host, d.version, d.commit_sha, d.started_at, d.previous_version,
                   (select count(*) from log_events e where e.deployment_id = d.id and e.fingerprint is not null),
                   inc.id, inc.title
            from (
                select d.*, lag(version) over (partition by application order by started_at) as previous_version from deployments d
            ) d
            left join lateral (
                select i.id, i.title from incidents i
                where i.suspected_deployment_id = d.id and i.status <> 'merged'
                order by i.last_seen desc limit 1
            ) inc on true
            order by d.started_at desc limit $1
            """);
        command.Parameters.Add(new() { Value = limit });

        var result = new List<DeploymentRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new DeploymentRow(
                reader.GetGuid(0), reader.GetString(1), NullableString(reader, 2), NullableString(reader, 3), NullableString(reader, 4),
                NullableString(reader, 5), reader.GetFieldValue<DateTimeOffset>(6), NullableString(reader, 7), reader.GetInt64(8),
                reader.IsDBNull(9) ? null : reader.GetGuid(9), NullableString(reader, 10)));
        }

        return result;
    }

    /// <summary>Templates that logged since <paramref name="since"/>, newest template first.</summary>
    public async Task<IReadOnlyList<TemplateView>> ActiveTemplatesAsync(DateTimeOffset since, int limit = 100, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand($"""
            select t.id, t.text, t.source, t.first_seen, s.logs, s.errors, s.applications
            from (
                select template_id, count(*) as logs, count(*) filter (where level in {ErrorLevels}) as errors,
                       array_agg(distinct application) as applications
                from log_events where timestamp >= $1 group by 1
            ) s join log_templates t on t.id = s.template_id
            order by t.first_seen desc, s.logs desc limit $2
            """);
        command.Parameters.Add(new() { Value = since.ToUniversalTime() });
        command.Parameters.Add(new() { Value = limit });

        var result = new List<TemplateView>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new TemplateView(
                reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3),
                reader.GetInt64(4), reader.GetInt64(5), reader.GetFieldValue<string[]>(6)));
        }

        return result;
    }

    /// <summary>Every log of one request, across modules, in time order: how the request travelled and where it failed.</summary>
    public async Task<IReadOnlyList<LogEventView>> TraceAsync(string traceId, int limit = 500, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand(
            $"select {EventColumns} from log_events where trace_id = $1 order by timestamp, id limit $2");
        command.Parameters.Add(new() { Value = traceId.Trim().ToLowerInvariant() });
        command.Parameters.Add(new() { Value = limit });
        return await ReadEventsAsync(command, ct);
    }

    /// <summary>Latest logs of every level; all filters are optional.</summary>
    public async Task<IReadOnlyList<LogEventView>> RecentLogsAsync(
        DateTimeOffset since, string? application, string? level, string? search, int limit = 100, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand($"""
            select {EventColumns} from log_events
            where ($2::text is null or application = $2)
              and ($3::text is null or level = $3)
              and (
                  -- a trace id or a log id (#123) finds its logs whatever the time range
                  ($4::text is not null and (trace_id = lower($4) or ($4 ~ '^#?[0-9]+$' and length($4) <= 19 and id = ltrim($4, '#')::bigint)))
                  or (timestamp >= $1 and ($4::text is null or message ilike '%' || $4 || '%' or exception_type ilike '%' || $4 || '%')))
            order by timestamp desc limit $5
            """);
        command.Parameters.Add(new() { Value = since.ToUniversalTime() });
        command.Parameters.Add(new() { Value = (object?)application ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        command.Parameters.Add(new() { Value = (object?)level ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        command.Parameters.Add(new() { Value = string.IsNullOrWhiteSpace(search) ? DBNull.Value : EscapeLike(search.Trim()), NpgsqlDbType = NpgsqlDbType.Text });
        command.Parameters.Add(new() { Value = limit });
        return await ReadEventsAsync(command, ct);
    }

    /// <summary>
    /// The latest verdict of each incident. <see cref="FeedbackRow.CorrectRank"/> is where the developer's choice stood in
    /// the suggested ranking (1 = the suggestion itself), which is what Top-k accuracy is computed from.
    /// </summary>
    public async Task<IReadOnlyList<FeedbackRow>> FeedbackAsync(CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            select * from (
                select distinct on (f.incident_id)
                       f.incident_id, i.title, i.category, f.verdict, f.comment, f.created_at,
                       s.exception_type, s.applications, c.exception_type, c.applications,
                       (select m.ord::int from jsonb_array_elements(i.members) with ordinality m(v, ord)
                        where m.v ->> 'fingerprint' = f.correct_fingerprint limit 1)
                from incident_feedback f
                join incidents i on i.id = f.incident_id
                left join error_groups s on s.fingerprint = f.suggested_fingerprint
                left join error_groups c on c.fingerprint = f.correct_fingerprint
                where i.status <> 'merged'
                order by f.incident_id, f.created_at desc
            ) latest order by created_at desc
            """);

        var result = new List<FeedbackRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new FeedbackRow(
                reader.GetGuid(0), reader.GetString(1), ParseCategory(reader.GetString(2)), reader.GetString(3),
                NullableString(reader, 4), reader.GetFieldValue<DateTimeOffset>(5), NullableString(reader, 6),
                reader.IsDBNull(7) ? [] : reader.GetFieldValue<string[]>(7), NullableString(reader, 8),
                reader.IsDBNull(9) ? [] : reader.GetFieldValue<string[]>(9), reader.IsDBNull(10) ? null : reader.GetInt32(10)));
        }

        return result;
    }

    public async Task<IReadOnlyList<CategoryCount>> IncidentCategoriesAsync(CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand(
            "select category, count(*)::int from incidents where status <> 'merged' group by 1 order by 2 desc");

        var result = new List<CategoryCount>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new CategoryCount(ParseCategory(reader.GetString(0)), reader.GetInt32(1)));
        return result;
    }

    private static ErrorCategory ParseCategory(string value) =>
        Enum.TryParse<ErrorCategory>(value, out var category) ? category : ErrorCategory.Unknown;

    /// <summary>The search box is plain text, not a pattern.</summary>
    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
