using System.Text.RegularExpressions;

namespace ErrorLensLLM.Ml.Parsing;

/// <summary>
/// Online log parser that turns raw log messages into templates (He et al., "Drain: An Online Log Parsing
/// Approach with Fixed Depth Tree", ICWS 2017). Messages are routed by token count and leading tokens to a
/// small set of candidate templates; the most similar one is reused, otherwise a new template is created.
/// </summary>
public sealed class DrainParser
{
    public const string Wildcard = "<*>";

    private readonly Node _root = new();
    private readonly List<LogTemplate> _templates = [];
    private readonly int _prefixTokens;
    private readonly double _similarityThreshold;
    private readonly DrainSimilarity _similarity;
    private readonly int _maxChildren;
    private readonly Regex[] _maskPatterns;

    /// <param name="depth">Tree depth including root, length and leaf layers (paper default 4).</param>
    /// <param name="similarityThreshold">Minimum similarity to join an existing template.</param>
    /// <param name="maxChildren">Max children per internal node; overflow is routed to a wildcard child.</param>
    /// <param name="maskPatterns">Domain regexes (ids, IPs) replaced by the wildcard before parsing.</param>
    /// <param name="similarity">How similarity is measured; see <see cref="DrainSimilarity"/>.</param>
    public DrainParser(
        int depth = 4,
        double similarityThreshold = 0.5,
        int maxChildren = 100,
        IEnumerable<Regex>? maskPatterns = null,
        DrainSimilarity similarity = DrainSimilarity.Original)
    {
        if (depth < 3)
            throw new ArgumentOutOfRangeException(nameof(depth), "Depth must be at least 3.");

        _prefixTokens = depth - 3;
        _similarityThreshold = similarityThreshold;
        _similarity = similarity;
        _maxChildren = maxChildren;
        _maskPatterns = maskPatterns?.ToArray() ?? [];
    }

    public IReadOnlyList<LogTemplate> Templates => _templates;

    public LogTemplate Parse(string message)
    {
        var tokens = Tokenize(message);
        var leaf = Descend(tokens, create: true)!;

        var match = BestMatch(leaf.Templates, tokens);
        if (match is null)
        {
            match = new LogTemplate(_templates.Count, tokens);
            _templates.Add(match);
            leaf.Templates.Add(match);
        }
        else
        {
            match.Merge(tokens);
        }

        match.Count++;
        return match;
    }

    /// <summary>Finds the template for a message without learning from it; null when nothing is similar enough.</summary>
    public LogTemplate? Match(string message)
    {
        var tokens = Tokenize(message);
        var leaf = Descend(tokens, create: false);
        return leaf is null ? null : BestMatch(leaf.Templates, tokens);
    }

    private string[] Tokenize(string message)
    {
        foreach (var pattern in _maskPatterns)
            message = pattern.Replace(message, Wildcard);

        return message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    }

    private Node? Descend(string[] tokens, bool create)
    {
        var node = GetChild(_root, tokens.Length.ToString(), create);

        for (var i = 0; node is not null && i < Math.Min(_prefixTokens, tokens.Length); i++)
        {
            var key = tokens[i].Any(char.IsDigit) ? Wildcard : tokens[i];

            if (node.Children.TryGetValue(key, out var child))
                node = child;
            else if (!create)
                node = node.Children.GetValueOrDefault(Wildcard);
            else
            {
                // Keep one slot free for the wildcard child so that rare tokens do not blow up the tree
                if (key != Wildcard && node.Children.Count + 1 >= _maxChildren)
                    key = Wildcard;
                node = GetChild(node, key, create: true);
            }
        }

        return node;
    }

    private static Node? GetChild(Node node, string key, bool create)
    {
        if (node.Children.TryGetValue(key, out var child))
            return child;
        if (!create)
            return null;

        child = new Node();
        node.Children[key] = child;
        return child;
    }

    private LogTemplate? BestMatch(List<LogTemplate> candidates, string[] tokens)
    {
        LogTemplate? best = null;
        var bestSimilarity = -1.0;
        var bestWildcards = -1;

        foreach (var candidate in candidates)
        {
            var (similarity, wildcards) = candidate.Similarity(tokens, _similarity);
            if (similarity > bestSimilarity || (similarity == bestSimilarity && wildcards > bestWildcards))
            {
                best = candidate;
                bestSimilarity = similarity;
                bestWildcards = wildcards;
            }
        }

        return bestSimilarity >= _similarityThreshold ? best : null;
    }

    private sealed class Node
    {
        public Dictionary<string, Node> Children { get; } = new(StringComparer.Ordinal);
        public List<LogTemplate> Templates { get; } = [];
    }
}

public enum DrainSimilarity
{
    /// <summary>
    /// Paper definition: identical constant tokens / all tokens. Wildcard positions never count as similar, so a
    /// template with many variables can never reach a high threshold and every message opens a new template.
    /// </summary>
    Original,

    /// <summary>
    /// Identical constant tokens / constant tokens of the template. Variables are ignored, so a high threshold
    /// separates messages that differ in a single word (e.g. "terminating" vs "interrupted") without exploding.
    /// </summary>
    ConstantTokens
}
