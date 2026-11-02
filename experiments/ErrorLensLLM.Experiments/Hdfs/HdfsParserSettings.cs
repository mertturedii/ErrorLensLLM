using ErrorLensLLM.Ml.Parsing;

namespace ErrorLensLLM.Experiments.Hdfs;

/// <summary>How HDFS.log is turned into templates. Defaults reproduce the LogHub / logparser benchmark setup.</summary>
/// <param name="Similarity">Drain similarity threshold.</param>
/// <param name="Mode">How Drain measures similarity.</param>
/// <param name="MaskPaths">Also replace file paths with the wildcard (not part of the LogHub masks).</param>
public sealed record HdfsParserSettings(double Similarity = 0.5, DrainSimilarity Mode = DrainSimilarity.Original, bool MaskPaths = false)
{
    /// <summary>File name part of the parse cache; one cache per setting.</summary>
    public string CacheName => $"{Mode.ToString().ToLowerInvariant()}-st{Similarity}{(MaskPaths ? "-paths" : "")}";

    public override string ToString() => $"{Mode} ≥ {Similarity}{(MaskPaths ? " + path mask" : "")}";
}
