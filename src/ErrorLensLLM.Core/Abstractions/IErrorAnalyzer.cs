using ErrorLensLLM.Core.Models;

namespace ErrorLensLLM.Core.Abstractions;

/// <summary>LLM provider that explains an error and suggests a fix.</summary>
public interface IErrorAnalyzer
{
    string Provider { get; }

    Task<ErrorAnalysis> AnalyzeAsync(
        ErrorEvent error,
        IReadOnlyList<SourceCodeContext> code,
        CancellationToken ct = default);
}
