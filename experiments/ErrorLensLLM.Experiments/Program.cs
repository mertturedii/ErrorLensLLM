using System.Globalization;
using ErrorLensLLM.Experiments.Hdfs;

// Usage: dotnet run --project experiments/ErrorLensLLM.Experiments -c Release -- hdfs [--data data/HDFS]
//        [--report docs/experiments/hdfs-results.md] [--train-ratio 0.5] [--epochs 10] [--deeplog-sessions 5000] [--skip-deeplog]

// Reports must not depend on the machine's locale (e.g. tr-TR writes 0,03 and %0,1)
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

if (args.Length == 0 || args[0] != "hdfs")
{
    Console.Error.WriteLine("Usage: ErrorLensLLM.Experiments hdfs [--data <dir>] [--report <file>] [--train-ratio <r>] " +
                            "[--epochs <n>] [--deeplog-sessions <n>] [--skip-deeplog]");
    return 1;
}

string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

var defaults = new HdfsExperimentOptions { DataDirectory = "data/HDFS", ReportPath = "docs/experiments/hdfs-results.md" };
var options = defaults with
{
    DataDirectory = Option("--data") ?? defaults.DataDirectory,
    ReportPath = Option("--report") ?? defaults.ReportPath,
    TrainRatio = Option("--train-ratio") is { } ratio ? double.Parse(ratio, CultureInfo.InvariantCulture) : defaults.TrainRatio,
    DeepLogEpochs = Option("--epochs") is { } epochs ? int.Parse(epochs) : defaults.DeepLogEpochs,
    DeepLogSessions = Option("--deeplog-sessions") is { } count ? int.Parse(count) : defaults.DeepLogSessions,
    SkipDeepLog = args.Contains("--skip-deeplog")
};

HdfsExperiment.Run(options);
return 0;
