using System.Diagnostics;
using System.Globalization;
using System.Text;
using ErrorLensLLM.Experiments.Common;
using ErrorLensLLM.Ml.Evaluation;

namespace ErrorLensLLM.Experiments.Bgl;

public sealed record BglExperimentOptions
{
    public required string DataDirectory { get; init; }
    public required string ReportPath { get; init; }

    /// <summary>Consecutive lines per window.</summary>
    public int WindowSize { get; init; } = 20;

    /// <summary>Windows are split in log order; the first part is used for training.</summary>
    public double TrainRatio { get; init; } = 0.8;

    /// <summary>
    /// Shuffle the windows before splitting (seed 42). Many papers split BGL this way; it hides the templates that only
    /// appear late in the log, so it is reported next to the chronological split, not instead of it.
    /// </summary>
    public bool Shuffle { get; init; }

    /// <summary>The last part of the training windows; thresholds and top-g are chosen here, never on the test set.</summary>
    public double ValidationShare { get; init; } = 0.1;

    public double RareTemplateShare { get; init; } = 0.001;

    /// <summary>Isolation Forest threshold without tuning (loglizer default, also used on HDFS).</summary>
    public double Contamination { get; init; } = 0.03;

    public int DeepLogSessions { get; init; } = 20000;
    public int DeepLogEpochs { get; init; } = 10;

    /// <summary>Top-g without tuning (DeepLog paper, also used on HDFS).</summary>
    public int DeepLogTopG { get; init; } = 9;

    public int MaxTopG { get; init; } = 150;
}

/// <summary>
/// Runs the HDFS method comparison on BGL. The rule baseline counts FATAL / FAILURE lines; Isolation Forest and DeepLog
/// are reported with the HDFS defaults and with a threshold / top-g chosen on a validation part of the training windows.
/// </summary>
public static class BglExperiment
{
    private static readonly double[] Contaminations = [0.01, 0.02, 0.03, 0.05, 0.08, 0.1, 0.15, 0.2];

    public static void Run(BglExperimentOptions options)
    {
        Console.WriteLine("Loading BGL...");
        var (windows, templates) = BglDataset.Load(options.DataDirectory, options.WindowSize);
        if (options.Shuffle)
        {
            var random = new Random(42);
            windows = windows.OrderBy(_ => random.Next()).ToList();
        }

        var trainCount = (int)(windows.Count * options.TrainRatio);
        var fitCount = (int)(trainCount * (1 - options.ValidationShare));
        var fit = windows.Take(fitCount).ToList();
        var validation = windows.Skip(fitCount).Take(trainCount - fitCount).ToList();
        var train = windows.Take(trainCount).ToList();
        var test = windows.Skip(trainCount).ToList();
        var actual = test.Select(s => s.IsAnomaly).ToList();
        var validationLabels = validation.Select(s => s.IsAnomaly).ToList();

        var trainTemplates = train.SelectMany(s => s.Events).ToHashSet();
        var unseen = test.Count(s => s.Events.Any(e => !trainTemplates.Contains(e)));
        var unseenAnomalies = test.Count(s => s.IsAnomaly && s.Events.Any(e => !trainTemplates.Contains(e)));

        Console.WriteLine($"{windows.Count:N0} windows of {options.WindowSize} lines, {templates.Count} templates; " +
                          $"train {train.Count:N0} ({train.Count(s => s.IsAnomaly):N0} anomalies), " +
                          $"test {test.Count:N0} ({test.Count(s => s.IsAnomaly):N0} anomalies)");

        var results = new List<(string Method, string Setting, DetectionMetrics Metrics, TimeSpan Elapsed)>();

        void Evaluate(string method, string setting, Func<List<bool>> predict)
        {
            Console.WriteLine($"Running {method} ({setting})...");
            var watch = Stopwatch.StartNew();
            var metrics = DetectionMetrics.Compute(actual, predict());
            results.Add((method, setting, metrics, watch.Elapsed));
            Console.WriteLine($"  P={metrics.Precision:F3} R={metrics.Recall:F3} F1={metrics.F1:F3} ({watch.Elapsed:mm\\:ss})");
        }

        Evaluate("Rule: FATAL/FAILURE level", "any FATAL or FAILURE line in the window", () => test.Select(s => s.HasWarning).ToList());

        Evaluate("Rule: rare template", $"template in < {options.RareTemplateShare:P1} of training windows",
            () => SessionDetectors.RareTemplate(train, test, templates.Count, options.RareTemplateShare));

        Evaluate("Isolation Forest", $"count vectors, contamination {options.Contamination} (HDFS default)",
            () => SessionDetectors.IsolationForest(train, test, templates.Count, options.Contamination));

        // Contamination chosen on validation: the forest is fitted on the fit part only, like DeepLog below
        var bestContamination = Contaminations
            .Select(c => (C: c, F1: DetectionMetrics.Compute(validationLabels, SessionDetectors.IsolationForest(fit, validation, templates.Count, c)).F1))
            .MaxBy(x => x.F1);
        Evaluate("Isolation Forest", $"count vectors, contamination {bestContamination.C} (validation F1 {bestContamination.F1:F3})",
            () => SessionDetectors.IsolationForest(train, test, templates.Count, bestContamination.C));

        var normal = fit.Where(s => !s.IsAnomaly).Take(options.DeepLogSessions).Select(s => s.Events).ToList();
        Console.WriteLine($"Training DeepLog on {normal.Count:N0} normal windows...");
        var trainWatch = Stopwatch.StartNew();
        using var deepLog = new SessionDeepLog(normal, options.DeepLogEpochs, Console.WriteLine);
        var trainTime = trainWatch.Elapsed;
        var validationRanks = deepLog.WorstRanks(validation);
        var testRanks = deepLog.WorstRanks(test);

        var bestG = Enumerable.Range(1, options.MaxTopG)
            .Select(g => (G: g, F1: DetectionMetrics.Compute(validationLabels, validationRanks.Select(r => r >= g).ToList()).F1))
            .MaxBy(x => x.F1);

        var setting = $"h=10, 2×64 LSTM, {normal.Count:N0} normal windows, {options.DeepLogEpochs} epochs";
        Evaluate("DeepLog (LSTM)", $"{setting}, top-g={options.DeepLogTopG} (HDFS default)",
            () => testRanks.Select(r => r >= options.DeepLogTopG).ToList());
        Evaluate("DeepLog (LSTM)", $"{setting}, top-g={bestG.G} (validation F1 {bestG.F1:F3})",
            () => testRanks.Select(r => r >= bestG.G).ToList());

        var report = BuildReport(options, windows.Count, templates.Count, train, test, validation, unseen, unseenAnomalies, trainTime, results);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.ReportPath))!);
        File.WriteAllText(options.ReportPath, report);
        Console.WriteLine();
        Console.WriteLine(report);
    }

    private static string BuildReport(
        BglExperimentOptions options, int windowCount, int templateCount, List<LogSession> train, List<LogSession> test,
        List<LogSession> validation, int unseen, int unseenAnomalies, TimeSpan deepLogTraining,
        List<(string Method, string Setting, DetectionMetrics Metrics, TimeSpan Elapsed)> results)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine($"# BGL anomaly detection results — {(options.Shuffle ? "random" : "chronological")} split");
        sb.AppendLine();
        sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm} by `dotnet run --project experiments/ErrorLensLLM.Experiments -c Release -- bgl{(options.Shuffle ? " --shuffle" : "")}`.");
        sb.AppendLine();
        sb.AppendLine("## Setup");
        sb.AppendLine();
        sb.AppendLine("- Dataset: LogHub BGL (Blue Gene/L supercomputer, 4,747,963 lines); a line is an alert when its first field is not `-`");
        sb.AppendLine($"- Parsing: Drain (depth 4, constant-token similarity ≥ 0.8 as in the collector; core ids and every token containing a digit are masked; templates with the same final text merged) → {templateCount} templates");
        sb.AppendLine($"- Windows: BGL has no session id, so the log is cut into fixed windows of {options.WindowSize} consecutive lines " +
                      $"({windowCount:N0} windows); a window is an anomaly when it contains an alert line");
        sb.AppendLine(string.Create(inv, $"- Split: {(options.Shuffle ? "random (windows shuffled, seed 42)" : "chronological")}, train ratio {options.TrainRatio}; train {train.Count:N0} ({train.Count(s => s.IsAnomaly):N0} anomalies), " +
                                         $"test {test.Count:N0} ({test.Count(s => s.IsAnomaly):N0} anomalies)"));
        sb.AppendLine(string.Create(inv, $"- Validation: the last {options.ValidationShare:P0} of the training windows ({validation.Count:N0}, " +
                                         $"{validation.Count(s => s.IsAnomaly):N0} anomalies). The tuned rows choose the Isolation Forest " +
                                         $"contamination and DeepLog's top-g there; the test set is never used for a choice"));
        sb.AppendLine($"- DeepLog trains on normal windows of the part before validation only ({deepLogTraining:mm\\:ss} on CPU)");
        sb.AppendLine(string.Create(inv, $"- {unseen:N0} test windows ({(double)unseen / test.Count:P1}) contain a template never seen in training; " +
                                         $"{unseenAnomalies:N0} of them are anomalies"));
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
