using System.Diagnostics;
using System.Text;
using ErrorLensLLM.Ml.Evaluation;

namespace ErrorLensLLM.Experiments.Hdfs;

public sealed record HdfsDeepLogTuningOptions
{
    public required string DataDirectory { get; init; }
    public required string ReportPath { get; init; }
    public double TrainRatio { get; init; } = 0.5;

    /// <summary>How the log is parsed into templates.</summary>
    public HdfsParserSettings Parser { get; init; } = new();

    /// <summary>Last share of the training part held out to choose hyper-parameters; the test part is never used for that.</summary>
    public double ValidationShare { get; init; } = 0.2;

    public IReadOnlyList<int> TrainingSizes { get; init; } = [1000, 5000, 20000, 50000];
    public int MaxTopG { get; init; } = 15;
    public int Epochs { get; init; } = 10;
}

/// <summary>
/// Chooses DeepLog's top-g and number of normal training blocks on a validation split, then reports the
/// chosen setting on the untouched test split. One model per training size; every g reuses its ranks.
/// </summary>
public static class HdfsDeepLogTuning
{
    private sealed record TrainedModel(int Size, int Templates, TimeSpan Elapsed, int[] ValidationRanks, int[] TestRanks);

    public static void Run(HdfsDeepLogTuningOptions options)
    {
        Console.WriteLine("Loading HDFS...");
        var (sessions, _) = HdfsDataset.Load(options.DataDirectory, options.Parser);

        var trainCount = (int)(sessions.Count * options.TrainRatio);
        var fitCount = (int)(trainCount * (1 - options.ValidationShare));
        var fit = sessions.Take(fitCount).ToList();
        var validation = sessions.Skip(fitCount).Take(trainCount - fitCount).ToList();
        var test = sessions.Skip(trainCount).ToList();
        var normal = fit.Where(s => !s.IsAnomaly).Select(s => s.Events).ToList();

        Console.WriteLine($"fit {fit.Count:N0} ({normal.Count:N0} normal), validation {validation.Count:N0} " +
                          $"({validation.Count(s => s.IsAnomaly):N0} anomalies), test {test.Count:N0} ({test.Count(s => s.IsAnomaly):N0} anomalies)");

        var runs = new List<TrainedModel>();
        foreach (var size in options.TrainingSizes.Where(n => n <= normal.Count))
        {
            Console.WriteLine($"Training DeepLog on {size:N0} normal blocks...");
            var watch = Stopwatch.StartNew();
            var trainingSet = normal.Take(size).ToList();
            using var model = new HdfsDeepLog(trainingSet, options.Epochs, Console.WriteLine);
            var templates = trainingSet.SelectMany(e => e).Distinct().Count();
            runs.Add(new TrainedModel(size, templates, watch.Elapsed, model.WorstRanks(validation), model.WorstRanks(test)));
        }

        var validationLabels = validation.Select(s => s.IsAnomaly).ToList();
        var testLabels = test.Select(s => s.IsAnomaly).ToList();
        DetectionMetrics At(int[] ranks, List<bool> labels, int g) =>
            DetectionMetrics.Compute(labels, ranks.Select(r => r >= g).ToList());

        var best = runs
            .SelectMany(run => Enumerable.Range(1, options.MaxTopG).Select(g => (Run: run, G: g, F1: At(run.ValidationRanks, validationLabels, g).F1)))
            .MaxBy(x => x.F1);

        var report = new StringBuilder();
        report.AppendLine("# DeepLog tuning on HDFS");
        report.AppendLine();
        report.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm} by `dotnet run --project experiments/ErrorLensLLM.Experiments -c Release -- hdfs-tune`.");
        report.AppendLine();
        report.AppendLine("## Protocol");
        report.AppendLine();
        report.AppendLine($"- Parsing: Drain, {options.Parser}");
        report.AppendLine($"- Same chronological split as the main experiment (train ratio {options.TrainRatio}); the test blocks are identical");
        report.AppendLine($"- The last {options.ValidationShare:P0} of the training part is held out as **validation** " +
                          $"({validation.Count:N0} blocks, {validationLabels.Count(x => x):N0} anomalies)");
        report.AppendLine($"- DeepLog is trained on the first N **normal** blocks of the remaining part ({normal.Count:N0} available), {options.Epochs} epochs");
        report.AppendLine($"- Top-g and N are chosen by validation F1 only; test numbers are reported for the chosen setting");
        report.AppendLine();
        report.AppendLine("## Training size (best g per size, chosen on validation)");
        report.AppendLine();
        report.AppendLine("| Normal blocks (N) | Templates seen | Train time | Best g | Val F1 | Test P | Test R | **Test F1** | Test blocks with unseen template |");
        report.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var run in runs)
        {
            var g = Enumerable.Range(1, options.MaxTopG).MaxBy(g => At(run.ValidationRanks, validationLabels, g).F1);
            var val = At(run.ValidationRanks, validationLabels, g);
            var t = At(run.TestRanks, testLabels, g);
            var unseen = run.TestRanks.Count(r => r == int.MaxValue);
            report.AppendLine($"| {run.Size:N0} | {run.Templates} | {run.Elapsed:mm\\:ss} | {g} | {val.F1:F3} | {t.Precision:F3} | {t.Recall:F3} | **{t.F1:F3}** | {unseen:N0} |");
        }

        report.AppendLine();
        report.AppendLine($"## Top-g curve for N = {best.Run.Size:N0}");
        report.AppendLine();
        report.AppendLine("Test columns are shown for analysis only; they were not used to choose g.");
        report.AppendLine();
        report.AppendLine("| g | Val P | Val R | Val F1 | Test P | Test R | Test F1 |");
        report.AppendLine("|---|---|---|---|---|---|---|");
        for (var g = 1; g <= options.MaxTopG; g++)
        {
            var val = At(best.Run.ValidationRanks, validationLabels, g);
            var t = At(best.Run.TestRanks, testLabels, g);
            var mark = g == best.G ? " ← chosen" : "";
            report.AppendLine($"| {g}{mark} | {val.Precision:F3} | {val.Recall:F3} | {val.F1:F3} | {t.Precision:F3} | {t.Recall:F3} | {t.F1:F3} |");
        }

        var chosen = At(best.Run.TestRanks, testLabels, best.G);
        report.AppendLine();
        report.AppendLine("## Result");
        report.AppendLine();
        report.AppendLine($"Chosen on validation: **N = {best.Run.Size:N0}, g = {best.G}** → test precision {chosen.Precision:F3}, " +
                          $"recall {chosen.Recall:F3}, **F1 {chosen.F1:F3}** (TP {chosen.TruePositives:N0}, FP {chosen.FalsePositives:N0}, FN {chosen.FalseNegatives:N0}).");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.ReportPath))!);
        File.WriteAllText(options.ReportPath, report.ToString());
        Console.WriteLine();
        Console.WriteLine(report);
    }
}
