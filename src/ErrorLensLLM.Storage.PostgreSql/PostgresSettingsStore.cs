using Npgsql;
using NpgsqlTypes;

namespace ErrorLensLLM.Storage.PostgreSql;

/// <summary>
/// An LLM provider as configured on the settings screen. <see cref="ProtectedApiKey"/> is the key encrypted by the
/// applications (ASP.NET Data Protection); the database never holds it in plain text.
/// </summary>
public sealed record LlmProviderRow(
    string Name, string DisplayName, bool Enabled, bool IsDefault, string Model, int MaxOutputTokens, string? ProtectedApiKey,
    string? KeyHint, DateTimeOffset UpdatedAt);

/// <summary>One call to an LLM: what was asked, what came back and what it cost.</summary>
public sealed record LlmRequestRow(
    long Id, DateTimeOffset CreatedAt, string Provider, string Model, Guid? IncidentId, string Purpose,
    string SystemPrompt, string UserPrompt, string? Response, long InputTokens, long OutputTokens, int DurationMs, string? Error);

/// <summary>Requests and tokens of one provider and model.</summary>
public sealed record LlmUsageRow(string Provider, string Model, long Requests, long Failed, long InputTokens, long OutputTokens, DateTimeOffset LastAt);

/// <summary>The settings screen's storage: tunable values, LLM providers and the log of every LLM request.</summary>
public sealed class PostgresSettingsStore(NpgsqlDataSource dataSource)
{
    private const string Schema = """
        create table if not exists settings (
            section    text not null,
            key        text not null,
            value      double precision not null,
            updated_at timestamptz not null default now(),
            primary key (section, key)
        );

        create table if not exists llm_providers (
            name              text primary key,
            display_name      text not null,
            enabled           boolean not null,
            is_default        boolean not null,
            model             text not null,
            max_output_tokens int not null,
            api_key           text,
            key_hint          text,
            updated_at        timestamptz not null default now()
        );

        create table if not exists llm_requests (
            id            bigserial primary key,
            created_at    timestamptz not null default now(),
            provider      text not null,
            model         text not null,
            incident_id   uuid,
            purpose       text not null,
            system_prompt text not null,
            user_prompt   text not null,
            response      text,
            input_tokens  bigint not null default 0,
            output_tokens bigint not null default 0,
            duration_ms   int not null,
            error         text
        );
        create index if not exists ix_llm_requests_created on llm_requests (created_at desc);
        """;

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand(Schema);
        await command.ExecuteNonQueryAsync(ct);
    }

    // ---------- Settings ----------

    public async Task<SettingsSnapshot> LoadSettingsAsync(CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("select section, key, value from settings");
        var values = new Dictionary<(string, string), double>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            values[(reader.GetString(0), reader.GetString(1))] = reader.GetDouble(2);
        return new SettingsSnapshot(values);
    }

    public async Task SaveSettingAsync(string section, string key, double value, CancellationToken ct = default)
    {
        var definition = SettingsCatalog.Find(section, key);
        if (double.IsNaN(value) || value < definition.Min || value > definition.Max)
            throw new ArgumentOutOfRangeException(nameof(value), $"{definition.Label}: {definition.Min}–{definition.Max} arasında olmalı.");

        await using var command = dataSource.CreateCommand("""
            insert into settings (section, key, value) values ($1, $2, $3)
            on conflict (section, key) do update set value = excluded.value, updated_at = now()
            """);
        command.Parameters.Add(new() { Value = section });
        command.Parameters.Add(new() { Value = key });
        command.Parameters.Add(new() { Value = definition.Integer ? Math.Round(value) : value });
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Removes the stored values of a section, so that it falls back to the defaults.</summary>
    public async Task ResetSectionAsync(string section, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("delete from settings where section = $1");
        command.Parameters.Add(new() { Value = section });
        await command.ExecuteNonQueryAsync(ct);
    }

    // ---------- Providers ----------

    /// <summary>Adds a provider on first sight; an existing row, with whatever was set on the screen, is left alone.</summary>
    public async Task SeedProviderAsync(string name, string displayName, string model, int maxOutputTokens, bool isDefault, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            insert into llm_providers (name, display_name, enabled, is_default, model, max_output_tokens)
            values ($1, $2, true, $3 and not exists (select 1 from llm_providers where is_default), $4, $5)
            on conflict (name) do nothing
            """);
        command.Parameters.Add(new() { Value = name });
        command.Parameters.Add(new() { Value = displayName });
        command.Parameters.Add(new() { Value = isDefault });
        command.Parameters.Add(new() { Value = model });
        command.Parameters.Add(new() { Value = maxOutputTokens });
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<LlmProviderRow>> ProvidersAsync(CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            select name, display_name, enabled, is_default, model, max_output_tokens, api_key, key_hint, updated_at
            from llm_providers order by display_name
            """);
        var rows = new List<LlmProviderRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new LlmProviderRow(
                reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3), reader.GetString(4), reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetFieldValue<DateTimeOffset>(8)));
        }

        return rows;
    }

    public async Task UpdateProviderAsync(string name, bool enabled, string model, int maxOutputTokens, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            update llm_providers set enabled = $2, model = $3, max_output_tokens = $4, updated_at = now() where name = $1
            """);
        command.Parameters.Add(new() { Value = name });
        command.Parameters.Add(new() { Value = enabled });
        command.Parameters.Add(new() { Value = model.Trim() });
        command.Parameters.Add(new() { Value = maxOutputTokens });
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Makes one provider the one incidents are explained with.</summary>
    public async Task SetDefaultProviderAsync(string name, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand(
            "update llm_providers set is_default = (name = $1), updated_at = case when is_default <> (name = $1) then now() else updated_at end");
        command.Parameters.Add(new() { Value = name });
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Stores an encrypted key with a short hint of the plain one (its last characters); null removes the key.</summary>
    public async Task SetApiKeyAsync(string name, string? protectedKey, string? hint, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand(
            "update llm_providers set api_key = $2, key_hint = $3, updated_at = now() where name = $1");
        command.Parameters.Add(new() { Value = name });
        command.Parameters.Add(new() { Value = (object?)protectedKey ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        command.Parameters.Add(new() { Value = (object?)hint ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        await command.ExecuteNonQueryAsync(ct);
    }

    // ---------- Request log ----------

    public async Task LogRequestAsync(LlmRequestRow row, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            insert into llm_requests (provider, model, incident_id, purpose, system_prompt, user_prompt, response,
                                      input_tokens, output_tokens, duration_ms, error)
            values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
            """);
        command.Parameters.Add(new() { Value = row.Provider });
        command.Parameters.Add(new() { Value = row.Model });
        command.Parameters.Add(new() { Value = (object?)row.IncidentId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        command.Parameters.Add(new() { Value = row.Purpose });
        command.Parameters.Add(new() { Value = row.SystemPrompt });
        command.Parameters.Add(new() { Value = row.UserPrompt });
        command.Parameters.Add(new() { Value = (object?)row.Response ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        command.Parameters.Add(new() { Value = row.InputTokens });
        command.Parameters.Add(new() { Value = row.OutputTokens });
        command.Parameters.Add(new() { Value = row.DurationMs });
        command.Parameters.Add(new() { Value = (object?)row.Error ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Latest requests, newest first, without their prompt and response texts (see <see cref="RequestAsync"/>).</summary>
    public async Task<IReadOnlyList<LlmRequestRow>> RequestsAsync(
        DateTimeOffset from, string? provider, bool failedOnly, int limit = 100, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            select id, created_at, provider, model, incident_id, purpose, '', '', null, input_tokens, output_tokens, duration_ms, error
            from llm_requests
            where created_at >= $1 and ($2::text is null or provider = $2) and (not $3 or error is not null)
            order by created_at desc limit $4
            """);
        command.Parameters.Add(new() { Value = from.ToUniversalTime() });
        command.Parameters.Add(new() { Value = (object?)provider ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        command.Parameters.Add(new() { Value = failedOnly });
        command.Parameters.Add(new() { Value = limit });
        return await ReadRequestsAsync(command, ct);
    }

    public async Task<LlmRequestRow?> RequestAsync(long id, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            select id, created_at, provider, model, incident_id, purpose, system_prompt, user_prompt, response,
                   input_tokens, output_tokens, duration_ms, error
            from llm_requests where id = $1
            """);
        command.Parameters.Add(new() { Value = id });
        return (await ReadRequestsAsync(command, ct)).FirstOrDefault();
    }

    public async Task<IReadOnlyList<LlmUsageRow>> UsageAsync(DateTimeOffset from, CancellationToken ct = default)
    {
        await using var command = dataSource.CreateCommand("""
            select provider, model, count(*), count(*) filter (where error is not null), sum(input_tokens)::bigint, sum(output_tokens)::bigint,
                   max(created_at)
            from llm_requests where created_at >= $1
            group by provider, model order by sum(input_tokens + output_tokens) desc
            """);
        command.Parameters.Add(new() { Value = from.ToUniversalTime() });
        var rows = new List<LlmUsageRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new LlmUsageRow(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4),
                reader.GetInt64(5), reader.GetFieldValue<DateTimeOffset>(6)));
        }

        return rows;
    }

    private static async Task<IReadOnlyList<LlmRequestRow>> ReadRequestsAsync(NpgsqlCommand command, CancellationToken ct)
    {
        var rows = new List<LlmRequestRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new LlmRequestRow(
                reader.GetInt64(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetInt64(9), reader.GetInt64(10), reader.GetInt32(11),
                reader.IsDBNull(12) ? null : reader.GetString(12)));
        }

        return rows;
    }
}
