namespace ErrorLensLLM.Core.Models;

/// <summary>A single captured error occurrence.</summary>
public sealed record ErrorEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Application / module name that produced the error.</summary>
    public required string Application { get; init; }
    public string? Environment { get; init; }

    /// <summary>Commit SHA the application was built from (from AssemblyInformationalVersion).</summary>
    public string? CommitSha { get; init; }

    public required string ExceptionType { get; init; }
    public required string Message { get; init; }
    public required string StackTrace { get; init; }
    public IReadOnlyList<StackFrameInfo> Frames { get; init; } = [];

    public string? TraceId { get; init; }
    public string? RequestPath { get; init; }
    public string? RequestMethod { get; init; }
    public string? UserId { get; init; }

    /// <summary>Computed after normalization; groups identical root causes.</summary>
    public string Fingerprint { get; set; } = string.Empty;
}
