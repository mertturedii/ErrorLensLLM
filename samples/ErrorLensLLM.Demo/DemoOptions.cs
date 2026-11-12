using System.Collections.Concurrent;
using System.Diagnostics;

namespace ErrorLensLLM.Demo;

public sealed class DemoOptions
{
    public const string Section = "Demo";

    /// <summary>kayit (enrollment), not (grades), odeme (payments) or traffic (load generator).</summary>
    public string Module { get; set; } = "kayit";

    /// <summary>Reported as the deployment version; change it together with Faults to simulate a release.</summary>
    public string Version { get; set; } = "1.0.0";

    public string Environment { get; set; } = "demo";

    /// <summary>Injected faults, see <see cref="Faults"/>.</summary>
    public string[] Faults { get; set; } = [];

    /// <summary>Configured faults stay dormant this long after start, like a code path reached only later.</summary>
    public int FaultDelaySeconds { get; set; }

    public Uri CollectorUrl { get; set; } = new("http://localhost:5025/");
    public Dictionary<string, Uri> Modules { get; set; } = [];
    public TrafficOptions Traffic { get; set; } = new();

    private static readonly DateTime ProcessStart = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    /// <summary>Faults switched on at runtime (POST /admin/faults/{name}), without a release.</summary>
    public static ConcurrentDictionary<string, bool> RuntimeFaults { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Has(string fault) =>
        RuntimeFaults.ContainsKey(fault)
        || (Faults.Contains(fault, StringComparer.OrdinalIgnoreCase) && DateTime.UtcNow - ProcessStart >= TimeSpan.FromSeconds(FaultDelaySeconds));
}

public sealed class TrafficOptions
{
    public int RequestsPerSecond { get; set; } = 20;
}

/// <summary>Fault injection scenarios with a known root cause, used to evaluate root cause suggestions.</summary>
public static class Faults
{
    /// <summary>kayit 1.1 reads a column whose migration was never applied → PostgresException; not and odeme fail through it.</summary>
    public const string KayitMissingColumn = "kayit-missing-column";

    /// <summary>The bank behind odeme stops answering in time → TimeoutException in odeme only.</summary>
    public const string OdemeBankTimeout = "odeme-bank-timeout";

    /// <summary>not 1.1 dereferences a missing elective course → NullReferenceException for some students.</summary>
    public const string NotNullReference = "not-null-reference";

    /// <summary>odeme 1.1 logs bank rejections with LogError but throws nothing; the failure is only in the log text.</summary>
    public const string OdemeLoggedError = "odeme-logged-error";

    public static readonly string[] All = [KayitMissingColumn, OdemeBankTimeout, NotNullReference, OdemeLoggedError];
}
