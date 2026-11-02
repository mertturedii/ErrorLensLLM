namespace ErrorLensLLM.Core.Models;

public sealed record StackFrameInfo(
    string? TypeName,
    string? MethodName,
    string? FilePath,
    int? LineNumber,
    string? AssemblyName,
    bool IsApplicationCode);
