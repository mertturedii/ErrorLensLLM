using ErrorLensLLM.Ml.Detection;

namespace ErrorLensLLM.Ml.Tests;

public class IsolationForestTests
{
    [Fact]
    public void Outlier_scores_higher_than_inliers()
    {
        var random = new Random(1);
        var samples = Enumerable.Range(0, 500)
            .Select(_ => new[] { random.NextDouble(), random.NextDouble() })
            .ToList();

        var forest = new IsolationForest(seed: 7);
        forest.Fit(samples);

        var inlier = forest.Score([0.5, 0.5]);
        var outlier = forest.Score([8.0, -6.0]);

        Assert.True(outlier > inlier, $"outlier {outlier} <= inlier {inlier}");
        Assert.True(outlier > 0.6, $"outlier score {outlier}");
    }

    [Fact]
    public void Same_seed_gives_same_scores()
    {
        var samples = Enumerable.Range(0, 100).Select(i => new double[] { i % 7, i % 3 }).ToList();

        var a = new IsolationForest(seed: 3);
        var b = new IsolationForest(seed: 3);
        a.Fit(samples);
        b.Fit(samples);

        Assert.Equal(a.Score([2, 1]), b.Score([2, 1]));
    }

    [Fact]
    public void Constant_data_does_not_throw()
    {
        var forest = new IsolationForest();
        forest.Fit(Enumerable.Repeat(new double[] { 1, 1 }, 50).ToList());

        Assert.InRange(forest.Score([1, 1]), 0, 1);
    }
}
