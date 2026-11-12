using ErrorLensLLM.Core.Incidents;
using Microsoft.Extensions.Options;

namespace ErrorLensLLM.Dashboard;

public sealed class DashboardOptions
{
    public const string Section = "Dashboard";

    /// <summary>Times are stored in UTC and shown in this zone.</summary>
    public string TimeZone { get; set; } = "Europe/Istanbul";

    public int RefreshSeconds { get; set; } = 10;
}

/// <summary>Turkish labels and local times for the screens.</summary>
public sealed class DashboardFormat(IOptions<DashboardOptions> options)
{
    private readonly TimeZoneInfo _zone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);

    public TimeSpan Refresh => TimeSpan.FromSeconds(options.Value.RefreshSeconds);

    public DateTimeOffset Local(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, _zone);

    public string Time(DateTimeOffset value) => Local(value).ToString("HH:mm:ss");

    public string TimeWithMs(DateTimeOffset value) => Local(value).ToString("HH:mm:ss.fff");

    public string DateTime(DateTimeOffset value) => Local(value).ToString("dd.MM.yyyy HH:mm:ss");

    public static string Duration(TimeSpan span) => span.TotalSeconds switch
    {
        < 60 => $"{span.TotalSeconds:F0} sn",
        < 3600 => $"{(int)span.TotalMinutes} dk {span.Seconds} sn",
        _ => $"{(int)span.TotalHours} sa {span.Minutes} dk"
    };

    public static string Category(ErrorCategory category) => category switch
    {
        ErrorCategory.DatabaseSchema => "Veritabanı şeması",
        ErrorCategory.Database => "Veritabanı",
        ErrorCategory.Dependency => "Bağımlı servis",
        ErrorCategory.Timeout => "Zaman aşımı",
        ErrorCategory.CodeDefect => "Kod hatası",
        ErrorCategory.Validation => "Doğrulama",
        ErrorCategory.Authorization => "Yetkilendirme",
        ErrorCategory.Configuration => "Yapılandırma",
        _ => "Bilinmiyor"
    };

    public static string Status(string status) => status switch
    {
        "open" => "Açık",
        "closed" => "Kapandı",
        _ => status
    };

    /// <summary>"System.Net.Http.HttpRequestException" → "HttpRequestException".</summary>
    public static string ShortType(string? type) => type is null ? "" : type[(type.LastIndexOf('.') + 1)..];

    public static string ShortCommit(string? sha) => sha is { Length: > 8 } ? sha[..8] : sha ?? "";
}
