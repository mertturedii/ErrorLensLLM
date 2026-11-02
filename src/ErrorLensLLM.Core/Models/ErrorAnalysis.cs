namespace ErrorLensLLM.Core.Models;

/// <summary>LLM-produced explanation and fix suggestion for an error group.</summary>
public sealed record ErrorAnalysis
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Fingerprint { get; init; }
    public required string Cause { get; init; }
    public required string Solution { get; init; }
    public string? Category { get; init; }
    public AnalysisConfidence Confidence { get; init; }

    /// <summary>File:line the analysis points to.</summary>
    public string? Location { get; init; }

    public required string Provider { get; init; }
    public required string Model { get; init; }
    public string? CommitSha { get; init; }

    /// <summary>Hash of the code block sent to the LLM; a change invalidates this analysis.</summary>
    public string? CodeHash { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public AnalysisStatus Status { get; set; } = AnalysisStatus.Active;
}

public enum AnalysisConfidence
{
    Low,
    Medium,
    High
}

public enum AnalysisStatus
{
    Active,
    /// <summary>Developer marked it wrong or the code changed.</summary>
    Invalidated
}
