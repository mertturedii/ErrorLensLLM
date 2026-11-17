using ErrorLensLLM.Ml.Detection;

namespace ErrorLensLLM.Ml.Streaming;

/// <summary>Settings of <see cref="StreamAnomalyModel"/>.</summary>
public sealed record StreamAnomalyOptions
{
    /// <summary>Last part of the training buckets, held out to choose top-g and the DeepLog bucket threshold.</summary>
    public double HoldoutShare { get; init; } = 0.25;

    /// <summary>Top-g is the smallest g that flags at most this share of the held-out (normal) events.</summary>
    public double TargetEventRate { get; init; } = 0.01;

    public int MaxTopG { get; init; } = 10;

    /// <summary>A bucket is never a DeepLog anomaly below this share of anomalous events, however clean training was.</summary>
    public double MinBucketShare { get; init; } = 0.05;

    /// <summary>... nor with fewer anomalous events than this (one odd line is not an anomaly).</summary>
    public int MinAnomalousEvents { get; init; } = 2;

    /// <summary>A bucket's event count is a volume anomaly this many standard deviations from the training mean ...</summary>
    public double VolumeSigmas { get; init; } = 4;

    /// <summary>... and at least this share of the mean away from it, so a very steady training stream does not alarm on noise.</summary>
    public double MinVolumeChange { get; init; } = 0.5;

    public DeepLogOptions DeepLog { get; init; } = new() { Epochs = 8, BatchSize = 512, LearningRate = 0.005 };
}

/// <summary>The score of one time bucket of one module's log stream.</summary>
/// <param name="Events">Log events in the bucket.</param>
/// <param name="AnomalousEvents">Events DeepLog did not expect (rank ≥ top-g).</param>
/// <param name="DeepLogShare">Their share of the bucket's events.</param>
/// <param name="DeepLogAnomaly">The share is above the DeepLog threshold.</param>
/// <param name="ForestScore">Isolation Forest score of the bucket's template counts.</param>
/// <param name="ForestAnomaly">The forest score is above its threshold.</param>
/// <param name="VolumeAnomaly">The event count is far outside the training range (see <see cref="StreamAnomalyOptions.VolumeSigmas"/>).</param>
/// <param name="UnseenTemplates">Templates in the bucket that never occurred in training.</param>
public sealed record BucketScore(
    int Events, int AnomalousEvents, double DeepLogShare, bool DeepLogAnomaly, double ForestScore, bool ForestAnomaly,
    bool VolumeAnomaly, IReadOnlyList<int> UnseenTemplates)
{
    public bool IsAnomaly => DeepLogAnomaly || ForestAnomaly || VolumeAnomaly;
}

/// <summary>
/// Live anomaly detection for one module. Its log has no session id that spans more than a line or two (a demo request
/// logs one line), so, as on BGL, the stream is cut into fixed time buckets:
/// <list type="bullet">
/// <item>DeepLog learns which template follows the previous ones in the module's stream and scores a bucket by the
/// share of its events outside the top-g predictions (unseen templates always are);</item>
/// <item>Isolation Forest scores the bucket's template count vector, which catches an unusual mix of ordinary lines;</item>
/// <item>a volume rule flags an event count far from the training mean. Isolation Forest cannot: its splits lie inside the
/// training range, so a silent module (an all-zero vector) is isolated no faster than the quietest training bucket.</item>
/// </list>
/// Thresholds come from the training buckets, assumed normal: top-g and the DeepLog threshold from a held-out part,
/// the forest threshold from the highest training score.
/// </summary>
public sealed class StreamAnomalyModel : IDisposable
{
    private readonly DeepLog _deepLog;
    private readonly IsolationForest _forest;
    private readonly Dictionary<int, int> _vocabulary;
    private StreamAnomalyOptions _options = new();

    private StreamAnomalyModel(DeepLog deepLog, IsolationForest forest, Dictionary<int, int> vocabulary)
    {
        _deepLog = deepLog;
        _forest = forest;
        _vocabulary = vocabulary;
    }

    public int TopG { get; private init; }
    public double DeepLogThreshold { get; private init; }
    public double ForestThreshold { get; private init; }

    /// <summary>Share of held-out events flagged at the chosen top-g.</summary>
    public double HoldoutEventRate { get; private init; }

    public int Templates => _vocabulary.Count;

    public double MeanEvents { get; private init; }
    public double StdEvents { get; private init; }

    /// <summary>The last events of a stream that serve as DeepLog's context for the next bucket.</summary>
    public int ContextSize => _options.DeepLog.WindowSize;

    /// <summary>Trains on consecutive, normal buckets (template ids in log order; empty buckets are allowed).</summary>
    public static StreamAnomalyModel Train(IReadOnlyList<int[]> buckets, StreamAnomalyOptions? options = null)
    {
        options ??= new StreamAnomalyOptions();
        if (buckets.Count < 4 || buckets.Sum(b => b.Length) == 0)
            throw new ArgumentException("At least four buckets with events are required.", nameof(buckets));

        var holdoutCount = Math.Max(1, (int)(buckets.Count * options.HoldoutShare));
        var fit = buckets.Take(buckets.Count - holdoutCount).ToList();
        var holdout = buckets.Skip(buckets.Count - holdoutCount).ToList();

        var vocabulary = fit.SelectMany(b => b).Distinct().Order()
            .Select((id, index) => (id, index))
            .ToDictionary(p => p.id, p => p.index);
        if (vocabulary.Count == 0)
            throw new ArgumentException("The training buckets before the held-out part have no events.", nameof(buckets));

        var deepLog = new DeepLog(vocabulary.Count, options.DeepLog);
        var fitStream = fit.SelectMany(b => b).Select(id => vocabulary[id]).ToArray();
        deepLog.Fit([fitStream]);

        // Hold-out buckets are scored as a continuation of the training stream, like live buckets are
        var holdoutRanks = BucketRanks(deepLog, vocabulary, holdout, fitStream.TakeLast(options.DeepLog.WindowSize).ToArray(), options.DeepLog.WindowSize);
        var holdoutEvents = holdoutRanks.Sum(r => r.Length);
        var topG = Enumerable.Range(1, options.MaxTopG)
            .FirstOrDefault(g => holdoutEvents == 0 || holdoutRanks.Sum(r => r.Count(x => x >= g)) <= options.TargetEventRate * holdoutEvents,
                options.MaxTopG);

        var holdoutShares = holdoutRanks.Select(r => r.Length == 0 ? 0 : (double)r.Count(x => x >= topG) / r.Length).ToList();

        // The forest sees every training bucket; its threshold is the highest score among them
        var forest = new IsolationForest(treeCount: 100, sampleSize: 256, seed: 42);
        var vectors = buckets.Select(b => CountVector(vocabulary, b)).ToList();
        forest.Fit(vectors);

        return new StreamAnomalyModel(deepLog, forest, vocabulary)
        {
            _options = options,
            TopG = topG,
            DeepLogThreshold = Math.Max(options.MinBucketShare, holdoutShares.DefaultIfEmpty(0).Max()),
            ForestThreshold = vectors.Max(forest.Score),
            MeanEvents = buckets.Average(b => b.Length),
            StdEvents = Math.Sqrt(buckets.Average(b => Math.Pow(b.Length - buckets.Average(x => x.Length), 2))),
            HoldoutEventRate = holdoutEvents == 0 ? 0 : (double)holdoutRanks.Sum(r => r.Count(x => x >= topG)) / holdoutEvents
        };
    }

    /// <param name="bucket">Template ids of the bucket in log order.</param>
    /// <param name="context">Template ids just before the bucket (at most <see cref="ContextSize"/> are used).</param>
    public BucketScore Score(int[] bucket, IReadOnlyList<int> context)
    {
        var mappedContext = context.TakeLast(ContextSize).Select(id => _vocabulary.GetValueOrDefault(id, -1)).ToArray();
        var ranks = BucketRanks(_deepLog, _vocabulary, [bucket], mappedContext, ContextSize)[0];
        var anomalous = ranks.Count(r => r >= TopG);
        var share = bucket.Length == 0 ? 0 : (double)anomalous / bucket.Length;
        var forestScore = _forest.Score(CountVector(_vocabulary, bucket));
        var unseen = bucket.Where(id => !_vocabulary.ContainsKey(id)).Distinct().ToList();

        return new BucketScore(
            bucket.Length, anomalous, share,
            DeepLogAnomaly: anomalous >= _options.MinAnomalousEvents && share > DeepLogThreshold,
            forestScore,
            ForestAnomaly: forestScore > ForestThreshold,
            VolumeAnomaly: Math.Abs(bucket.Length - MeanEvents) > Math.Max(_options.VolumeSigmas * StdEvents, _options.MinVolumeChange * MeanEvents),
            unseen);
    }

    public void Dispose() => _deepLog.Dispose();

    /// <summary>Ranks of each bucket's events; every bucket continues the stream of the ones before it.</summary>
    private static int[][] BucketRanks(
        DeepLog model, Dictionary<int, int> vocabulary, IReadOnlyList<int[]> buckets, int[] context, int windowSize)
    {
        var sequences = new List<int[]>();
        var history = context.ToList();
        foreach (var bucket in buckets)
        {
            var mapped = bucket.Select(id => vocabulary.GetValueOrDefault(id, -1)).ToArray();
            sequences.Add([.. history, .. mapped]);
            history = history.Concat(mapped).TakeLast(windowSize).ToList();
        }

        var ranks = model.Ranks(sequences);
        return ranks.Select((r, i) => r.Skip(sequences[i].Length - buckets[i].Length).ToArray()).ToArray();
    }

    /// <summary>Count of each known template; unseen templates are left to DeepLog, which always flags them.</summary>
    private static double[] CountVector(Dictionary<int, int> vocabulary, int[] bucket)
    {
        var vector = new double[vocabulary.Count];
        foreach (var id in bucket)
        {
            if (vocabulary.TryGetValue(id, out var index))
                vector[index]++;
        }

        return vector;
    }
}
