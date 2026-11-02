using System.Diagnostics;
using ErrorLensLLM.Core.Models;

namespace ErrorLensLLM.Core.Processing;

public static class StackFrameExtractor
{
    // "System" alone matches System.dll; "System." covers System.*; a bare "System" prefix would also catch e.g. "Systematic.Api"
    private static readonly string[] FrameworkAssemblies = ["System", "mscorlib", "netstandard"];

    private static readonly string[] FrameworkAssemblyPrefixes =
    [
        "System.", "Microsoft.", "Npgsql", "Serilog", "Newtonsoft.", "Dapper", "Polly", "AutoMapper", "MediatR",
        "FluentValidation", "Castle.", "Hangfire", "Swashbuckle", "Grpc.", "Google.Protobuf", "OpenTelemetry", "StackExchange.Redis"
    ];

    private static readonly HashSet<string> LibraryAssemblies =
    [
        "ErrorLensLLM.Core",
        "ErrorLensLLM.AspNetCore",
        "ErrorLensLLM.Serilog",
        "ErrorLensLLM.Storage.PostgreSql",
        "ErrorLensLLM.Source.Bitbucket",
        "ErrorLensLLM.AI.Claude"
    ];

    private static bool IsApplicationAssembly(string? assemblyName) =>
        assemblyName is not null
        && !LibraryAssemblies.Contains(assemblyName)
        && !FrameworkAssemblies.Contains(assemblyName)
        && !FrameworkAssemblyPrefixes.Any(assemblyName.StartsWith);

    /// <summary>Extracts frames with file/line info (requires portable PDBs next to the assemblies).</summary>
    public static IReadOnlyList<StackFrameInfo> Extract(Exception exception) => ExtractOrigin(exception).Frames;

    /// <summary>
    /// Finds the exception whose stack shows where the failure entered application code: the innermost one that has
    /// application frames. A wrapped driver error (DbUpdateException → PostgresException) only has library frames in
    /// the inner exception, so the wrapper is used then. Falls back to the innermost exception.
    /// </summary>
    public static (Exception Origin, IReadOnlyList<StackFrameInfo> Frames) ExtractOrigin(Exception exception)
    {
        var chain = new List<Exception>();
        for (var current = exception; current is not null; current = current.InnerException)
            chain.Add(current);

        (Exception, IReadOnlyList<StackFrameInfo>)? innermost = null;
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var frames = Frames(chain[i]);
            innermost ??= (chain[i], frames);
            if (frames.Any(f => f.IsApplicationCode))
                return (chain[i], frames);
        }

        return innermost!.Value;
    }

    private static IReadOnlyList<StackFrameInfo> Frames(Exception exception) =>
        new StackTrace(exception, fNeedFileInfo: true).GetFrames()
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
