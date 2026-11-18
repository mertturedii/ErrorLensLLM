namespace ErrorLensLLM.Storage.PostgreSql;

/// <summary>A setting the system settings screen can change; its default is the value the code used to have built in.</summary>
public sealed record SettingDefinition(
    string Section, string Key, string Label, string Help, string Unit, double Default, double Min, double Max, bool Integer = true);

/// <summary>A group of settings on the settings screen.</summary>
public sealed record SettingSection(string Key, string Title, string Description);

/// <summary>
/// Every setting that used to be a constant in the collector. The collector reads the stored values (or these defaults)
/// every few seconds, so a change on the settings screen takes effect without a restart.
/// </summary>
public static class SettingsCatalog
{
    public const string Incidents = "incidents", Correlation = "correlation", Anomaly = "anomaly";

    public static readonly IReadOnlyList<SettingSection> Sections =
    [
        new(Incidents, "Olay ayarları", "Olayların ne zaman kapandığı ve yapay zekâ açıklamasının ne zaman istendiği."),
        new(Correlation, "Kök neden ağırlıkları ve pencereler", "Hataların aynı olaya bağlanması ve kök neden skorunun hesaplanması."),
        new(Anomaly, "Anomali tespiti", "DeepLog, Isolation Forest ve hacim kuralının eğitimi ve eşikleri.")
    ];

    public static readonly IReadOnlyList<SettingDefinition> All =
    [
        new(Incidents, "WindowMinutes", "İlişkilendirme penceresi", "Her analizde bu kadar geriye kadar olan hatalar olaylara bağlanır.", "dk", 60, 5, 1440),
        new(Incidents, "CloseAfterMinutes", "Olay kapanma süresi", "Bu süre boyunca yeni hata gelmeyen olay kapanır.", "dk", 15, 1, 1440),
        new(Incidents, "ExplainSettleSeconds", "Açıklama bekleme süresi", "Olay açıldıktan bu kadar sonra yapay zekâya gönderilir; ilk hata dalgası otursun diye.", "sn", 30, 0, 3600),
        new(Incidents, "ExplainRetryMinutes", "Başarısız açıklamayı yeniden deneme", "Açıklama alınamayan olay bu kadar sonra yeniden denenir.", "dk", 10, 1, 1440),
        new(Incidents, "ExplainLookbackMinutes", "Kendiliğinden açıklanan olayların yaşı", "Collector açıldığında bundan daha eski olaylar kendiliğinden açıklanmaz.", "dk", 60, 1, 10080),

        new(Correlation, "UpstreamWeight", "Upstream ağırlığı", "Ortak trace'lerde zincirin başında olma payının skordaki ağırlığı.", "", 0.5, 0, 1, false),
        new(Correlation, "DeploymentWeight", "Deploy ağırlığı", "Şüpheli yayının servisine ait olmanın skordaki ağırlığı.", "", 0.3, 0, 1, false),
        new(Correlation, "OnsetWeight", "Erken başlama ağırlığı", "Olay içinde erken başlamanın skordaki ağırlığı.", "", 0.2, 0, 1, false),
        new(Correlation, "DeploymentWindowMinutes", "Deploy penceresi", "İlk hatadan en fazla bu kadar önceki yayın şüpheli sayılır.", "dk", 5, 1, 120),
        new(Correlation, "DeploymentLeadSeconds", "Deploy öncesi tolerans", "Yayın kaydı ilk hatadan bu kadar sonra gelse de ilişkilendirilir.", "sn", 30, 0, 600),
        new(Correlation, "CoOnsetWindowSeconds", "Birlikte başlama penceresi", "Aynı yayından sonra bu kadar yakın başlayan hata grupları aynı olaya bağlanır.", "sn", 60, 1, 3600),
        new(Correlation, "EpisodeGapMinutes", "Yeni dönem aralığı", "Bu kadar sessizlikten sonra dönen hata yeni bir dönem, dolayısıyla yeni bir olay başlatabilir.", "dk", 15, 1, 1440),

        new(Anomaly, "TrainingMinutes", "Eğitim süresi", "Model, son bu kadar dakikanın hatasız kovalarıyla eğitilir.", "dk", 10, 2, 1440),
        new(Anomaly, "RetrainMinutes", "Yeniden eğitim aralığı", "Her servisin modeli bu sıklıkla yeniden eğitilir.", "dk", 30, 1, 1440),
        new(Anomaly, "MinTrainingBuckets", "En az eğitim kovası", "Bundan az hatasız kova varsa model eğitilmez (kova 10 sn).", "kova", 18, 4, 10000),
        new(Anomaly, "MinTrainingEvents", "En az eğitim logu", "Eğitim kovalarında toplam bundan az log varsa model eğitilmez.", "log", 200, 10, 1000000),
        new(Anomaly, "TargetEventRate", "DeepLog hedef yanlış alarm oranı", "top-g, ayrılmış normal verideki logların en fazla bu payını beklenmedik sayacak şekilde seçilir.", "oran", 0.01, 0.0001, 0.5, false),
        new(Anomaly, "MinBucketShare", "DeepLog en düşük eşik", "Eğitim ne kadar temiz olursa olsun, kova eşiği bunun altına inmez.", "oran", 0.05, 0, 1, false),
        new(Anomaly, "MinAnomalousEvents", "DeepLog en az beklenmeyen log", "Kovada bundan az beklenmeyen log varsa DeepLog alarm vermez.", "log", 2, 1, 1000),
        new(Anomaly, "VolumeSigmas", "Hacim kuralı: standart sapma", "Log sayısı ortalamadan bu kadar standart sapma uzaklaşırsa ...", "σ", 4, 1, 20, false),
        new(Anomaly, "MinVolumeChange", "Hacim kuralı: en az değişim", "... ve ortalamanın en az bu payı kadar uzaklaşırsa hacim alarmı verilir.", "oran", 0.5, 0, 10, false)
    ];

    public static SettingDefinition Find(string section, string key) =>
        All.First(d => d.Section == section && d.Key == key);
}

/// <summary>Stored values over the catalog defaults.</summary>
public sealed class SettingsSnapshot(IReadOnlyDictionary<(string Section, string Key), double> values)
{
    public static SettingsSnapshot Defaults { get; } = new(new Dictionary<(string, string), double>());

    public double this[string section, string key] =>
        values.TryGetValue((section, key), out var value) ? value : SettingsCatalog.Find(section, key).Default;

    public bool IsDefault(string section, string key) => !values.ContainsKey((section, key));

    public int Int(string section, string key) => (int)Math.Round(this[section, key]);
}
