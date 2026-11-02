using System.Text.RegularExpressions;

namespace ErrorLensLLM.Core.Processing;

/// <summary>
/// Replaces variable parts of an error message (ids, numbers, dates, quoted values)
/// so that the same error with different data produces the same template.
/// </summary>
public static partial class MessageNormalizer
{
    public static string Normalize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return string.Empty;

        var result = GuidRegex().Replace(message, "<guid>");
        result = DateTimeRegex().Replace(result, "<date>");
        result = QuotedRegex().Replace(result, "'<val>'");
        result = HexRegex().Replace(result, "<hex>");
        result = NumberRegex().Replace(result, "<num>");
        result = WhitespaceRegex().Replace(result, " ");

        return result.Trim();
    }

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex GuidRegex();

    // A four-digit year first or last, so that version numbers like 1.2.3 are not dates
    [GeneratedRegex(@"\b(?:\d{4}[-./]\d{1,2}[-./]\d{1,2}|\d{1,2}[-./]\d{1,2}[-./]\d{4})(?:[ T]\d{1,2}:\d{2}(?::\d{2})?(?:\.\d+)?(?:Z|[+-]\d{2}:?\d{2})?)?")]
    private static partial Regex DateTimeRegex();

    // A quote only opens after a separator, so Turkish suffix apostrophes (Öğrenci'nin, Ankara'da) are not values
    [GeneratedRegex(@"(?<=^|[\s=:(\[,])(?:'[^']*'|""[^""]*"")(?=$|[\s,.;:)\]])")]
    private static partial Regex QuotedRegex();

    [GeneratedRegex(@"\b0x[0-9a-fA-F]+\b")]
    private static partial Regex HexRegex();

    [GeneratedRegex(@"(?<![A-Za-z_])\d+(?:\.\d+)?")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
