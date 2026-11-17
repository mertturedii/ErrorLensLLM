using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using ErrorLensLLM.Core.Incidents;

namespace ErrorLensLLM.AI.Claude;

/// <summary>Everything the model sees about an incident. All texts come from the collector, which masked them on arrival.</summary>
public sealed record IncidentContext(
    string Title,
    ErrorCategory Category,
    DateTimeOffset StartedAt,
    DateTimeOffset LastSeen,
    long Occurrences,
    IReadOnlyList<string> Applications,
    string? SuspectedDeployment,
    IReadOnlyList<IncidentMember> Members,
    IReadOnlyList<string> RequestChain,
    string? RootStackTrace,
    IReadOnlyList<string> AnomalyNotes);

/// <summary>The model's reading of an incident, in Turkish.</summary>
public sealed record IncidentExplanation(
    string Summary,
    string RootCause,
    string Impact,
    IReadOnlyList<string> NextSteps,
    string Confidence,
    string? Disagreement);

public sealed record ExplanationResult(IncidentExplanation Explanation, string Model, long InputTokens, long OutputTokens);

public sealed class ClaudeOptions
{
    public const string Section = "Claude";

    /// <summary>Taken from <c>ANTHROPIC_API_KEY</c> when not set here; the layer is off without a key.</summary>
    public string? ApiKey { get; set; }

    public string Model { get; set; } = "claude-opus-5-5";

    /// <summary>low, medium, high, xhigh or max.</summary>
    public string Effort { get; set; } = "medium";

    public int MaxTokens { get; set; } = 8000;
}

/// <summary>
/// Explanation layer on top of the correlator: the incident, its ranked root cause candidates with their evidence, the
/// request chain and the stack trace go to Claude, which writes a short explanation and next steps for a developer.
/// The correlator's ranking stays the decision; the model explains it and may disagree in a separate field, so that a
/// developer sees both and the feedback form judges the ranking, not the prose.
/// </summary>
public sealed class IncidentExplainer(AnthropicClient client, ClaudeOptions options)
{
    private const string SystemPrompt = """
        Sen bir yazılım ekibinin olay (incident) analizi asistanısın. Bir log izleme sistemi, bir dağıtımdan (deploy) sonra
        farklı modüllerde oluşan hataları tek bir olayda topladı ve kök neden adaylarını kural tabanlı bir skorla sıraladı.
        Sana olayın verisini veriyorum: adaylar, her adayın skoru ve kanıtları, bir isteğin modüller arasında nasıl yayıldığı,
        kök neden adayının stack trace'i ve varsa canlı anomali tespitinin notları.

        Görevin bu olayı nöbetteki bir geliştiriciye açıklamak:
        - Ne olduğunu iki üç cümlede özetle.
        - Kök nedeni açıkla: hangi modülde, hangi değişiklik ya da durum yüzünden, diğer hatalar neden onun sonucu.
          Yalnızca verilen veriye dayan; veride olmayan bir dosya, tablo ya da kişi uydurma. Emin olmadığın yeri söyle.
        - Etkiyi yaz: hangi modüller ve kullanıcı işlemleri etkileniyor.
        - Somut, sıralı sonraki adımlar öner (geri alma, migration, yapılandırma, kod düzeltmesi gibi); en fazla beş adım.
        - Güveni "yüksek", "orta" ya da "düşük" olarak ver.
        - Sistemin sıraladığı birinci adayın kök neden olmadığını düşünüyorsan "disagreement" alanında nedenini yaz;
          katılıyorsan bu alanı boş bırak.
        Kısa ve düz Türkçe yaz; teknik terimleri (exception adları, SQL, HTTP) çevirme.
        """;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Model => options.Model;

    public async Task<ExplanationResult> ExplainAsync(IncidentContext incident, CancellationToken ct = default)
    {
        var response = await client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = options.Model,
            MaxTokens = options.MaxTokens,
            // A policy decline is served by a fallback model within the same call instead of ending the request
            Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
            Fallbacks = new Default(),
            OutputConfig = new BetaOutputConfig
            {
                Effort = Enum.Parse<Effort>(options.Effort, ignoreCase: true),
                Format = new BetaJsonOutputFormat { Schema = Schema() }
            },
            System = SystemPrompt,
            Messages = [new BetaUserMessageParam { Content = Describe(incident) }]
        }, ct);

        if (response.StopReason == "refusal")
            throw new InvalidOperationException($"The model declined to explain the incident ({response.StopDetails?.Category}).");
        if (response.StopReason == "max_tokens")
            throw new InvalidOperationException("The explanation was cut off at the token limit.");

        var text = string.Concat(response.Content.Select(b => b.Value).OfType<BetaTextBlock>().Select(t => t.Text));
        var explanation = JsonSerializer.Deserialize<IncidentExplanation>(text, Json)
                          ?? throw new InvalidOperationException("The model returned no explanation.");

        return new ExplanationResult(explanation, response.Model, response.Usage.InputTokens, response.Usage.OutputTokens);
    }

    /// <summary>The user message: the incident as plain, labeled text.</summary>
    public static string Describe(IncidentContext incident)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Olay: {incident.Title}");
        sb.AppendLine($"Kategori (kural tabanlı): {incident.Category}");
        sb.AppendLine($"Başlangıç: {incident.StartedAt:u}, son hata: {incident.LastSeen:u}, toplam {incident.Occurrences} hata");
        sb.AppendLine($"Modüller: {string.Join(", ", incident.Applications)}");
        sb.AppendLine($"Şüpheli deploy: {incident.SuspectedDeployment ?? "yok (olaydan önce sürüm değiştiren bir yayın bulunamadı)"}");
        sb.AppendLine();

        sb.AppendLine("Kök neden adayları (skora göre sıralı; skor = 0,5 upstream + 0,3 deploy + 0,2 erken başlama):");
        for (var i = 0; i < incident.Members.Count; i++)
        {
            var m = incident.Members[i];
            sb.AppendLine($"{i + 1}. [{m.Application}] {m.ExceptionType}: {m.NormalizedMessage}");
            sb.AppendLine($"   kod konumu: {m.TopFrame ?? "-"} · kategori: {m.Category} · {m.Occurrences} kez · skor {m.RootCauseScore:F2}");
            sb.AppendLine($"   ilk: {m.FirstSeen:u}, son: {m.LastSeen:u}");
            foreach (var evidence in m.Evidence)
                sb.AppendLine($"   kanıt: {evidence}");
        }

        if (incident.RequestChain.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Örnek bir isteğin logları (aynı trace, zaman sırasıyla):");
            foreach (var line in incident.RequestChain)
                sb.AppendLine($"- {line}");
        }

        if (incident.RootStackTrace is { } stack)
        {
            sb.AppendLine();
            sb.AppendLine("Birinci adayın örnek stack trace'i:");
            sb.AppendLine(stack);
        }

        if (incident.AnomalyNotes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Canlı anomali tespiti (DeepLog / Isolation Forest / hacim, 10 sn'lik kovalar):");
            foreach (var note in incident.AnomalyNotes)
                sb.AppendLine($"- {note}");
        }

        return sb.ToString();
    }

    private static Dictionary<string, JsonElement> Schema() => new()
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["properties"] = JsonSerializer.SerializeToElement(new
        {
            summary = new { type = "string", description = "Ne oldu, iki üç cümle" },
            rootCause = new { type = "string", description = "Kök nedenin açıklaması" },
            impact = new { type = "string", description = "Etkilenen modüller ve işlemler" },
            nextSteps = new { type = "array", items = new { type = "string" }, description = "En fazla beş somut adım" },
            confidence = new { type = "string", @enum = new[] { "yüksek", "orta", "düşük" } },
            disagreement = new { type = new[] { "string", "null" }, description = "Birinci adaya katılmıyorsan nedeni, yoksa null" }
        }),
        ["required"] = JsonSerializer.SerializeToElement(new[] { "summary", "rootCause", "impact", "nextSteps", "confidence", "disagreement" }),
        ["additionalProperties"] = JsonSerializer.SerializeToElement(false)
    };
}
