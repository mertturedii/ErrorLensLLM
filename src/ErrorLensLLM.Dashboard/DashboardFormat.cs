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
    private static readonly System.Globalization.CultureInfo Turkish = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");

    private readonly TimeZoneInfo _zone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);

    public TimeSpan Refresh => TimeSpan.FromSeconds(options.Value.RefreshSeconds);

    public DateTimeOffset Local(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, _zone);

    public string Time(DateTimeOffset value) => Local(value).ToString("HH:mm:ss");

    public string TimeWithMs(DateTimeOffset value) => Local(value).ToString("HH:mm:ss.fff");

    public string DateTime(DateTimeOffset value) => Local(value).ToString("dd.MM.yyyy HH:mm:ss");

    /// <summary>Time alone for today, with the date in front for anything older.</summary>
    public string Stamp(DateTimeOffset value)
    {
        var local = Local(value);
        return local.Date == Local(DateTimeOffset.UtcNow).Date ? local.ToString("HH:mm:ss") : local.ToString("dd.MM HH:mm");
    }

    /// <summary>Axis label for a chart bucket: long buckets span days, so they carry the date.</summary>
    public string Tick(DateTimeOffset value, TimeSpan bucket) =>
        Local(value).ToString(bucket >= TimeSpan.FromHours(3) ? "dd.MM HH:mm" : bucket >= TimeSpan.FromMinutes(1) ? "HH:mm" : "HH:mm:ss");

    public static string Relative(DateTimeOffset value)
    {
        var age = DateTimeOffset.UtcNow - value;
        return age.TotalSeconds switch
        {
            < 10 => "az önce",
            < 60 => $"{age.TotalSeconds:F0} sn önce",
            < 3600 => $"{(int)age.TotalMinutes} dk önce",
            < 86400 => $"{(int)age.TotalHours} sa önce",
            _ => $"{(int)age.TotalDays} gün önce"
        };
    }

    public static string Number(long value) => value.ToString("N0", Turkish);

    /// <summary>"%12,4"; a dash when there is nothing to divide by.</summary>
    public static string Rate(long part, long total) =>
        total == 0 ? "—" : "%" + (100.0 * part / total).ToString(part * 10 >= total || part == 0 ? "0" : "0.0", Turkish);

    public static string Level(string level) => level switch
    {
        "Fatal" => "Kritik",
        "Error" => "Hata",
        "Warning" => "Uyarı",
        "Information" => "Bilgi",
        _ => level
    };

    public static bool IsError(string level) => level is "Error" or "Fatal";

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
        "merged" => "Birleştirildi",
        _ => status
    };

    /// <summary>"System.Net.Http.HttpRequestException" → "HttpRequestException".</summary>
    public static string ShortType(string? type) => type is null ? "" : type[(type.LastIndexOf('.') + 1)..];

    public static string ShortCommit(string? sha) => sha is { Length: > 8 } ? sha[..8] : sha ?? "";
}

/// <summary>A look-back window offered on the overview and monitoring screens.</summary>
public sealed record TimeRange(string Label, TimeSpan Span)
{
    public static readonly IReadOnlyList<TimeRange> All =
    [
        new("15 dk", TimeSpan.FromMinutes(15)),
        new("1 sa", TimeSpan.FromHours(1)),
        new("24 sa", TimeSpan.FromHours(24)),
        new("7 gün", TimeSpan.FromDays(7))
    ];

    /// <summary>The shortest window that still contains the latest log, so a screen never opens empty while data exists.</summary>
    public static TimeRange Fit(DateTimeOffset? latestLog) =>
        All.FirstOrDefault(r => latestLog >= DateTimeOffset.UtcNow - r.Span) ?? All[1];
}

/// <summary>What one browser session chose, kept while it moves between screens.</summary>
public sealed class DashboardSession
{
    public TimeRange? Range { get; set; }
}

/// <summary>
/// One color slot per application for the whole dashboard, so "kayit" is the same color on every screen. Slots follow
/// the alphabetical order at first load; applications that appear later are appended and never reshuffle the others.
/// </summary>
public sealed class ModuleColors(ErrorLensLLM.Storage.PostgreSql.PostgresDashboardStore store)
{
    private volatile string[] _order = [];

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var known = _order;
        var added = (await store.ModulesAsync(ct)).Select(m => m.Application).Except(known).Order().ToArray();
        if (added.Length > 0)
            _order = [.. known, .. added];
    }

    public string Of(string application)
    {
        var index = Array.IndexOf(_order, application);
        return index is >= 0 and < 5 ? $"var(--series-{index + 1})" : "var(--series-other)";
    }

    /// <summary>Applications in slot order; unknown ones last, by name.</summary>
    public IReadOnlyList<string> Sort(IEnumerable<string> applications)
    {
        var order = _order;
        return applications.Distinct()
            .OrderBy(a => Array.IndexOf(order, a) is var i and >= 0 ? i : int.MaxValue).ThenBy(a => a).ToList();
    }
}

/// <summary>Clusters the error groups on screen by text similarity (see <see cref="ErrorLensLLM.Core.Grouping.ErrorGroupClusterer"/>).</summary>
public static class ErrorGroupSimilarity
{
    private static readonly ErrorLensLLM.Core.Grouping.ErrorGroupClusterer Clusterer = new();

    public static ErrorLensLLM.Core.Grouping.ErrorGroupClustering Cluster(IEnumerable<ErrorLensLLM.Storage.PostgreSql.ErrorGroupView> groups) =>
        Clusterer.Cluster(groups
            .Select(g => new ErrorLensLLM.Core.Grouping.ErrorGroupText(g.Fingerprint, g.ExceptionType, g.NormalizedMessage, g.TopFrame))
            .ToList());
}
