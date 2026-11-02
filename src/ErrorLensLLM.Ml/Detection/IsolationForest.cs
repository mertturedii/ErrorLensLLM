namespace ErrorLensLLM.Ml.Detection;

/// <summary>
/// Unsupervised anomaly detector (Liu, Ting &amp; Zhou, "Isolation Forest", ICDM 2008). Random axis-parallel
/// splits isolate rare points in fewer steps, so a short average path length means a high anomaly score.
/// </summary>
public sealed class IsolationForest
{
    private const double EulerGamma = 0.5772156649;

    private readonly int _treeCount;
    private readonly int _sampleSize;
    private readonly Random _random;
    private Node[] _trees = [];
    private double _normalizer;

    public IsolationForest(int treeCount = 100, int sampleSize = 256, int seed = 42)
    {
        _treeCount = treeCount;
        _sampleSize = sampleSize;
        _random = new Random(seed);
    }

    public void Fit(IReadOnlyList<double[]> samples)
    {
        if (samples.Count == 0)
            throw new ArgumentException("At least one sample is required.", nameof(samples));

        var sampleSize = Math.Min(_sampleSize, samples.Count);
        var heightLimit = (int)Math.Ceiling(Math.Log2(Math.Max(sampleSize, 2)));

        _trees = new Node[_treeCount];
        for (var t = 0; t < _treeCount; t++)
        {
            var subsample = Enumerable.Range(0, samples.Count)
                .OrderBy(_ => _random.Next())
                .Take(sampleSize)
                .Select(i => samples[i])
                .ToArray();
            _trees[t] = Build(subsample, depth: 0, heightLimit);
        }

        _normalizer = AveragePathLength(sampleSize);
    }

    /// <summary>Anomaly score in (0, 1]; values close to 1 are anomalies, values well below 0.5 are normal.</summary>
    public double Score(double[] sample)
    {
        if (_trees.Length == 0)
            throw new InvalidOperationException("Call Fit before Score.");

        var meanPath = _trees.Average(tree => PathLength(tree, sample, depth: 0));
        return Math.Pow(2, -meanPath / _normalizer);
    }

    private Node Build(double[][] samples, int depth, int heightLimit)
    {
        if (depth >= heightLimit || samples.Length <= 1)
            return Node.Leaf(samples.Length);

        // Only features that vary inside this node can split it
        var features = Enumerable.Range(0, samples[0].Length)
            .Where(f => samples.Any(s => s[f] != samples[0][f]))
            .ToArray();
        if (features.Length == 0)
            return Node.Leaf(samples.Length);

        var feature = features[_random.Next(features.Length)];
        var min = samples.Min(s => s[feature]);
        var max = samples.Max(s => s[feature]);
        var split = min + _random.NextDouble() * (max - min);

        return new Node(
            feature,
            split,
            Build(samples.Where(s => s[feature] < split).ToArray(), depth + 1, heightLimit),
            Build(samples.Where(s => s[feature] >= split).ToArray(), depth + 1, heightLimit),
            samples.Length);
    }

    private static double PathLength(Node node, double[] sample, int depth)
    {
        while (node.Left is not null)
        {
            node = sample[node.Feature] < node.Split ? node.Left : node.Right!;
            depth++;
        }

        // An unsplit leaf stands for a subtree of this size; add its expected depth
        return depth + AveragePathLength(node.Size);
    }

    /// <summary>Average path length of an unsuccessful BST search over n points, c(n) in the paper.</summary>
    private static double AveragePathLength(int n) => n switch
    {
        <= 1 => 0,
        2 => 1,
        _ => 2 * (Math.Log(n - 1) + EulerGamma) - 2.0 * (n - 1) / n
    };

    private sealed record Node(int Feature, double Split, Node? Left, Node? Right, int Size)
    {
        public static Node Leaf(int size) => new(-1, 0, null, null, size);
    }
}
