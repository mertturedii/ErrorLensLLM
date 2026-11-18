using ErrorLensLLM.Storage.PostgreSql;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ErrorLensLLM.Collector.Tests;

/// <summary>Settings screen storage in a throwaway database: values, providers, encrypted keys and the request log.</summary>
public sealed class SettingsEndToEndTests : IAsyncLifetime
{
    private const string Admin = "Host=127.0.0.1;Port=5432;Database=errorlens;Username=errorlens;Password=errorlens_dev";
    private readonly string _database = $"errorlens_test_{Guid.NewGuid():N}"[..30];
    private readonly string _keys = Path.Combine(Path.GetTempPath(), $"errorlens-keys-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;

    public async Task InitializeAsync()
    {
        await using var admin = NpgsqlDataSource.Create(Admin);
        await using var create = admin.CreateCommand($"create database {_database}");
        await create.ExecuteNonQueryAsync();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:ErrorLens", new NpgsqlConnectionStringBuilder(Admin) { Database = _database }.ConnectionString);
            b.UseSetting("DataProtection:KeysPath", _keys);
            b.UseSetting("Explanation:Provider", "openai");
        });
        _ = _factory.CreateClient(); // starts the host, which creates the schema and seeds the providers
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var admin = NpgsqlDataSource.Create(Admin);
        await using var drop = admin.CreateCommand($"drop database if exists {_database} with (force)");
        await drop.ExecuteNonQueryAsync();
        if (Directory.Exists(_keys))
            Directory.Delete(_keys, recursive: true);
    }

    [Fact]
    public async Task Every_provider_is_seeded_with_the_configured_default()
    {
        var store = _factory.Services.GetRequiredService<PostgresSettingsStore>();

        var providers = await store.ProvidersAsync();

        Assert.Equal(["claude", "gemini", "openai"], providers.Select(p => p.Name).Order());
        Assert.Equal("openai", providers.Single(p => p.IsDefault).Name);
    }

    [Fact]
    public async Task Stored_values_override_defaults_and_a_reset_brings_them_back()
    {
        var store = _factory.Services.GetRequiredService<PostgresSettingsStore>();

        await store.SaveSettingAsync(SettingsCatalog.Incidents, "CloseAfterMinutes", 30);
        var changed = await store.LoadSettingsAsync();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SaveSettingAsync(SettingsCatalog.Correlation, "UpstreamWeight", 2));
        await store.ResetSectionAsync(SettingsCatalog.Incidents);
        var reset = await store.LoadSettingsAsync();

        Assert.Equal(30, changed[SettingsCatalog.Incidents, "CloseAfterMinutes"]);
        Assert.False(changed.IsDefault(SettingsCatalog.Incidents, "CloseAfterMinutes"));
        Assert.Equal(15, reset[SettingsCatalog.Incidents, "CloseAfterMinutes"]);
    }

    [Fact]
    public async Task Api_key_is_stored_encrypted_and_only_its_hint_in_plain_text()
    {
        var store = _factory.Services.GetRequiredService<PostgresSettingsStore>();
        var protector = _factory.Services.GetRequiredService<ApiKeyProtector>();
        const string key = "sk-test-0123456789abcdef";

        await store.SetApiKeyAsync("openai", protector.Protect(key), ApiKeyProtector.Hint(key));
        var row = (await store.ProvidersAsync()).Single(p => p.Name == "openai");

        Assert.DoesNotContain(key, row.ProtectedApiKey);
        Assert.Equal(key, protector.Unprotect(row.ProtectedApiKey));
        Assert.Equal("sk-t••••••••cdef", row.KeyHint);
    }

    [Fact]
    public async Task Requests_are_logged_and_summed_per_provider_and_model()
    {
        var store = _factory.Services.GetRequiredService<PostgresSettingsStore>();
        var now = DateTimeOffset.UtcNow;

        await store.LogRequestAsync(new LlmRequestRow(0, now, "gemini", "gemini-3.5-flash-lite", null, "Olay açıklama", "sistem", "soru", "{}", 2000, 300, 1200, null));
        await store.LogRequestAsync(new LlmRequestRow(0, now, "gemini", "gemini-3.5-flash-lite", null, "Olay açıklama", "sistem", "soru", null, 0, 0, 900, "503"));

        var usage = (await store.UsageAsync(now.AddMinutes(-1))).Single();
        var failed = await store.RequestsAsync(now.AddMinutes(-1), "gemini", failedOnly: true);
        var detail = await store.RequestAsync(failed.Single().Id);

        Assert.Equal((2L, 1L, 2000L, 300L), (usage.Requests, usage.Failed, usage.InputTokens, usage.OutputTokens));
        Assert.Equal("soru", detail!.UserPrompt);
        Assert.Equal("503", detail.Error);
    }
}
