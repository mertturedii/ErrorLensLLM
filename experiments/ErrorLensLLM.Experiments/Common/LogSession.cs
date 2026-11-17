namespace ErrorLensLLM.Experiments.Common;

/// <summary>
/// A unit that is labeled and scored as a whole: an HDFS block (all events that mention its block id) or a BGL window
/// (consecutive lines), with its template ids in log order.
/// </summary>
/// <param name="HasWarning">Contains a line whose level the dataset's rule baseline counts as a warning.</param>
public sealed record LogSession(string Id, bool IsAnomaly, bool HasWarning, int[] Events);

public sealed record LogTemplateRow(int Id, long Count, string Text);
