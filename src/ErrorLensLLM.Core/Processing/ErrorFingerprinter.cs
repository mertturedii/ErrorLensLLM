using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ErrorLensLLM.Core.Models;

namespace ErrorLensLLM.Core.Processing;

/// <summary>
/// Produces a stable id for an error's root cause: exception type + top application frames (without line numbers)
/// + normalized message. Line numbers are excluded so that the fingerprint survives deployments.
/// </summary>
public static partial class ErrorFingerprinter
{
    public const int FrameCount = 3;

    public static string Compute(string exceptionType, string normalizedMessage, IReadOnlyList<StackFrameInfo> frames)
    {
        var appFrames = frames
            .Where(f => f.IsApplicationCode)
            .Take(FrameCount)
            .Select(f => $"{CleanTypeName(f.TypeName)}.{CleanMethodName(f.MethodName)}")
            .ToList();

        var builder = new StringBuilder(exceptionType).Append('|');
        if (appFrames.Count > 0)
            builder.AppendJoin('>', appFrames);
        else
            builder.Append(normalizedMessage); // no own code in the stack: fall back to the message template

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }

    // Compiler-generated names for async state machines / lambdas: <GetKayit>d__4, <>c__DisplayClass2_0
    private static string CleanTypeName(string? typeName) =>
        typeName is null ? "?" : GeneratedTypeRegex().Replace(typeName, m => m.Groups["name"].Value);

    private static string CleanMethodName(string? methodName) =>
        methodName is null ? "?" : GeneratedMethodRegex().Replace(methodName, m => m.Groups["name"].Value);

    [GeneratedRegex(@"\+<(?<name>[^>]*)>[a-z]__\w+")]
    private static partial Regex GeneratedTypeRegex();

    [GeneratedRegex(@"^<(?<name>[^>]*)>[a-z]__\w+$")]
    private static partial Regex GeneratedMethodRegex();
}
