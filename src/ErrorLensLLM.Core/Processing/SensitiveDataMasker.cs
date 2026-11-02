using System.Text.RegularExpressions;

namespace ErrorLensLLM.Core.Processing;

/// <summary>Masks personal data (TC kimlik no, phone, e-mail, configured fields) before storing or sending to an LLM.</summary>
public sealed partial class SensitiveDataMasker
{
    public const string Mask = "***";

    private readonly Regex? _fieldRegex;

    public SensitiveDataMasker(IEnumerable<string>? sensitiveFields = null)
    {
        var fields = sensitiveFields?.Where(f => !string.IsNullOrWhiteSpace(f)).Select(Regex.Escape).ToArray() ?? [];
        if (fields.Length > 0)
        {
            // key=value, key: value, "key":"value with spaces"; the key must not be the tail of a longer word
            _fieldRegex = new Regex(
                $$"""(?<key>"?(?<![A-Za-z0-9_])(?:{{string.Join('|', fields)}})"?\s*[:=]\s*)(?:(?<quote>")[^"]*"|[^",;&\s}]+)""",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }
    }

    public string MaskText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? string.Empty;

        var result = EmailRegex().Replace(text, Mask);
        result = TcKimlikRegex().Replace(result, Mask);
        result = PhoneRegex().Replace(result, Mask);

        if (_fieldRegex is not null)
            result = _fieldRegex.Replace(result, m => m.Groups["quote"].Success ? $"{m.Groups["key"].Value}\"{Mask}\"" : $"{m.Groups["key"].Value}{Mask}");

        return result;
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?<!\d)[1-9]\d{10}(?!\d)")]
    private static partial Regex TcKimlikRegex();

    [GeneratedRegex(@"(?<!\d)(?:\+90|0)?\s?5\d{2}\s?\d{3}\s?\d{2}\s?\d{2}(?!\d)")]
    private static partial Regex PhoneRegex();
}
