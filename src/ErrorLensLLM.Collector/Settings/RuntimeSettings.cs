using ErrorLensLLM.Core.Incidents;
using ErrorLensLLM.Ml.Streaming;
using ErrorLensLLM.Storage.PostgreSql;
using static ErrorLensLLM.Storage.PostgreSql.SettingsCatalog;

namespace ErrorLensLLM.Collector.Settings;

/// <summary>
/// The values set on the settings screen, read from the database at most every <see cref="CacheFor"/>; the background
/// services ask for them on every run, so a change takes effect within seconds and without a restart. When the database
/// cannot be read, the last values (or the defaults) are kept.
/// </summary>
public sealed class RuntimeSettings(PostgresSettingsStore store, ILogger<RuntimeSettings> log)
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private SettingsSnapshot _snapshot = SettingsSnapshot.Defaults;
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    public async Task<SettingsSnapshot> GetAsync(CancellationToken ct = default)
    {
        if (DateTimeOffset.UtcNow - _loadedAt < CacheFor)
            return _snapshot;

        await _lock.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow - _loadedAt >= CacheFor)
            {
                try
                {
                    _snapshot = await store.LoadSettingsAsync(ct);
                }
                catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException)
                {
                    log.LogWarning(ex, "Settings could not be read; the previous values stay in use");
                }

                _loadedAt = DateTimeOffset.UtcNow;
            }

            return _snapshot;
        }
        finally
        {
            _lock.Release();
        }
    }

    public static CorrelationOptions Correlation(SettingsSnapshot s) => new()
    {
        UpstreamWeight = s[SettingsCatalog.Correlation, "UpstreamWeight"],
        DeploymentWeight = s[SettingsCatalog.Correlation, "DeploymentWeight"],
        OnsetWeight = s[SettingsCatalog.Correlation, "OnsetWeight"],
        DeploymentWindow = TimeSpan.FromMinutes(s[SettingsCatalog.Correlation, "DeploymentWindowMinutes"]),
        DeploymentLeadTime = TimeSpan.FromSeconds(s[SettingsCatalog.Correlation, "DeploymentLeadSeconds"]),
        CoOnsetWindow = TimeSpan.FromSeconds(s[SettingsCatalog.Correlation, "CoOnsetWindowSeconds"]),
        EpisodeGap = TimeSpan.FromMinutes(s[SettingsCatalog.Correlation, "EpisodeGapMinutes"])
    };

    public static StreamAnomalyOptions Stream(SettingsSnapshot s) => new()
    {
        TargetEventRate = s[SettingsCatalog.Anomaly, "TargetEventRate"],
        MinBucketShare = s[SettingsCatalog.Anomaly, "MinBucketShare"],
        MinAnomalousEvents = s.Int(SettingsCatalog.Anomaly, "MinAnomalousEvents"),
        VolumeSigmas = s[SettingsCatalog.Anomaly, "VolumeSigmas"],
        MinVolumeChange = s[SettingsCatalog.Anomaly, "MinVolumeChange"]
    };
}
