using System.Diagnostics;
using System.Text.RegularExpressions;
using ErrorLensLLM.Experiments.Common;
using ErrorLensLLM.Experiments.Hdfs;
using ErrorLensLLM.Ml.Parsing;

namespace ErrorLensLLM.Experiments.Bgl;

/// <summary>
/// Loads LogHub BGL (Blue Gene/L, Oliner &amp; Stearley, DSN 2007): parses BGL.log with Drain and cuts it into fixed
/// windows of consecutive lines. BGL has no session id, so a window is the unit that is labeled and scored; it is an
/// anomaly when any of its lines carries an alert label (the first field is not "-"). The parsed result is cached.
/// </summary>
public static class BglDataset
{
    /// <summary>BGL has a few hundred event types; far more templates means Drain diverged.</summary>
    public const int MaxTemplates = 5000;

    public static (List<LogSession> Windows, List<LogTemplateRow> Templates) Load(string directory, int windowSize)
    {
        var cache = Path.Combine(directory, $"windows-w{windowSize}.tsv");
        var templatesPath = Path.Combine(directory, "templates.tsv");

        if (!File.Exists(cache) || !File.Exists(templatesPath))
            Parse(directory, windowSize, cache, templatesPath);

        var templates = File.ReadLines(templatesPath)
            .Select(line => line.Split('\t'))
            .Select(p => new LogTemplateRow(int.Parse(p[0]), long.Parse(p[1]), p[2]))
            .ToList();

        var windows = File.ReadLines(cache)
            .Select(line => line.Split('\t'))
            .Select(p => new LogSession(p[0], p[1] == "1", p[2] == "1", p[3].Split(',').Select(int.Parse).ToArray()))
            .ToList();

        return (windows, templates);
    }

    private static void Parse(string directory, int windowSize, string cachePath, string templatesPath)
    {
        // Core ids as in the LogHub / logparser BGL setting; BGL also dumps registers and addresses ("fpr7=3fd8426b",
        // "28:0da3a530"), which open a template per value unless every token with a digit is a variable. Such lines are
        // then mostly wildcards, which Drain's original similarity never matches (wildcards do not count as similar,
        // so it diverged past 5,000 templates); the constant-token similarity the collector uses ignores them.
        List<Regex> masks =
        [
            new(@"core\.\d+", RegexOptions.Compiled),
            new(@"(?<=^|\s)\S*\d\S*(?=\s|$)", RegexOptions.Compiled)
        ];

        var parser = new DrainParser(depth: 4, similarityThreshold: 0.8, similarity: DrainSimilarity.ConstantTokens, maskPatterns: masks);
        var watch = Stopwatch.StartNew();
        var windows = new List<(long Start, bool Anomaly, bool Fatal, int[] Events)>();
        var events = new List<int>(windowSize);
        bool anomaly = false, fatal = false;
        long lineCount = 0;

        foreach (var line in File.ReadLines(Path.Combine(directory, "BGL.log")))
        {
            // <label> <unix time> <date> <node> <time> <node> <type> <component> <level> <content>
            var fields = line.Split(' ', 10);
            // A few lines are cut short; they still take a place in their window
            var content = fields.Length == 10 && fields[9].Trim().Length > 0 ? fields[9] : "<empty>";

            var template = parser.Parse(content);
            if (parser.Templates.Count > MaxTemplates)
                throw new ParsingDivergedException(parser.Templates.Count, lineCount);

            events.Add(template.Id);
            anomaly |= fields[0] != "-";
            fatal |= fields.Length > 8 && fields[8] is "FATAL" or "FAILURE";

            if (++lineCount % windowSize == 0)
            {
                windows.Add((lineCount - windowSize, anomaly, fatal, events.ToArray()));
                events.Clear();
                anomaly = fatal = false;
            }

            if (lineCount % 1_000_000 == 0)
                Console.WriteLine($"  {lineCount:N0} lines parsed, {parser.Templates.Count} templates, {watch.Elapsed:mm\\:ss}");
        }

        // Drain keeps one template per (length, first tokens) branch, so the same final text can exist several times
        // (e.g. all-variable lines of equal length). Those are one event: merge them and number the result densely.
        var byText = parser.Templates.GroupBy(t => t.Text).Select((g, index) => (Index: index, g.Key, Count: g.Sum(t => t.Count), Ids: g.Select(t => t.Id)))
            .ToList();
        var canonical = byText.SelectMany(g => g.Ids.Select(id => (id, g.Index))).ToDictionary(p => p.id, p => p.Index);

        // A trailing partial window is dropped so that every window has the same length
        Console.WriteLine($"  {lineCount:N0} lines, {windows.Count:N0} windows, {parser.Templates.Count} Drain templates " +
                          $"→ {byText.Count} distinct, {watch.Elapsed:mm\\:ss}");

        File.WriteAllLines(templatesPath, byText.Select(t => $"{t.Index}\t{t.Count}\t{t.Key.Replace('\t', ' ')}"));
        File.WriteAllLines(cachePath, windows.Select(w =>
            $"{w.Start}\t{(w.Anomaly ? 1 : 0)}\t{(w.Fatal ? 1 : 0)}\t{string.Join(',', w.Events.Select(id => canonical[id]))}"));
    }
}
