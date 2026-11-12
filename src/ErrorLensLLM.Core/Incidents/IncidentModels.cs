namespace ErrorLensLLM.Core.Incidents;

/// <summary>One stored error occurrence, as read back for correlation.</summary>
public sealed record ErrorSample(
    string Fingerprint,
    string Application,
    string ExceptionType,
    string NormalizedMessage,
    string? TopFrame,
    DateTimeOffset Timestamp,
    string? TraceId);

public sealed record DeploymentInfo(Guid Id, string Application, string? Version, string? CommitSha, DateTimeOffset StartedAt);

/// <summary>One error group's part in an incident, with the evidence behind its root cause score.</summary>
public sealed record IncidentMember(
    string Fingerprint,
    string Application,
    string ExceptionType,
    string NormalizedMessage,
    string? TopFrame,
    ErrorCategory Category,
    long Occurrences,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    double RootCauseScore,
    IReadOnlyList<string> Evidence);

/// <summary>Correlated error groups that share one likely root cause. Members are ordered by root cause score.</summary>
public sealed record IncidentDraft(
    DateTimeOffset StartedAt,
    DateTimeOffset LastSeen,
    IReadOnlyList<IncidentMember> Members,
    DeploymentInfo? SuspectedDeployment)
{
    public IncidentMember RootCause => Members[0];
    public IEnumerable<string> Applications => Members.Select(m => m.Application).Distinct().Order();
    public long Occurrences => Members.Sum(m => m.Occurrences);
}

public enum ErrorCategory
{
    Unknown,

    /// <summary>Missing column/table, failed migration: the code and the database schema disagree.</summary>
    DatabaseSchema,

    /// <summary>Other database errors: constraint, deadlock, connection.</summary>
    Database,

    /// <summary>A called service failed or refused the connection; usually a symptom of another error.</summary>
    Dependency,

    Timeout,

    /// <summary>A defect in the application's own code (null reference, bad cast, index out of range).</summary>
    CodeDefect,

    Validation,
    Authorization,
    Configuration
}
