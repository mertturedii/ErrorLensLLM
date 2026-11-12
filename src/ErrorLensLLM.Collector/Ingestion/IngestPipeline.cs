using System.Text.Json;
using System.Text.RegularExpressions;
using ErrorLensLLM.Core.Ingestion;
using ErrorLensLLM.Core.Processing;
using ErrorLensLLM.Ml.Parsing;
using ErrorLensLLM.Storage.PostgreSql;

namespace ErrorLensLLM.Collector.Ingestion;

/// <summary>A log event ready to store once its template text has been resolved to an id.</summary>
/// <param name="TemplateText">Serilog message template, or the Drain template for plain-text messages.</param>
/// <param name="TemplateSource">"template" when the application supplied it, "drain" when it was mined.</param>
/// <param name="Row">The row with <see cref="LogEventRow.TemplateId"/> still 0.</param>
public sealed record PreparedEvent(string TemplateText, string TemplateSource, LogEventRow Row);

public sealed record PreparedBatch(IReadOnlyList<PreparedEvent> Events, IReadOnlyList<ErrorOccurrence> Errors);

/// <summary>
/// Turns a received batch into storable rows: masks personal data first, then assigns a template and, for
/// exceptions, an error fingerprint. Holds no database state, so it can be tested in isolation.
/// </summary>
public sealed partial class IngestPipeline
{
    public const int MaxMessageLength = 8_000;
    public const int MaxStackTraceLength = 32_000;

    private static readonly HashSet<string> Levels = ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];

    private readonly SensitiveDataMasker _masker;
    private readonly DrainParser _drain;
    private readonly object _drainLock = new();

    public IngestPipeline(SensitiveDataMasker masker, double drainSimilarity = 0.8)
    {
        _masker = masker;

        // Constant-token similarity with path masking was the most accurate setting on HDFS (GA 0.998 at 0.8)
        _drain = new DrainParser(similarityThreshold: drainSimilarity, similarity: DrainSimilarity.ConstantTokens, maskPatterns:
        [
            GuidRegex(),
            IpRegex(),
            PathRegex(),
            NumberRegex()
        ]);
    }

    public PreparedBatch Prepare(IngestBatch batch)
    {
        var source = batch.Source;
        var events = new List<PreparedEvent>(batch.Events.Count);
        var errors = new List<ErrorOccurrence>();

        foreach (var e in batch.Events)
        {
            // Npgsql only writes UTC values to timestamptz
            var timestamp = e.Timestamp.ToUniversalTime();
            var message = Truncate(_masker.MaskText(e.Message), MaxMessageLength);
            var (templateText, templateSource) = Template(e.MessageTemplate, message);

            string? fingerprint = null;
            string? exceptionMessage = null;
            string? stackTrace = null;
            if (e.Exception is { } exception)
            {
                exceptionMessage = Truncate(_masker.MaskText(exception.Message), MaxMessageLength);
                stackTrace = Truncate(_masker.MaskText(exception.StackTrace), MaxStackTraceLength);

                var normalized = MessageNormalizer.Normalize(exceptionMessage);
                fingerprint = ErrorFingerprinter.Compute(exception.Type, normalized, exception.Frames);
                var topFrame = exception.Frames.FirstOrDefault(f => f.IsApplicationCode) is { } frame ? ErrorFingerprinter.FrameKey(frame) : null;
                errors.Add(new ErrorOccurrence(fingerprint, exception.Type, normalized, topFrame, source.Application, timestamp));
            }

            var row = new LogEventRow
            {
                Timestamp = timestamp,
                Application = source.Application,
                Environment = source.Environment,
                Host = source.Host,
                Level = Levels.Contains(e.Level) ? e.Level : "Information",
                TemplateId = 0,
                Message = message,
                PropertiesJson = MaskProperties(e.Properties),
                TraceId = e.TraceId,
                SpanId = e.SpanId,
                ExceptionType = e.Exception?.Type,
                ExceptionMessage = exceptionMessage,
                StackTrace = stackTrace,
                Fingerprint = fingerprint
            };

            events.Add(new PreparedEvent(templateText, templateSource, row));
        }

        return new PreparedBatch(events, errors);
    }

    private (string Text, string Source) Template(string? messageTemplate, string maskedMessage)
    {
        // Structured logging already separates the constant text from the values; no need to mine it
        if (!string.IsNullOrWhiteSpace(messageTemplate))
            return (Truncate(messageTemplate, MaxMessageLength), "template");

        lock (_drainLock)
            return (_drain.Parse(maskedMessage).Text, "drain");
    }

    private string? MaskProperties(IReadOnlyDictionary<string, string>? properties)
    {
        if (properties is null || properties.Count == 0)
            return null;

        // A property named like a secret is masked whole; other values go through the text masker
        var masked = properties.ToDictionary(
            p => p.Key,
            p => _masker.IsSensitiveField(p.Key) ? SensitiveDataMasker.Mask : _masker.MaskText(p.Value));
        return JsonSerializer.Serialize(masked);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex GuidRegex();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}(?::\d+)?\b")]
    private static partial Regex IpRegex();

    [GeneratedRegex(@"(?<=^|\s)/\S+")]
    private static partial Regex PathRegex();

    // Same numeric mask as the LogHub benchmark: numbers that are not part of an identifier
    [GeneratedRegex(@"(?<=[^A-Za-z0-9])(\-?\+?\d+)(?=[^A-Za-z0-9])|[0-9]+$")]
    private static partial Regex NumberRegex();
}
