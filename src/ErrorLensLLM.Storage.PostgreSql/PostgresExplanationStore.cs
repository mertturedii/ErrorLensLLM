using Npgsql;
using NpgsqlTypes;

namespace ErrorLensLLM.Storage.PostgreSql;

/// <summary>A stored LLM explanation; <see cref="Json"/> holds the explanation fields, or null when the attempt failed.</summary>
public sealed record ExplanationRow(
    Guid IncidentId, string RootFingerprint, string Model, string? Json, string? Error, long InputTokens, long OutputTokens, DateTimeOffset CreatedAt);

/// <summary>LLM explanations of incidents, one per incident and root cause; a new root cause gets a new explanation.</summary>
public sealed class PostgresExplanationStore(NpgsqlDataSource dataSource)
{
    private const string Schema = """
        create table if not exists incident_explanations (
            id               bigserial primary key,
            incident_id      uuid not null references incidents (id),
            root_fingerprint text not null,
            model            text not null,
            content          jsonb,
            error            text,
            input_tokens     bigint not null default 0,
            output_tokens    bigint not null default 0,
            created_at       timestamptz not null default now()
        );
        create index if not exists ix_incident_explanations_incident on incident_explanations (incident_id, created_at desc);
        """;

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand(Schema);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// The oldest incident that needs an explanation: not merged, detected at least <paramref name="settle"/> ago (so its
    /// members have stopped moving), and without an explanation for its current root cause. A failed attempt is retried
    /// after <paramref name="retryAfter"/>.
    /// </summary>
    public async Task<Guid?> NextPendingAsync(TimeSpan settle, TimeSpan retryAfter, DateTimeOffset since, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            select i.id from incidents i
            where i.status <> 'merged' and i.created_at <= now() - $1 and i.last_seen >= $3
              and not exists (
                  select 1 from incident_explanations x
                  where x.incident_id = i.id and x.root_fingerprint = i.root_fingerprint
                    and (x.error is null or x.created_at > now() - $2))
            order by i.created_at limit 1
            """);
        command.Parameters.Add(new() { Value = settle, NpgsqlDbType = NpgsqlDbType.Interval });
        command.Parameters.Add(new() { Value = retryAfter, NpgsqlDbType = NpgsqlDbType.Interval });
        command.Parameters.Add(new() { Value = since.ToUniversalTime() });
        return await command.ExecuteScalarAsync(ct) is Guid id ? id : null;
    }

    public async Task SaveAsync(ExplanationRow row, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            insert into incident_explanations (incident_id, root_fingerprint, model, content, error, input_tokens, output_tokens)
            values ($1, $2, $3, $4, $5, $6, $7)
            """);
        command.Parameters.Add(new() { Value = row.IncidentId });
        command.Parameters.Add(new() { Value = row.RootFingerprint });
        command.Parameters.Add(new() { Value = row.Model });
        command.Parameters.Add(new() { Value = (object?)row.Json ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Jsonb });
        command.Parameters.Add(new() { Value = (object?)row.Error ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        command.Parameters.Add(new() { Value = row.InputTokens });
        command.Parameters.Add(new() { Value = row.OutputTokens });
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The latest explanation or failed attempt for an incident, if any.</summary>
    public async Task<ExplanationRow?> LatestAsync(Guid incidentId, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            select incident_id, root_fingerprint, model, content::text, error, input_tokens, output_tokens, created_at
            from incident_explanations where incident_id = $1 order by created_at desc limit 1
            """);
        command.Parameters.Add(new() { Value = incidentId });

        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;
            return new ExplanationRow(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetFieldValue<DateTimeOffset>(7));
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return null;
        }
    }
}
