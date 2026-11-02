using System.Text;
using ErrorLensLLM.Ml.Evaluation;
using ErrorLensLLM.Ml.Parsing;

namespace ErrorLensLLM.Experiments.Hdfs;

public sealed record HdfsParsingExperimentOptions
{
    public required string DataDirectory { get; init; }
    public required string ReportPath { get; init; }
    public IReadOnlyList<double> Similarities { get; init; } = [0.5, 0.6, 0.7, 0.8, 0.9];
    public IReadOnlyList<DrainSimilarity> Modes { get; init; } = [DrainSimilarity.Original, DrainSimilarity.ConstantTokens];
    public IReadOnlyList<bool> MaskPaths { get; init; } = [false, true];
    public bool SkipDetection { get; init; }
}

/// <summary>
/// Measures Drain's grouping accuracy against LogHub's labeled HDFS events for each similarity mode and threshold,
/// and how the resulting templates change Isolation Forest and DeepLog (main-experiment settings).
/// </summary>
public static class HdfsParsingExperiment
{
    public static void Run(HdfsParsingExperimentOptions options)
    {
        Console.WriteLine("Loading ground truth...");
        var truth = HdfsDataset.LoadGroundTruthEvents(options.DataDirectory);
        var truthTemplates = File.ReadLines(Path.Combine(options.DataDirectory, "preprocessed", "HDFS.log_templates.csv"))
            .Skip(1)
            .Select(line => line.Split(',', 2))
            .ToDictionary(p => p[0], p => p[1]);

        var rows = new List<string>();
        var details = new StringBuilder();

        var settings =
            from maskPaths in options.MaskPaths
            from mode in options.Modes
            from similarity in options.Similarities
            select new HdfsParserSettings(similarity, mode, maskPaths);

        foreach (var parser in settings)
        {
            Console.WriteLine($"{parser}...");
            var columns = $"| {parser.Mode} | {(parser.MaskPaths ? "yes" : "no")} | {parser.Similarity} |";
            List<HdfsSession> sessions;
            List<HdfsTemplate> templates;
            try
            {
                (sessions, templates) = HdfsDataset.Load(options.DataDirectory, parser);
            }
            catch (ParsingDivergedException e)
            {
                Console.WriteLine($"  {e.Message}");
                var empty = options.SkipDetection ? "" : " – | – |";
                rows.Add($"{columns} > {HdfsDataset.MaxTemplates:N0} | diverged after {e.Lines:N0} lines | – | – |{empty}");
                continue;
            }
            var text = templates.ToDictionary(t => t.Id, t => t.Text);

            // Block traces list the same messages in the same order, so events align position by position
            var result = ParsingMetrics.GroupingAccuracy(sessions.SelectMany(s => s.Events.Zip(truth[s.BlockId])));

            var f1 = "";
            if (!options.SkipDetection)
            {
                var detection = new HdfsExperimentOptions { DataDirectory = options.DataDirectory, ReportPath = "", Parser = parser };
                var trainCount = (int)(sessions.Count * detection.TrainRatio);
                var train = sessions.Take(trainCount).ToList();
                var test = sessions.Skip(trainCount).ToList();
                var actual = test.Select(s => s.IsAnomaly).ToList();

                var forest = DetectionMetrics.Compute(actual, HdfsExperiment.IsolationForestDetector(train, test, templates.Count, detection.Contamination));
                var deepLog = DetectionMetrics.Compute(actual, HdfsExperiment.DeepLogDetector(train, test, templates.Count, detection));
                f1 = $" {forest.F1:F3} | {deepLog.F1:F3} |";
                Console.WriteLine($"  GA {result.Accuracy:F3}, IF F1 {forest.F1:F3}, DeepLog F1 {deepLog.F1:F3}");
            }

            rows.Add($"{columns} {templates.Count} | **{result.Accuracy:F3}** | {result.Merged.Count} | {result.Split.Count} |{f1}");

            details.AppendLine($"### {parser}");
            details.AppendLine();
            foreach (var (template, events) in result.Merged)
            {
                details.AppendLine($"- **Merged** `{text[template]}` ← " +
                                   string.Join(", ", events.Select(e => $"{e} `{truthTemplates[e]}`")));
            }
            foreach (var (eventId, parts) in result.Split)
                details.AppendLine($"- **Split** {eventId} `{truthTemplates[eventId]}` → {parts.Count} templates");
            details.AppendLine();
        }

        var report = new StringBuilder();
        report.AppendLine("# Drain parsing accuracy on HDFS");
        report.AppendLine();
        report.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm} by `dotnet run --project experiments/ErrorLensLLM.Experiments -c Release -- hdfs-parse`.");
        report.AppendLine();
        report.AppendLine("- Ground truth: LogHub `Event_traces.csv` (29 manually labeled events), aligned with the parsed blocks position by position");
        report.AppendLine("- Grouping accuracy (Zhu et al., 2019): a message is correct only if its template groups exactly the messages of its true event; " +
                          "computed over block traces, so a message that mentions two blocks counts twice");
        report.AppendLine("- Similarity modes: **Original** = identical constant tokens / all tokens (Drain paper); " +
                          "**ConstantTokens** = identical constant tokens / constant tokens of the template (variables ignored)");
        report.AppendLine("- Path mask: also replace file paths (`/mnt/...`) with the wildcard; not part of the LogHub masks");
        report.AppendLine($"- A run stops as *diverged* when Drain exceeds {HdfsDataset.MaxTemplates:N0} templates (HDFS has 29 true events)");
        report.AppendLine("- Detection columns: test F1 with the main experiment's settings (chronological 0.5 split, IF contamination 0.03, DeepLog 5,000 blocks, g = 9)");
        report.AppendLine();
        report.AppendLine(options.SkipDetection
            ? "| Mode | Path mask | Similarity | Templates | Grouping accuracy | Merged templates | Split events |\n|---|---|---|---|---|---|---|"
            : "| Mode | Path mask | Similarity | Templates | Grouping accuracy | Merged templates | Split events | IF F1 | DeepLog F1 |\n|---|---|---|---|---|---|---|---|---|");
        rows.ForEach(row => report.AppendLine(row));
        report.AppendLine();
        report.AppendLine("## Errors per threshold");
        report.AppendLine();
        report.Append(details);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.ReportPath))!);
        File.WriteAllText(options.ReportPath, report.ToString());
        Console.WriteLine();
        Console.WriteLine(report);
    }
}
