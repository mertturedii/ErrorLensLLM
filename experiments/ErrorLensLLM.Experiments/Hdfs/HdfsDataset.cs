using System.Diagnostics;
using System.Text.RegularExpressions;
using ErrorLensLLM.Ml.Parsing;

namespace ErrorLensLLM.Experiments.Hdfs;

/// <summary>One HDFS block: all log events that mention its block id, in log order.</summary>
public sealed record HdfsSession(string BlockId, bool IsAnomaly, bool HasWarning, int[] Events);

public sealed record HdfsTemplate(int Id, long Count, string Text);

/// <summary>
/// Loads LogHub HDFS_v1 (Xu et al., SOSP 2009): parses HDFS.log with Drain, groups events by block id and
/// joins the block labels. The parsed result is cached next to the data, one file per similarity threshold.
/// </summary>
public static partial class HdfsDataset
{
    /// <summary>HDFS has 29 true events; far more templates means the threshold is unreachable and Drain diverged.</summary>
    public const int MaxTemplates = 2000;

    /// <exception cref="ParsingDivergedException">Drain produced more than <see cref="MaxTemplates"/> templates.</exception>
    public static (List<HdfsSession> Sessions, List<HdfsTemplate> Templates) Load(string directory, HdfsParserSettings? settings = null)
    {
        settings ??= new HdfsParserSettings();
        var sessionsPath = Path.Combine(directory, $"sessions-{settings.CacheName}.tsv");
        var templatesPath = Path.Combine(directory, $"templates-{settings.CacheName}.tsv");

        if (!File.Exists(sessionsPath) || !File.Exists(templatesPath))
            Parse(directory, settings, sessionsPath, templatesPath);

        var templates = File.ReadLines(templatesPath)
            .Select(line => line.Split('\t'))
            .Select(p => new HdfsTemplate(int.Parse(p[0]), long.Parse(p[1]), p[2]))
            .ToList();

        var sessions = File.ReadLines(sessionsPath)
            .Select(line => line.Split('\t'))
            .Select(p => new HdfsSession(p[0], p[1] == "1", p[2] == "1", p[3].Split(',').Select(int.Parse).ToArray()))
            .ToList();

        return (sessions, templates);
    }

    /// <summary>Ground-truth event sequence (E1..E29) per block, from LogHub's manually labeled templates.</summary>
    public static Dictionary<string, string[]> LoadGroundTruthEvents(string directory) =>
        File.ReadLines(Path.Combine(directory, "preprocessed", "Event_traces.csv"))
            .Skip(1)
            .Select(line => line.Split(',', 4))
            .ToDictionary(p => p[0], p => p[3][(p[3].IndexOf('[') + 1)..p[3].IndexOf(']')].Split(','));

    private static void Parse(string directory, HdfsParserSettings settings, string sessionsPath, string templatesPath)
    {
        var labels = File.ReadLines(Path.Combine(directory, "preprocessed", "anomaly_label.csv"))
            .Skip(1)
            .Select(line => line.Split(','))
            .ToDictionary(p => p[0], p => p[1] == "Anomaly");

        // Masks used for HDFS in the LogHub / logparser benchmark
        List<Regex> masks =
        [
            BlockIdRegex(),
            new Regex(@"(/|)([0-9]+\.){3}[0-9]+(:[0-9]+|)(:|)", RegexOptions.Compiled),
            new Regex(@"(?<=[^A-Za-z0-9])(\-?\+?\d+)(?=[^A-Za-z0-9])|[0-9]+$", RegexOptions.Compiled)
        ];

        // Each job writes under its own directory; without this a strict threshold opens a template per path
        if (settings.MaskPaths)
            masks.Add(new Regex(@"(?<=^|\s)/\S+", RegexOptions.Compiled));

        var parser = new DrainParser(depth: 4, similarityThreshold: settings.Similarity, similarity: settings.Mode, maskPatterns: masks);

        var order = new List<string>();
        var events = new Dictionary<string, (List<int> Events, bool HasWarning)>();
        var watch = Stopwatch.StartNew();
        long lineCount = 0;

        foreach (var line in File.ReadLines(Path.Combine(directory, "HDFS.log")))
        {
            // <date> <time> <pid> <level> <component>: <content>
            var fields = line.Split(' ', 6);
            if (fields.Length < 6)
                continue;

            var template = parser.Parse(fields[5]);
            if (parser.Templates.Count > MaxTemplates)
                throw new ParsingDivergedException(parser.Templates.Count, lineCount);
            var isWarning = fields[3] is "WARN" or "ERROR" or "FATAL";

            foreach (var blockId in BlockIdRegex().Matches(fields[5]).Select(m => m.Value).Distinct())
            {
                if (!events.TryGetValue(blockId, out var session))
                {
                    session = ([], false);
                    order.Add(blockId);
                }

                session.Events.Add(template.Id);
                events[blockId] = (session.Events, session.HasWarning || isWarning);
            }

            if (++lineCount % 1_000_000 == 0)
                Console.WriteLine($"  {lineCount:N0} lines parsed, {parser.Templates.Count} templates, {watch.Elapsed:mm\\:ss}");
        }

        Console.WriteLine($"  {lineCount:N0} lines, {order.Count:N0} blocks, {parser.Templates.Count} templates in {watch.Elapsed:mm\\:ss}");

        File.WriteAllLines(templatesPath, parser.Templates.Select(t => $"{t.Id}\t{t.Count}\t{t.Text}"));
        File.WriteAllLines(sessionsPath, order
            .Where(labels.ContainsKey)
            .Select(id => $"{id}\t{(labels[id] ? 1 : 0)}\t{(events[id].HasWarning ? 1 : 0)}\t{string.Join(',', events[id].Events)}"));
    }

    [GeneratedRegex(@"blk_-?\d+")]
    private static partial Regex BlockIdRegex();
}

public sealed class ParsingDivergedException(int templates, long lines)
    : Exception($"Drain diverged: {templates:N0} templates after {lines:N0} lines")
{
    public int Templates { get; } = templates;
    public long Lines { get; } = lines;
}
