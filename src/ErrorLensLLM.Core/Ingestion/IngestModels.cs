using ErrorLensLLM.Core.Models;

namespace ErrorLensLLM.Core.Ingestion;

/// <summary>Identifies the running application instance that produced a batch.</summary>
public sealed record IngestSource
{
    public required string Application { get; init; }
    public string? Environment { get; init; }
    public string? Host { get; init; }
    public string? Version { get; init; }

    /// <summary>Commit the build came from (the "+sha" suffix of AssemblyInformationalVersion).</summary>
    public string? CommitSha { get; init; }

    /// <summary>Process start; together with the version it marks a deployment.</summary>
    public DateTimeOffset StartedAt { get; init; }
}

/// <summary>Body of <c>POST /api/ingest/logs</c>.</summary>
public sealed record IngestBatch
{
    public required IngestSource Source { get; init; }
    public required IReadOnlyList<IngestLogEvent> Events { get; init; }
}

public sealed record IngestLogEvent
{
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Verbose, Debug, Information, Warning, Error or Fatal.</summary>
    public required string Level { get; init; }

    /// <summary>Structured-logging template ("Kayit {OgrenciId} bulunamadi"); empty for plain text logs.</summary>
    public string? MessageTemplate { get; init; }

    public required string Message { get; init; }
    public IReadOnlyDictionary<string, string>? Properties { get; init; }

    /// <summary>W3C trace context (the ids OpenTelemetry and System.Diagnostics.Activity use).</summary>
    public string? TraceId { get; init; }
    public string? SpanId { get; init; }

    public IngestException? Exception { get; init; }
}

/// <param name="Type">Type of the exception that reaches application code (see StackFrameExtractor.ExtractOrigin).</param>
/// <param name="Message">Its message.</param>
/// <param name="StackTrace">Full <c>ToString()</c> of the logged exception, including inner exceptions.</param>
/// <param name="Frames">Frames of the origin exception; extracted in-process because they need reflection and PDBs.</param>
public sealed record IngestException(string Type, string Message, string StackTrace, IReadOnlyList<StackFrameInfo> Frames);
