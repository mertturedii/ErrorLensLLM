namespace ErrorLensLLM.Core.Models;

/// <summary>Source code fetched from the repository for a stack frame.</summary>
public sealed record SourceCodeContext(
    string Repository,
    string CommitSha,
    string Path,
    int LineNumber,
    string Code,
    int StartLine,
    string? WebUrl);
