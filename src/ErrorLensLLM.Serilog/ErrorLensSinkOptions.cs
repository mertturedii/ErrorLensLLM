namespace ErrorLensLLM.Serilog;

public sealed class ErrorLensSinkOptions
{
    /// <summary>Base address of the collector, e.g. http://localhost:5025/.</summary>
    public required Uri CollectorUrl { get; init; }

    /// <summary>Application / module name, e.g. "kayit".</summary>
    public required string Application { get; init; }

    public string? Environment { get; init; }
    public string? ApiKey { get; init; }

    /// <summary>Defaults to the entry assembly's informational version.</summary>
    public string? Version { get; init; }

    /// <summary>Defaults to the "+sha" suffix of the informational version.</summary>
    public string? CommitSha { get; init; }

    public int BatchSize { get; init; } = 500;
    public TimeSpan Period { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Events kept while the collector is unreachable; older ones are dropped beyond this.</summary>
    public int QueueLimit { get; init; } = 10_000;
}
