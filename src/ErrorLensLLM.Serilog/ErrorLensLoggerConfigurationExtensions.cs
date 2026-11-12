using Serilog;
using Serilog.Configuration;
using Serilog.Events;

namespace ErrorLensLLM.Serilog;

public static class ErrorLensLoggerConfigurationExtensions
{
    /// <summary>
    /// Sends events to the ErrorLensLLM collector:
    /// <c>.WriteTo.ErrorLens(new() { CollectorUrl = new("http://localhost:5025/"), Application = "kayit" })</c>.
    /// </summary>
    public static LoggerConfiguration ErrorLens(
        this LoggerSinkConfiguration sinkConfiguration,
        ErrorLensSinkOptions options,
        LogEventLevel restrictedToMinimumLevel = LogEventLevel.Information) =>
        sinkConfiguration.Sink(
            new ErrorLensSink(options),
            new BatchingOptions
            {
                BatchSizeLimit = options.BatchSize,
                BufferingTimeLimit = options.Period,
                QueueLimit = options.QueueLimit,
                EagerlyEmitFirstEvent = true
            },
            restrictedToMinimumLevel);
}
