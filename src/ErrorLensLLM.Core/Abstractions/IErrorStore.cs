using ErrorLensLLM.Core.Models;

namespace ErrorLensLLM.Core.Abstractions;

public interface IErrorStore
{
    Task EnsureSchemaAsync(CancellationToken ct = default);

    /// <summary>Saves the occurrence and upserts its group. Returns the group after the update.</summary>
    Task<ErrorGroup> SaveOccurrenceAsync(ErrorEvent error, string normalizedMessage, CancellationToken ct = default);

    Task<ErrorAnalysis?> GetActiveAnalysisAsync(string fingerprint, CancellationToken ct = default);
    Task SaveAnalysisAsync(ErrorAnalysis analysis, CancellationToken ct = default);
    Task UpdateGroupStatusAsync(string fingerprint, ErrorGroupStatus status, CancellationToken ct = default);
}
