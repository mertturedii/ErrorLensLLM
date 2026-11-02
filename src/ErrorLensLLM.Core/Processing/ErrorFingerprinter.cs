using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ErrorLensLLM.Core.Models;

namespace ErrorLensLLM.Core.Processing;

/// <summary>
/// Produces a stable id for an error's root cause: exception type + normalized message + top application frames
/// (without line numbers). Line numbers are excluded so that the fingerprint survives deployments; the message
/// template keeps different errors thrown from the same method apart.
/// </summary>
public static partial class ErrorFingerprinter
{
    public const int FrameCount = 3;

    public static string Compute(string exceptionType, string normalizedMessage, IReadOnlyList<StackFrameInfo> frames)
    {
        var appFrames = frames
            .Where(f => f.IsApplicationCode)
            .Take(FrameCount)
            .Select(FrameKey)
            .ToList();

        var builder = new StringBuilder(exceptionType)
            .Append('|').Append(normalizedMessage)
            .Append('|').AppendJoin('>', appFrames);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }

    /// <summary>
    /// "Type.Method" with compiler-generated names resolved to the source method, so that ordinals that change
    /// between builds (d__4, b__0, |4_0) do not change the fingerprint.
    /// </summary>
    public static string FrameKey(StackFrameInfo frame)
    {
        var type = frame.TypeName ?? "?";
        var method = frame.MethodName ?? "?";

        // Async method: Ogrenci.KayitService+<GetKayit>d__4.MoveNext → Ogrenci.KayitService.GetKayit
        var stateMachine = StateMachineRegex().Match(type);
        if (stateMachine.Success && method == "MoveNext")
            return $"{DisplayClassRegex().Replace(stateMachine.Groups["outer"].Value, "")}.{stateMachine.Groups["name"].Value}";

        // Lambda <GetKayit>b__0 → GetKayit; local function <Run>g__Load|4_0 → Run.Load
        var generated = GeneratedMethodRegex().Match(method);
        if (generated.Success)
        {
            method = generated.Groups["local"].Success
                ? $"{generated.Groups["name"].Value}.{generated.Groups["local"].Value}"
                : generated.Groups["name"].Value;
        }

        return $"{DisplayClassRegex().Replace(type, "")}.{method}";
    }

    [GeneratedRegex(@"^(?<outer>.+)\+<(?<name>[^>]+)>d__\d+$")]
    private static partial Regex StateMachineRegex();

    // Closure classes: +<>c, +<>c__DisplayClass2_0
    [GeneratedRegex(@"\+<>c(?:__DisplayClass[\d_]+)?")]
    private static partial Regex DisplayClassRegex();

    [GeneratedRegex(@"^<(?<name>[^>]+)>(?:g__(?<local>[^|]+)\|[\d_]+|[a-z]__[\d_]+)$")]
    private static partial Regex GeneratedMethodRegex();
}
