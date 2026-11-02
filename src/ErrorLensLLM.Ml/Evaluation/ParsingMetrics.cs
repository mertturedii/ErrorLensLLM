namespace ErrorLensLLM.Ml.Evaluation;

/// <summary>Log parsing accuracy as defined by Zhu et al., "Tools and Benchmarks for Automated Log Parsing", ICSE-SEIP 2019.</summary>
public static class ParsingMetrics
{
    /// <summary>
    /// Share of messages whose predicted template groups exactly the same messages as their ground-truth event.
    /// A template that merges two events, or an event split over two templates, makes all of its messages wrong.
    /// </summary>
    public static GroupingResult<TPredicted, TTruth> GroupingAccuracy<TPredicted, TTruth>(IEnumerable<(TPredicted Predicted, TTruth Truth)> messages)
        where TPredicted : notnull
        where TTruth : notnull
    {
        var pairs = new Dictionary<(TPredicted, TTruth), long>();
        foreach (var message in messages)
            pairs[message] = pairs.GetValueOrDefault(message) + 1;

        var truthsPerTemplate = pairs.Keys.GroupBy(p => p.Item1).ToDictionary(g => g.Key, g => g.Select(p => p.Item2).ToList());
        var templatesPerTruth = pairs.Keys.GroupBy(p => p.Item2).ToDictionary(g => g.Key, g => g.Select(p => p.Item1).ToList());

        var total = pairs.Values.Sum();
        var correct = pairs
            .Where(p => truthsPerTemplate[p.Key.Item1].Count == 1 && templatesPerTruth[p.Key.Item2].Count == 1)
            .Sum(p => p.Value);

        return new GroupingResult<TPredicted, TTruth>(
            total == 0 ? 0 : (double)correct / total,
            total,
            truthsPerTemplate.Where(p => p.Value.Count > 1).ToDictionary(p => p.Key, p => (IReadOnlyList<TTruth>)p.Value),
            templatesPerTruth.Where(p => p.Value.Count > 1).ToDictionary(p => p.Key, p => (IReadOnlyList<TPredicted>)p.Value));
    }
}

/// <param name="Accuracy">Grouping accuracy in [0, 1].</param>
/// <param name="Messages">Number of messages compared.</param>
/// <param name="Merged">Templates that mix several ground-truth events.</param>
/// <param name="Split">Ground-truth events spread over several templates.</param>
public sealed record GroupingResult<TPredicted, TTruth>(
    double Accuracy,
    long Messages,
    IReadOnlyDictionary<TPredicted, IReadOnlyList<TTruth>> Merged,
    IReadOnlyDictionary<TTruth, IReadOnlyList<TPredicted>> Split)
    where TPredicted : notnull
    where TTruth : notnull;
