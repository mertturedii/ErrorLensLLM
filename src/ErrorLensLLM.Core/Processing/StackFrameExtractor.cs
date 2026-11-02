using System.Diagnostics;
using ErrorLensLLM.Core.Models;

namespace ErrorLensLLM.Core.Processing;

public static class StackFrameExtractor
{
    private static readonly string[] FrameworkAssemblyPrefixes = ["System", "Microsoft.", "netstandard", "mscorlib", "Npgsql", "Serilog"];

    private static readonly HashSet<string> LibraryAssemblies =
    [
        "ErrorLensLLM.Core",
        "ErrorLensLLM.AspNetCore",
        "ErrorLensLLM.Storage.PostgreSql",
        "ErrorLensLLM.Source.Bitbucket",
        "ErrorLensLLM.AI.Claude"
    ];

    private static bool IsApplicationAssembly(string? assemblyName) =>
        assemblyName is not null
        && !LibraryAssemblies.Contains(assemblyName)
        && !FrameworkAssemblyPrefixes.Any(assemblyName.StartsWith);

    /// <summary>Extracts frames with file/line info (requires portable PDBs next to the assemblies).</summary>
    public static IReadOnlyList<StackFrameInfo> Extract(Exception exception)
    {
        // Innermost exception holds the real origin of the failure
        var root = exception;
        while (root.InnerException is not null)
            root = root.InnerException;

        var frames = new StackTrace(root, fNeedFileInfo: true).GetFrames();
        return frames
            .Select(f =>
            {
                var method = f.GetMethod();
                var type = method?.DeclaringType;
                var assemblyName = type?.Assembly.GetName().Name;
                return new StackFrameInfo(
                    TypeName: type?.FullName,
                    MethodName: method?.Name,
                    FilePath: f.GetFileName(),
                    LineNumber: f.GetFileLineNumber() is > 0 and var line ? line : null,
                    AssemblyName: assemblyName,
                    IsApplicationCode: IsApplicationAssembly(assemblyName));
            })
            .ToList();
    }
}
