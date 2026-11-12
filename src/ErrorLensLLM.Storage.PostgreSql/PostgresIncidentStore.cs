using System.Text.Json;
using System.Text.Json.Serialization;
using ErrorLensLLM.Core.Incidents;
using Npgsql;
using NpgsqlTypes;

namespace ErrorLensLLM.Storage.PostgreSql;

/// <summary>Reads recent errors for correlation and keeps incidents up to date as they evolve.</summary>
public sealed class PostgresIncidentStore(NpgsqlDataSource dataSource)
{
    private const string Schema = """
        create table if not exists incidents (
            id                      uuid primary key,
            status                  text not null,
            merged_into             uuid references incidents (id),
            started_at              timestamptz not null,
            last_seen               timestamptz not null,
            created_at              timestamptz not null default now(),
            updated_at              timestamptz not null default now(),
            title                   text not null,
            category                text not null,
            root_fingerprint        text not null references error_groups (fingerprint),
            suspected_deployment_id uuid references deployments (id),
            applications            text[] not null,
            occurrences             bigint not null,
            members                 jsonb not null
        );
        create index if not exists ix_incidents_status on incidents (status, last_seen desc);
        -- When the incident was first detected; databases created before this column get it on startup
        alter table incidents add column if not exists created_at timestamptz not null default now();

        create table if not exists incident_groups (
            incident_id uuid not null references incidents (id),
            fingerprint text not null references error_groups (fingerprint),
            primary key (incident_id, fingerprint)
        );
        create index if not exists ix_incident_groups_fingerprint on incident_groups (fingerprint);

        -- Developer verdicts on the suggested root cause; the evaluation data for the correlator
        create table if not exists incident_feedback (
            id                    bigserial primary key,
            incident_id           uuid not null references incidents (id),
            verdict               text not null check (verdict in ('correct', 'wrong')),
            suggested_fingerprint text not null,
            correct_fingerprint   text,
            comment               text,
            created_at            timestamptz not null default now()
        );
        create index if not exists ix_incident_feedback_incident on incident_feedback (incident_id, created_at desc);
        """;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand(Schema);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<ErrorSample>> LoadErrorsAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            select e.fingerprint, e.application, g.exception_type, g.normalized_message, g.top_frame, e.timestamp, e.trace_id
            from log_events e join error_groups g on g.fingerprint = e.fingerprint
            where e.fingerprint is not null and e.timestamp >= $1
            """);
        command.Parameters.Add(new() { Value = since.ToUniversalTime() });

        var samples = new List<ErrorSample>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            samples.Add(new ErrorSample(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetFieldValue<DateTimeOffset>(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return samples;
    }

    public async Task<IReadOnlyList<DeploymentInfo>> LoadDeploymentsAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand(
            "select id, application, version, commit_sha, started_at from deployments where started_at >= $1");
        command.Parameters.Add(new() { Value = since.ToUniversalTime() });

        var deployments = new List<DeploymentInfo>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            deployments.Add(new DeploymentInfo(
                reader.GetGuid(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return deployments;
    }

    /// <summary>
    /// Stores a correlated incident. If any of its groups already belongs to an open incident that was still active
    /// after <paramref name="activeSince"/>, that incident is updated (the oldest one when several match; the others
    /// are marked merged into it). Returns the incident id.
    /// </summary>
    public async Task<Guid> SaveAsync(IncidentDraft draft, string title, DateTimeOffset activeSince, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var fingerprints = draft.Members.Select(m => m.Fingerprint).ToArray();

        var existing = new List<Guid>();
        await using (var find = new NpgsqlCommand("""
            select distinct i.id, i.started_at from incidents i join incident_groups g on g.incident_id = i.id
            where i.status = 'open' and i.last_seen >= $2 and g.fingerprint = any($1)
            order by i.started_at
            """, connection, transaction))
        {
            find.Parameters.Add(new() { Value = fingerprints });
            find.Parameters.Add(new() { Value = activeSince.ToUniversalTime() });
            await using var reader = await find.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                existing.Add(reader.GetGuid(0));
        }

        var id = existing.Count > 0 ? existing[0] : Guid.NewGuid();

        if (existing.Count > 1)
        {
            await using var merge = new NpgsqlCommand(
                "update incidents set status = 'merged', merged_into = $1, updated_at = now() where id = any($2)", connection, transaction);
            merge.Parameters.Add(new() { Value = id });
            merge.Parameters.Add(new() { Value = existing.Skip(1).ToArray() });
            await merge.ExecuteNonQueryAsync(ct);
        }

        await using (var upsert = new NpgsqlCommand("""
            insert into incidents (id, status, started_at, last_seen, title, category, root_fingerprint,
                                   suspected_deployment_id, applications, occurrences, members)
            values ($1, 'open', $2, $3, $4, $5, $6, $7, $8, $9, $10)
            on conflict (id) do update set
                started_at = least(incidents.started_at, excluded.started_at),
                last_seen = excluded.last_seen, title = excluded.title, category = excluded.category,
                root_fingerprint = excluded.root_fingerprint, suspected_deployment_id = excluded.suspected_deployment_id,
                applications = excluded.applications, occurrences = excluded.occurrences, members = excluded.members,
                updated_at = now()
            """, connection, transaction))
        {
            upsert.Parameters.Add(new() { Value = id });
            upsert.Parameters.Add(new() { Value = draft.StartedAt.ToUniversalTime() });
            upsert.Parameters.Add(new() { Value = draft.LastSeen.ToUniversalTime() });
            upsert.Parameters.Add(new() { Value = title });
            upsert.Parameters.Add(new() { Value = draft.RootCause.Category.ToString() });
            upsert.Parameters.Add(new() { Value = draft.RootCause.Fingerprint });
            upsert.Parameters.Add(new() { Value = (object?)draft.SuspectedDeployment?.Id ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
            upsert.Parameters.Add(new() { Value = draft.Applications.ToArray() });
            upsert.Parameters.Add(new() { Value = draft.Occurrences });
            upsert.Parameters.Add(new() { Value = JsonSerializer.Serialize(draft.Members, Json), NpgsqlDbType = NpgsqlDbType.Jsonb });
            await upsert.ExecuteNonQueryAsync(ct);
        }

        await using (var link = new NpgsqlCommand("""
            insert into incident_groups (incident_id, fingerprint) select $1, unnest($2::text[]) on conflict do nothing
            """, connection, transaction))
        {
            link.Parameters.Add(new() { Value = id });
            link.Parameters.Add(new() { Value = fingerprints });
            await link.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return id;
    }

    /// <summary>Closes open incidents with no new errors since <paramref name="quietSince"/>. Returns how many.</summary>
    public async Task<int> CloseQuietAsync(DateTimeOffset quietSince, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand(
            "update incidents set status = 'closed', updated_at = now() where status = 'open' and last_seen < $1");
        command.Parameters.Add(new() { Value = quietSince.ToUniversalTime() });
        return await command.ExecuteNonQueryAsync(ct);
    }
}
