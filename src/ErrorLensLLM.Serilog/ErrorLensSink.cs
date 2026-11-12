using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using ErrorLensLLM.Core.Ingestion;
using ErrorLensLLM.Core.Processing;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace ErrorLensLLM.Serilog;

/// <summary>
/// Sends log events to the ErrorLensLLM collector in batches. Runs behind Serilog's batching queue, so a slow or
/// unavailable collector never blocks the application: failed batches are retried, then dropped (see SelfLog).
/// </summary>
internal sealed class ErrorLensSink : IBatchedLogEventSink, IDisposable
{
    private readonly HttpClient _http;
    private readonly IngestSource _source;
    private bool _announced;

    public ErrorLensSink(ErrorLensSinkOptions options, HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = options.CollectorUrl;
        _http.Timeout = TimeSpan.FromSeconds(10);
        if (!string.IsNullOrEmpty(options.ApiKey))
            _http.DefaultRequestHeaders.Add("X-ErrorLens-Key", options.ApiKey);

        var (version, commit) = BuildInfo(Assembly.GetEntryAssembly());
        _source = new IngestSource
        {
            Application = options.Application,
            Environment = options.Environment,
            Host = Environment.MachineName,
            Version = options.Version ?? version,
            CommitSha = options.CommitSha ?? commit,
            StartedAt = Process.GetCurrentProcess().StartTime.ToUniversalTime()
        };
    }

    public Task EmitBatchAsync(IReadOnlyCollection<LogEvent> batch) => SendAsync(batch.Select(Map).ToList());

    /// <summary>Announces the deployment even when the application has not logged anything yet.</summary>
    public Task OnEmptyBatchAsync() => _announced ? Task.CompletedTask : SendAsync([]);

    public void Dispose() => _http.Dispose();

    private async Task SendAsync(IReadOnlyList<IngestLogEvent> events)
    {
        // Throwing lets Serilog retry the batch with back-off
        using var response = await _http.PostAsJsonAsync("api/ingest/logs", new IngestBatch { Source = _source, Events = events });
        response.EnsureSuccessStatusCode();
        _announced = true;
    }

    internal static IngestLogEvent Map(LogEvent e) => new()
    {
        Timestamp = e.Timestamp,
        Level = e.Level.ToString(),
        MessageTemplate = e.MessageTemplate.Text,
        Message = RenderLiteral(e),
        Properties = e.Properties.ToDictionary(p => p.Key, p => Render(p.Value)),
        TraceId = e.TraceId?.ToHexString(),
        SpanId = e.SpanId?.ToHexString(),
        Exception = e.Exception is null ? null : MapException(e.Exception)
    };

    private static IngestException MapException(Exception exception)
    {
        // Frames need reflection and PDBs, so they can only be read here, inside the application
        var (origin, frames) = StackFrameExtractor.ExtractOrigin(exception);
        return new IngestException(origin.GetType().FullName ?? origin.GetType().Name, origin.Message, exception.ToString(), frames);
    }

    /// <summary>Renders strings without Serilog's quotes, so templates that quote values themselves stay readable.</summary>
    private static string RenderLiteral(LogEvent e)
    {
        using var writer = new StringWriter();
        LiteralMessage.Format(e, writer);
        return writer.ToString();
    }

    private static readonly MessageTemplateTextFormatter LiteralMessage = new("{Message:l}");

    private static string Render(LogEventPropertyValue value) =>
        value is ScalarValue { Value: var scalar } ? scalar?.ToString() ?? "" : value.ToString();

    private static (string? Version, string? CommitSha) BuildInfo(Assembly? assembly) =>
        assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is { } informational
            ? ParseInformationalVersion(informational)
            : (assembly?.GetName().Version?.ToString(), null);

    /// <summary>"1.4.0+3f2a9c1…" (the SDK appends the git commit) → ("1.4.0", "3f2a9c1…").</summary>
    internal static (string Version, string? CommitSha) ParseInformationalVersion(string informational)
    {
        var plus = informational.IndexOf('+');
        return plus < 0 ? (informational, null) : (informational[..plus], informational[(plus + 1)..]);
    }
}
