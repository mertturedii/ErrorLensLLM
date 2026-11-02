namespace ErrorLensLLM.Ml.Parsing;

/// <summary>A log event type discovered by <see cref="DrainParser"/>. The id is stable; the text may generalize over time.</summary>
public sealed class LogTemplate
{
    private readonly string[] _tokens;

    internal LogTemplate(int id, string[] tokens)
    {
        Id = id;
        _tokens = (string[])tokens.Clone();
    }

    public int Id { get; }

    /// <summary>Number of messages assigned to this template.</summary>
    public long Count { get; internal set; }

    public string Text => string.Join(' ', _tokens);

    public override string ToString() => $"E{Id}: {Text}";

    /// <summary>Share of positions with an identical (non-wildcard) token, and the template's wildcard count.</summary>
    internal (double Similarity, int Wildcards) Similarity(string[] tokens)
    {
        var same = 0;
        var wildcards = 0;
        for (var i = 0; i < _tokens.Length; i++)
        {
            if (_tokens[i] == DrainParser.Wildcard)
                wildcards++;
            else if (_tokens[i] == tokens[i])
                same++;
        }

        return (_tokens.Length == 0 ? 1.0 : (double)same / _tokens.Length, wildcards);
    }

    internal void Merge(string[] tokens)
    {
        for (var i = 0; i < _tokens.Length; i++)
        {
            if (_tokens[i] != tokens[i])
                _tokens[i] = DrainParser.Wildcard;
        }
    }
}
