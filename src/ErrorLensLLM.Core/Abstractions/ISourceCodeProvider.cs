using ErrorLensLLM.Core.Models;

namespace ErrorLensLLM.Core.Abstractions;

/// <summary>Reads source code from a Git host with a read-only token.</summary>
public interface ISourceCodeProvider
{
    string Name { get; }

    Task<SourceCodeContext?> GetCodeAsync(StackFrameInfo frame, string? commitSha, CancellationToken ct = default);
}
