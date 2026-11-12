namespace ErrorLensLLM.Collector;

public sealed class CollectorOptions
{
    public const string Section = "Collector";

    /// <summary>When set, senders must pass it in the <c>X-ErrorLens-Key</c> header.</summary>
    public string? ApiKey { get; set; }

    public int MaxEventsPerBatch { get; set; } = 1000;

    /// <summary>Field / property names whose values are always masked.</summary>
    public string[] SensitiveFields { get; set; } = [];

    /// <summary>Drain threshold for plain-text messages (constant-token similarity).</summary>
    public double DrainSimilarity { get; set; } = 0.8;
}
