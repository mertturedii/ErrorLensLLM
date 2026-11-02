namespace ErrorLensLLM.Ml.Evaluation;

/// <summary>Confusion counts and the precision / recall / F1 reported in log anomaly detection papers.</summary>
public sealed record DetectionMetrics(long TruePositives, long FalsePositives, long FalseNegatives, long TrueNegatives)
{
    public double Precision => Ratio(TruePositives, TruePositives + FalsePositives);
    public double Recall => Ratio(TruePositives, TruePositives + FalseNegatives);
    public double F1 => Precision + Recall == 0 ? 0 : 2 * Precision * Recall / (Precision + Recall);

    public static DetectionMetrics Compute(IReadOnlyList<bool> actual, IReadOnlyList<bool> predicted)
    {
        if (actual.Count != predicted.Count)
            throw new ArgumentException("Actual and predicted labels must have the same length.");

        long tp = 0, fp = 0, fn = 0, tn = 0;
        for (var i = 0; i < actual.Count; i++)
        {
            switch (actual[i], predicted[i])
            {
                case (true, true): tp++; break;
                case (false, true): fp++; break;
                case (true, false): fn++; break;
                default: tn++; break;
            }
        }

        return new DetectionMetrics(tp, fp, fn, tn);
    }

    private static double Ratio(long numerator, long denominator) => denominator == 0 ? 0 : (double)numerator / denominator;
}
