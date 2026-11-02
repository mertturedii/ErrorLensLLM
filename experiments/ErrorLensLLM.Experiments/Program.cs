using System.Globalization;
using ErrorLensLLM.Experiments.Hdfs;
using ErrorLensLLM.Ml.Parsing;

// Usage: dotnet run --project experiments/ErrorLensLLM.Experiments -c Release -- <command> [options]
//   hdfs       [--data data/HDFS] [--report docs/experiments/hdfs-results.md] [--train-ratio 0.5] [--epochs 10]
//              [--deeplog-sessions 5000] [--skip-deeplog]
//   hdfs-tune  [--data data/HDFS] [--report docs/experiments/hdfs-deeplog-tuning.md] [--train-ratio 0.5] [--epochs 10]
//              [--sizes 1000,5000,20000,50000] [--max-g 15]
//   hdfs-parse [--data data/HDFS] [--report docs/experiments/hdfs-parsing.md] [--similarities 0.5,0.6,0.7,0.8,0.9]
//              [--modes original,constanttokens] [--mask-paths false,true]
//              [--skip-detection]
//   hdfs and hdfs-tune accept --similarity <threshold> (default 0.5), --similarity-mode <original|constanttokens>
//   and --mask-paths to choose how Drain parses the log.

// Reports must not depend on the machine's locale (e.g. tr-TR writes 0,03 and %0,1)
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

double? DoubleOption(string name) => Option(name) is { } value ? double.Parse(value, CultureInfo.InvariantCulture) : null;
int? IntOption(string name) => Option(name) is { } value ? int.Parse(value) : null;

var parser = new HdfsParserSettings(
    DoubleOption("--similarity") ?? 0.5,
    Option("--similarity-mode") is { } mode ? Enum.Parse<DrainSimilarity>(mode, ignoreCase: true) : DrainSimilarity.Original,
    args.Contains("--mask-paths"));

switch (args.FirstOrDefault())
{
    case "hdfs":
    {
        var defaults = new HdfsExperimentOptions { DataDirectory = "data/HDFS", ReportPath = "docs/experiments/hdfs-results.md" };
        HdfsExperiment.Run(defaults with
        {
            DataDirectory = Option("--data") ?? defaults.DataDirectory,
            Parser = parser,
            ReportPath = Option("--report") ?? defaults.ReportPath,
            TrainRatio = DoubleOption("--train-ratio") ?? defaults.TrainRatio,
            DeepLogEpochs = IntOption("--epochs") ?? defaults.DeepLogEpochs,
            DeepLogSessions = IntOption("--deeplog-sessions") ?? defaults.DeepLogSessions,
            SkipDeepLog = args.Contains("--skip-deeplog")
        });
        return 0;
    }
    case "hdfs-tune":
    {
        var defaults = new HdfsDeepLogTuningOptions { DataDirectory = "data/HDFS", ReportPath = "docs/experiments/hdfs-deeplog-tuning.md" };
        HdfsDeepLogTuning.Run(defaults with
        {
            DataDirectory = Option("--data") ?? defaults.DataDirectory,
            Parser = parser,
            ReportPath = Option("--report") ?? defaults.ReportPath,
            TrainRatio = DoubleOption("--train-ratio") ?? defaults.TrainRatio,
            Epochs = IntOption("--epochs") ?? defaults.Epochs,
            TrainingSizes = Option("--sizes")?.Split(',').Select(int.Parse).ToArray() ?? defaults.TrainingSizes,
            MaxTopG = IntOption("--max-g") ?? defaults.MaxTopG
        });
        return 0;
    }
    case "hdfs-parse":
    {
        var defaults = new HdfsParsingExperimentOptions { DataDirectory = "data/HDFS", ReportPath = "docs/experiments/hdfs-parsing.md" };
        HdfsParsingExperiment.Run(defaults with
        {
            DataDirectory = Option("--data") ?? defaults.DataDirectory,
            ReportPath = Option("--report") ?? defaults.ReportPath,
            Similarities = Option("--similarities")?.Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray()
                           ?? defaults.Similarities,
            Modes = Option("--modes")?.Split(',').Select(m => Enum.Parse<DrainSimilarity>(m, ignoreCase: true)).ToArray() ?? defaults.Modes,
            MaskPaths = Option("--mask-paths") is { } masks ? masks.Split(',').Select(bool.Parse).ToArray() : defaults.MaskPaths,
            SkipDetection = args.Contains("--skip-detection")
        });
        return 0;
    }
    default:
        Console.Error.WriteLine("Usage: ErrorLensLLM.Experiments <hdfs|hdfs-tune|hdfs-parse> [options]  (see Program.cs)");
        return 1;
}
