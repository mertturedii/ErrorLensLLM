using ErrorLensLLM.Ml.Evaluation;

namespace ErrorLensLLM.Ml.Tests;

public class DetectionMetricsTests
{
    [Fact]
    public void Precision_recall_and_f1_are_computed()
    {
        bool[] actual = [true, true, true, false, false];
        bool[] predicted = [true, true, false, true, false];

        var m = DetectionMetrics.Compute(actual, predicted);

        Assert.Equal((2, 1, 1, 1), (m.TruePositives, m.FalsePositives, m.FalseNegatives, m.TrueNegatives));
        Assert.Equal(2.0 / 3, m.Precision, 6);
        Assert.Equal(2.0 / 3, m.Recall, 6);
        Assert.Equal(2.0 / 3, m.F1, 6);
    }

    [Fact]
    public void No_predictions_gives_zero_instead_of_nan()
    {
        var m = DetectionMetrics.Compute([true, false], [false, false]);

        Assert.Equal(0, m.Precision);
        Assert.Equal(0, m.F1);
    }
}
