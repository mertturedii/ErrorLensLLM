using System.Diagnostics;
using System.Globalization;
using System.Text;
using ErrorLensLLM.Ml.Detection;
using ErrorLensLLM.Ml.Evaluation;

namespace ErrorLensLLM.Experiments.Hdfs;

public sealed record HdfsExperimentOptions
{
    public required string DataDirectory { get; init; }
    public required string ReportPath { get; init; }

    /// <summary>Sessions are split in log order; the first part is used for training (loglizer default 0.5).</summary>
    public double TrainRatio { get; init; } = 0.5;

    /// <summary>How the log is parsed into templates.</summary>
    public HdfsParserSettings Parser { get; init; } = new();

    /// <summary>Templates seen in fewer training sessions than this share are "rare" for the rule baseline.</summary>
    public double RareTemplateShare { get; init; } = 0.001;

    /// <summary>Expected anomaly share used to set the Isolation Forest threshold (loglizer default 0.03).</summary>
    public double Contamination { get; init; } = 0.03;

    /// <summary>Normal training sessions for DeepLog (the paper used 4,855 HDFS sessions).</summary>
    public int DeepLogSessions { get; init; } = 5000;

    /// <summary>A next event outside the top-g predictions is an anomaly (paper: g = 9).</summary>
    public int DeepLogTopG { get; init; } = 9;

    public int DeepLogEpochs { get; init; } = 10;
    public bool SkipDeepLog { get; init; }
}

/// <summary>Compares rule baselines, Isolation Forest and DeepLog on the same chronological HDFS split.</summary>
public static class HdfsExperiment
{
    public static void Run(HdfsExperimentOptions options)
    {
        Console.WriteLine("Loading HDFS...");
        var (sessions, templates) = HdfsDataset.Load(options.DataDirectory, options.Parser);

        var trainCount = (int)(sessions.Count * options.TrainRatio);
        var train = sessions.Take(trainCount).ToList();
        var test = sessions.Skip(trainCount).ToList();
        var actual = test.Select(s => s.IsAnomaly).ToList();

        Console.WriteLine($"{sessions.Count:N0} sessions, {templates.Count} templates; " +
                          $"train {train.Count:N0} ({train.Count(s => s.IsAnomaly):N0} anomalies), " +
                          $"test {test.Count:N0} ({test.Count(s => s.IsAnomaly):N0} anomalies)");

        var results = new List<(string Method, string Setting, DetectionMetrics Metrics, TimeSpan Elapsed)>();

        void Evaluate(string method, string setting, Func<List<bool>> predict)
        {
            Console.WriteLine($"Running {method}...");
            var watch = Stopwatch.StartNew();
            var metrics = DetectionMetrics.Compute(actual, predict());
            results.Add((method, setting, metrics, watch.Elapsed));
            Console.WriteLine($"  P={metrics.Precision:F3} R={metrics.Recall:F3} F1={metrics.F1:F3} ({watch.Elapsed:mm\\:ss})");
        }

        Evaluate("Rule: WARN/ERROR level", "any WARN/ERROR line in the block",
            () => test.Select(s => s.HasWarning).ToList());

        Evaluate("Rule: rare template", $"template in < {options.RareTemplateShare:P1} of training blocks",
            () => RareTemplateRule(train, test, templates.Count, options.RareTemplateShare));

        Evaluate("Isolation Forest", $"event count vectors, 100 trees, ψ=256, contamination {options.Contamination}",
            () => IsolationForestDetector(train, test, templates.Count, options.Contamination));

        if (!options.SkipDeepLog)
        {
            Evaluate("DeepLog (LSTM)",
                $"h=10, 2×64 LSTM, top-g={options.DeepLogTopG}, {options.DeepLogSessions:N0} normal training blocks, {options.DeepLogEpochs} epochs",
                () => DeepLogDetector(train, test, templates.Count, options));
        }

        var report = BuildReport(options, sessions.Count, templates.Count, train, test, results);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.ReportPath))!);
        File.WriteAllText(options.ReportPath, report);
        Console.WriteLine();
        Console.WriteLine(report);
    }

    private static List<bool> RareTemplateRule(List<HdfsSession> train, List<HdfsSession> test, int templateCount, double share)
    {
        var blocksWithTemplate = new int[templateCount];
        foreach (var session in train)
        {
            foreach (var id in session.Events.Distinct())
                blocksWithTemplate[id]++;
        }

        var minimum = share * train.Count;
        return test.Select(s => s.Events.Any(id => blocksWithTemplate[id] < minimum)).ToList();
    }

    internal static List<bool> IsolationForestDetector(List<HdfsSession> train, List<HdfsSession> test, int templateCount, double contamination)
    {
        var forest = new IsolationForest(treeCount: 100, sampleSize: 256, seed: 42);
        forest.Fit(train.Select(s => CountVector(s, templateCount)).ToList());

        // Many blocks share the same event counts; score each distinct vector once
        var cache = new Dictionary<string, double>();
        double Score(HdfsSession s)
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

    internal static List<bool> DeepLogDetector(List<HdfsSession> train, List<HdfsSession> test, int templateCount, HdfsExperimentOptions options)
    {
        var normal = train.Where(s => !s.IsAnomaly).Take(options.DeepLogSessions).Select(s => s.Events).ToList();

        using var model = new HdfsDeepLog(normal, options.DeepLogEpochs, Console.WriteLine);
        return model.WorstRanks(test).Select(rank => rank >= options.DeepLogTopG).ToList();
    }

    private static double[] CountVector(HdfsSession session, int templateCount)
    {
        var vector = new double[templateCount];
        foreach (var id in session.Events)
            vector[id]++;
        return vector;
    }

    private static string BuildReport(
        HdfsExperimentOptions options, int sessionCount, int templateCount, List<HdfsSession> train, List<HdfsSession> test,
        List<(string Method, string Setting, DetectionMetrics Metrics, TimeSpan Elapsed)> results)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("# HDFS anomaly detection results");
        sb.AppendLine();
        sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm} by `dotnet run --project experiments/ErrorLensLLM.Experiments -- hdfs`.");
        sb.AppendLine();
        sb.AppendLine("## Setup");
        sb.AppendLine();
        sb.AppendLine("- Dataset: LogHub HDFS_v1, sessions = blocks (`blk_*`), labels from `anomaly_label.csv`");
        sb.AppendLine($"- Parsing: Drain (depth 4, {options.Parser}, LogHub masks) → {templateCount} templates");
        sb.AppendLine($"- Split: chronological by first appearance, train ratio {options.TrainRatio.ToString(inv)}");
        sb.AppendLine($"- Sessions: {sessionCount:N0} total; train {train.Count:N0} ({train.Count(s => s.IsAnomaly):N0} anomalies), " +
                      $"test {test.Count:N0} ({test.Count(s => s.IsAnomaly):N0} anomalies)");
        sb.AppendLine("- All methods are evaluated on the same test blocks; DeepLog trains on normal blocks only (semi-supervised, as in the paper)");
        sb.AppendLine();
        sb.AppendLine("## Results (test set)");
        sb.AppendLine();
        sb.AppendLine("| Method | Precision | Recall | F1 | TP | FP | FN | Time | Setting |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var (method, setting, m, elapsed) in results)
        {
            sb.AppendLine(string.Create(inv,
                $"| {method} | {m.Precision:F3} | {m.Recall:F3} | **{m.F1:F3}** | {m.TruePositives:N0} | {m.FalsePositives:N0} | {m.FalseNegatives:N0} | {elapsed:mm\\:ss} | {setting} |"));
        }

        return sb.ToString();
    }
}
