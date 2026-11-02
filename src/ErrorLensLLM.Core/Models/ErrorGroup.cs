namespace ErrorLensLLM.Core.Models;

/// <summary>All occurrences sharing the same fingerprint.</summary>
public sealed record ErrorGroup
{
    public required string Fingerprint { get; init; }
    public required string ExceptionType { get; init; }
    public required string NormalizedMessage { get; init; }
    public string? TopFrame { get; init; }
    public long OccurrenceCount { get; set; }
    public DateTimeOffset FirstSeen { get; init; }
    public DateTimeOffset LastSeen { get; set; }
    public IReadOnlyList<string> Applications { get; set; } = [];
    public ErrorGroupStatus Status { get; set; } = ErrorGroupStatus.New;
}

public enum ErrorGroupStatus
{
    New,
    Analyzing,
    Analyzed,
    Resolved,
    Ignored
}
