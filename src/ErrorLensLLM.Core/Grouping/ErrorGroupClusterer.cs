using System.Text.RegularExpressions;

namespace ErrorLensLLM.Core.Grouping;

/// <summary>What the clusterer needs to know about an error group.</summary>
public sealed record ErrorGroupText(string Fingerprint, string ExceptionType, string NormalizedMessage, string? TopFrame);

/// <summary>A set of error groups whose texts are similar, with the terms they share most.</summary>
public sealed record ErrorCluster(int Id, IReadOnlyList<string> Fingerprints, IReadOnlyList<string> Terms);

public sealed record SimilarGroup(string Fingerprint, double Similarity);

/// <summary>
/// Groups similar error groups. A fingerprint separates errors that differ in type, message template or top frame, so
/// one failure often shows up as several groups (the same HTTP error from two callers, the same timeout at two call
/// sites). Each group becomes a TF-IDF vector of its exception type, message and frame terms (a sparse embedding; no
/// external language model is involved, so nothing leaves the process), and average-linkage agglomerative clustering
/// joins groups whose cosine similarity is at least <see cref="Threshold"/>.
/// </summary>
public sealed partial class ErrorGroupClusterer(double threshold = 0.5)
{
    public double Threshold { get; } = threshold;

    public ErrorGroupClustering Cluster(IReadOnlyList<ErrorGroupText> groups)
    {
        var vectors = Vectorize(groups);
        var n = groups.Count;
        var similarity = new double[n, n];
        for (var i = 0; i < n; i++)
        {
            similarity[i, i] = 1;
            for (var j = i + 1; j < n; j++)
                similarity[i, j] = similarity[j, i] = Cosine(vectors[i], vectors[j]);
        }

        // Average linkage: two clusters merge while the mean similarity between their members reaches the threshold
        var clusters = Enumerable.Range(0, n).Select(i => new List<int> { i }).ToList();
        while (true)
        {
            var (bestA, bestB, best) = (-1, -1, double.MinValue);
            for (var a = 0; a < clusters.Count; a++)
            {
                for (var b = a + 1; b < clusters.Count; b++)
                {
                    var mean = clusters[a].Sum(i => clusters[b].Sum(j => similarity[i, j])) / (clusters[a].Count * clusters[b].Count);
                    if (mean > best)
                        (bestA, bestB, best) = (a, b, mean);
                }
            }

            if (bestA < 0 || best < Threshold)
                break;
            clusters[bestA].AddRange(clusters[bestB]);
            clusters.RemoveAt(bestB);
        }

        var result = clusters
            .OrderByDescending(c => c.Count).ThenBy(c => c.Min())
            .Select((members, id) => new ErrorCluster(id, members.Select(i => groups[i].Fingerprint).ToList(), TopTerms(members, vectors)))
            .ToList();

        return new ErrorGroupClustering(groups, result, similarity);
    }

    /// <summary>Terms of the exception type (namespace parts and name), the message and the top frame, each kind prefixed.</summary>
    public static IEnumerable<string> Terms(ErrorGroupText group)
    {
        foreach (var part in group.ExceptionType.Split('.', StringSplitOptions.RemoveEmptyEntries))
            yield return "type:" + part;

        foreach (Match word in Word().Matches(group.NormalizedMessage.ToLowerInvariant()))
            yield return "msg:" + word.Value;

        if (group.TopFrame is { } frame)
        {
            foreach (var part in frame.Split(['.', ':', ' ', '(', ')', '`', '<', '>', '+'], StringSplitOptions.RemoveEmptyEntries).Where(p => p.Length > 1))
                yield return "frame:" + part;
        }
    }

    private static List<Dictionary<string, double>> Vectorize(IReadOnlyList<ErrorGroupText> groups)
    {
        var counts = groups.Select(g => Terms(g).GroupBy(t => t).ToDictionary(t => t.Key, t => (double)t.Count())).ToList();
        var documentFrequency = counts.SelectMany(c => c.Keys).GroupBy(t => t).ToDictionary(t => t.Key, t => t.Count());

        return counts.Select(c =>
        {
            // Smoothed idf, as in scikit-learn: a term in every group still counts a little
            var vector = c.ToDictionary(t => t.Key, t => t.Value * (Math.Log((groups.Count + 1.0) / (documentFrequency[t.Key] + 1)) + 1));
            var norm = Math.Sqrt(vector.Values.Sum(v => v * v));
            return vector.ToDictionary(t => t.Key, t => norm == 0 ? 0 : t.Value / norm);
        }).ToList();
    }

    private static double Cosine(Dictionary<string, double> a, Dictionary<string, double> b)
    {
        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        return small.Sum(t => large.TryGetValue(t.Key, out var v) ? t.Value * v : 0);
    }

    private static IReadOnlyList<string> TopTerms(List<int> members, List<Dictionary<string, double>> vectors) =>
        members.Count < 2
            ? []
            : members.SelectMany(i => vectors[i])
                // Only terms every member has describe what they share
                .GroupBy(t => t.Key).Where(t => t.Count() == members.Count)
                .OrderByDescending(t => t.Sum(x => x.Value)).Take(4)
                .Select(t => t.Key[(t.Key.IndexOf(':') + 1)..])
                .ToList();

    // Words of two or more letters; placeholders like <num> and <guid> keep their name
    [GeneratedRegex(@"<?[\p{L}][\p{L}\d_]+>?")]
    private static partial Regex Word();
}

/// <summary>Clusters plus pairwise similarities, to list a group's nearest neighbours.</summary>
public sealed class ErrorGroupClustering
{
    private readonly IReadOnlyList<ErrorGroupText> _groups;
    private readonly double[,] _similarity;
    private readonly Dictionary<string, int> _index;
    private readonly Dictionary<string, ErrorCluster> _clusterOf;

    internal ErrorGroupClustering(IReadOnlyList<ErrorGroupText> groups, IReadOnlyList<ErrorCluster> clusters, double[,] similarity)
    {
        _groups = groups;
        _similarity = similarity;
        Clusters = clusters;
        _index = groups.Select((g, i) => (g.Fingerprint, i)).ToDictionary(p => p.Fingerprint, p => p.i);
        _clusterOf = clusters.SelectMany(c => c.Fingerprints.Select(f => (f, c))).ToDictionary(p => p.f, p => p.c);
    }

    public IReadOnlyList<ErrorCluster> Clusters { get; }

    /// <summary>The cluster of a group, or null when it stands alone.</summary>
    public ErrorCluster? ClusterOf(string fingerprint) =>
        _clusterOf.TryGetValue(fingerprint, out var cluster) && cluster.Fingerprints.Count > 1 ? cluster : null;

    /// <summary>The most similar other groups, most similar first.</summary>
    public IReadOnlyList<SimilarGroup> Similar(string fingerprint, int count = 5, double minimum = 0.2)
    {
        if (!_index.TryGetValue(fingerprint, out var i))
            return [];

        return Enumerable.Range(0, _groups.Count)
            .Where(j => j != i && _similarity[i, j] >= minimum)
            .OrderByDescending(j => _similarity[i, j])
            .Take(count)
            .Select(j => new SimilarGroup(_groups[j].Fingerprint, _similarity[i, j]))
            .ToList();
    }
}
