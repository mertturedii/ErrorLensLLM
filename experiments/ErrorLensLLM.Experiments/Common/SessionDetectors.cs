using ErrorLensLLM.Ml.Detection;

namespace ErrorLensLLM.Experiments.Common;

/// <summary>Baselines shared by the HDFS and BGL experiments; every method sees the same train / test sessions.</summary>
public static class SessionDetectors
{
    /// <summary>A session is anomalous when it contains a template seen in fewer than <paramref name="share"/> of training sessions.</summary>
    public static List<bool> RareTemplate(List<LogSession> train, List<LogSession> test, int templateCount, double share)
    {
        var sessionsWithTemplate = new int[templateCount];
        foreach (var session in train)
        {
            foreach (var id in session.Events.Distinct())
                sessionsWithTemplate[id]++;
        }

        var minimum = share * train.Count;
        return test.Select(s => s.Events.Any(id => sessionsWithTemplate[id] < minimum)).ToList();
    }

    /// <summary>Isolation Forest on event count vectors; the threshold flags <paramref name="contamination"/> of the training sessions.</summary>
    public static List<bool> IsolationForest(List<LogSession> train, List<LogSession> test, int templateCount, double contamination)
    {
        var forest = new IsolationForest(treeCount: 100, sampleSize: 256, seed: 42);
        forest.Fit(train.Select(s => CountVector(s, templateCount)).ToList());

        // Many sessions share the same event counts; score each distinct vector once
        var cache = new Dictionary<string, double>();
        double Score(LogSession s)
        {
            var vector = CountVector(s, templateCount);
            var key = string.Join(',', vector);
            if (!cache.TryGetValue(key, out var score))
                cache[key] = score = forest.Score(vector);
            return score;
        }

        var trainScores = train.Select(Score).OrderBy(x => x).ToArray();
        var threshold = trainScores[(int)((trainScores.Length - 1) * (1 - contamination))];
        return test.Select(s => Score(s) > threshold).ToList();
    }

    public static double[] CountVector(LogSession session, int templateCount)
    {
        var vector = new double[templateCount];
        foreach (var id in session.Events)
            vector[id]++;
        return vector;
    }
}
