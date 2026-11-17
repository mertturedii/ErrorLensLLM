using ErrorLensLLM.Ml.Detection;
using ErrorLensLLM.Ml.Streaming;

namespace ErrorLensLLM.Ml.Tests;

public class StreamAnomalyModelTests
{
    private static readonly StreamAnomalyOptions Options = new()
    {
        DeepLog = new DeepLogOptions { WindowSize = 4, Epochs = 20, BatchSize = 64, LearningRate = 0.01 }
    };

    // A module that logs "request" (10), "query" (11) and "response" (12) for every request, about 20 per bucket
    private static int[] NormalBucket(Random random) =>
        Enumerable.Range(0, 18 + random.Next(5)).SelectMany(_ => new[] { 10, 11, 12 }).ToArray();

    private static StreamAnomalyModel Train(out Random random)
    {
        random = new Random(7);
        var r = random;
        return StreamAnomalyModel.Train(Enumerable.Range(0, 30).Select(_ => NormalBucket(r)).ToList(), Options);
    }

    [Fact]
    public void Normal_bucket_is_not_an_anomaly()
    {
        using var model = Train(out var random);

        var score = model.Score(NormalBucket(random), [10, 11, 12]);

        Assert.False(score.IsAnomaly, $"share {score.DeepLogShare}, forest {score.ForestScore} > {model.ForestThreshold}");
        Assert.Empty(score.UnseenTemplates);
    }

    [Fact]
    public void Bucket_full_of_a_new_error_template_is_a_deeplog_anomaly()
    {
        using var model = Train(out var random);

        // After a faulty release the query fails: request, error (99), no response
        var bucket = Enumerable.Range(0, 20).SelectMany(_ => new[] { 10, 99 }).ToArray();
        var score = model.Score(bucket, [10, 11, 12]);

        Assert.True(score.DeepLogAnomaly);
        Assert.Equal([99], score.UnseenTemplates);
        Assert.True(score.DeepLogShare >= 0.5);
    }

    [Fact]
    public void Silent_module_is_a_volume_anomaly()
    {
        using var model = Train(out _);

        // A module that stopped logging has nothing for DeepLog to judge, and Isolation Forest cannot see below its range
        var score = model.Score([], [10, 11, 12]);

        Assert.False(score.DeepLogAnomaly);
        Assert.True(score.VolumeAnomaly);
    }
}
