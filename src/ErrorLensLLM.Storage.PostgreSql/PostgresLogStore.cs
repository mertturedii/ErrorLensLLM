using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace ErrorLensLLM.Storage.PostgreSql;

/// <summary>A log event after masking, templating and fingerprinting, ready to be stored.</summary>
public sealed record LogEventRow
{
    public required DateTimeOffset Timestamp { get; init; }
    public required string Application { get; init; }
    public string? Environment { get; init; }
    public string? Host { get; init; }
    public required string Level { get; init; }
    public required int TemplateId { get; init; }
    public required string Message { get; init; }
    public string? PropertiesJson { get; init; }
    public string? TraceId { get; init; }
    public string? SpanId { get; init; }
    public Guid? DeploymentId { get; init; }
    public string? ExceptionType { get; init; }
    public string? ExceptionMessage { get; init; }
    public string? StackTrace { get; init; }
    public string? Fingerprint { get; init; }
}

/// <summary>One occurrence of an error group; occurrences of the same fingerprint in a batch are merged before upsert.</summary>
public sealed record ErrorOccurrence(
    string Fingerprint, string ExceptionType, string NormalizedMessage, string? TopFrame, string Application, DateTimeOffset Timestamp);

/// <summary>Stores collected logs, templates, deployments and error groups in PostgreSQL.</summary>
public sealed class PostgresLogStore(NpgsqlDataSource dataSource)
{
    private const string Schema = """
        create table if not exists deployments (
            id          uuid primary key,
            application text not null,
            environment text,
            host        text,
            version     text,
            commit_sha  text,
            started_at  timestamptz not null
        );
        create index if not exists ix_deployments_application on deployments (application, environment, started_at desc);

        create table if not exists log_templates (
            id         serial primary key,
            text_hash  text not null unique,
            text       text not null,
            source     text not null,
            first_seen timestamptz not null default now()
        );

        create table if not exists error_groups (
            fingerprint        text primary key,
            exception_type     text not null,
            normalized_message text not null,
            top_frame          text,
            first_seen         timestamptz not null,
            last_seen          timestamptz not null,
            occurrence_count   bigint not null,
            applications       text[] not null
        );

        create table if not exists log_events (
            id                bigserial primary key,
            timestamp         timestamptz not null,
            received_at       timestamptz not null default now(),
            application       text not null,
            environment       text,
            host              text,
            level             text not null,
            template_id       int not null references log_templates (id),
            message           text not null,
            properties        jsonb,
            trace_id          text,
            span_id           text,
            deployment_id     uuid references deployments (id),
            exception_type    text,
            exception_message text,
            stack_trace       text,
            fingerprint       text references error_groups (fingerprint)
        );
        create index if not exists ix_log_events_timestamp on log_events (timestamp);
        create index if not exists ix_log_events_application on log_events (application, timestamp);
        create index if not exists ix_log_events_fingerprint on log_events (fingerprint) where fingerprint is not null;
        create index if not exists ix_log_events_trace on log_events (trace_id) where trace_id is not null;
        """;

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand(Schema);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Returns the deployment for this application version and start time, creating it on first sight.</summary>
    public async Task<Guid> RecordDeploymentAsync(
        string application, string? environment, string? host, string? version, string? commitSha, DateTimeOffset startedAt,
        CancellationToken ct = default)
    {
        // Several instances of one release report the same version; they share a deployment when they start close together
        await using var find = dataSource.CreateCommand("""
            select id from deployments
            where application = $1 and environment is not distinct from $2 and version is not distinct from $3
              and commit_sha is not distinct from $4 and abs(extract(epoch from started_at - $5)) < 300
            order by started_at desc limit 1
            """);
        find.Parameters.Add(new() { Value = application });
        find.Parameters.Add(new() { Value = (object?)environment ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        find.Parameters.Add(new() { Value = (object?)version ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        find.Parameters.Add(new() { Value = (object?)commitSha ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        find.Parameters.Add(new() { Value = startedAt });
        if (await find.ExecuteScalarAsync(ct) is Guid existing)
            return existing;

        var id = Guid.NewGuid();
        await using var insert = dataSource.CreateCommand(
            "insert into deployments (id, application, environment, host, version, commit_sha, started_at) values ($1, $2, $3, $4, $5, $6, $7)");
        insert.Parameters.Add(new() { Value = id });
        insert.Parameters.Add(new() { Value = application });
        insert.Parameters.Add(new() { Value = (object?)environment ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        insert.Parameters.Add(new() { Value = (object?)host ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        insert.Parameters.Add(new() { Value = (object?)version ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        insert.Parameters.Add(new() { Value = (object?)commitSha ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        insert.Parameters.Add(new() { Value = startedAt });
        await insert.ExecuteNonQueryAsync(ct);
        return id;
    }

    /// <summary>Id of a template text, inserting it on first sight. Callers should cache the result.</summary>
    public async Task<int> GetOrCreateTemplateAsync(string text, string source, CancellationToken ct = default)
    {
        // Templates can be long; uniqueness is enforced on a hash instead of the text itself
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        await using var command = dataSource.CreateCommand("""
            insert into log_templates (text_hash, text, source) values ($1, $2, $3)
            on conflict (text_hash) do update set text_hash = excluded.text_hash
            returning id
            """);
        command.Parameters.Add(new() { Value = hash });
        command.Parameters.Add(new() { Value = text });
        command.Parameters.Add(new() { Value = source });
        return (int)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Upserts error groups, then bulk-inserts the events (groups first, events reference them).</summary>
    public async Task SaveAsync(IReadOnlyList<LogEventRow> events, IReadOnlyList<ErrorOccurrence> errors, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        foreach (var group in errors.GroupBy(e => e.Fingerprint))
        {
            var first = group.MinBy(e => e.Timestamp)!;
            await using var upsert = new NpgsqlCommand("""
                insert into error_groups (fingerprint, exception_type, normalized_message, top_frame, first_seen, last_seen, occurrence_count, applications)
                values ($1, $2, $3, $4, $5, $6, $7, $8)
                on conflict (fingerprint) do update set
                    first_seen       = least(error_groups.first_seen, excluded.first_seen),
                    last_seen        = greatest(error_groups.last_seen, excluded.last_seen),
                    occurrence_count = error_groups.occurrence_count + excluded.occurrence_count,
                    applications     = array(select distinct unnest(error_groups.applications || excluded.applications) order by 1)
                """, connection, transaction);
            upsert.Parameters.Add(new() { Value = group.Key });
            upsert.Parameters.Add(new() { Value = first.ExceptionType });
            upsert.Parameters.Add(new() { Value = first.NormalizedMessage });
            upsert.Parameters.Add(new() { Value = (object?)first.TopFrame ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            upsert.Parameters.Add(new() { Value = first.Timestamp });
            upsert.Parameters.Add(new() { Value = group.Max(e => e.Timestamp) });
            upsert.Parameters.Add(new() { Value = (long)group.Count() });
            upsert.Parameters.Add(new() { Value = group.Select(e => e.Application).Distinct().Order().ToArray() });
            await upsert.ExecuteNonQueryAsync(ct);
        }

        await using (var writer = await connection.BeginBinaryImportAsync("""
            copy log_events (timestamp, application, environment, host, level, template_id, message, properties,
                             trace_id, span_id, deployment_id, exception_type, exception_message, stack_trace, fingerprint)
            from stdin (format binary)
            """, ct))
        {
            foreach (var e in events)
            {
                await writer.StartRowAsync(ct);
                await writer.WriteAsync(e.Timestamp, NpgsqlDbType.TimestampTz, ct);
                await writer.WriteAsync(e.Application, NpgsqlDbType.Text, ct);
                await WriteNullableAsync(writer, e.Environment, NpgsqlDbType.Text, ct);
                await WriteNullableAsync(writer, e.Host, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(e.Level, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(e.TemplateId, NpgsqlDbType.Integer, ct);
                await writer.WriteAsync(e.Message, NpgsqlDbType.Text, ct);
                await WriteNullableAsync(writer, e.PropertiesJson, NpgsqlDbType.Jsonb, ct);
                await WriteNullableAsync(writer, e.TraceId, NpgsqlDbType.Text, ct);
                await WriteNullableAsync(writer, e.SpanId, NpgsqlDbType.Text, ct);
                await WriteNullableAsync(writer, e.DeploymentId, NpgsqlDbType.Uuid, ct);
                await WriteNullableAsync(writer, e.ExceptionType, NpgsqlDbType.Text, ct);
                await WriteNullableAsync(writer, e.ExceptionMessage, NpgsqlDbType.Text, ct);
                await WriteNullableAsync(writer, e.StackTrace, NpgsqlDbType.Text, ct);
                await WriteNullableAsync(writer, e.Fingerprint, NpgsqlDbType.Text, ct);
            }

            await writer.CompleteAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    private static Task WriteNullableAsync<T>(NpgsqlBinaryImporter writer, T? value, NpgsqlDbType type, CancellationToken ct) =>
        value is null ? writer.WriteNullAsync(ct) : writer.WriteAsync(value, type, ct);
}
