using ErrorLensLLM.Ml.Detection;

namespace ErrorLensLLM.Experiments.Common;

/// <summary>DeepLog trained on normal sessions; maps template ids to the model's vocabulary and scores sessions.</summary>
public sealed class SessionDeepLog : IDisposable
{
    private readonly DeepLog _model;
    private readonly Dictionary<int, int> _vocabulary;

    public SessionDeepLog(IReadOnlyList<int[]> normalSessions, int epochs, Action<string>? log = null)
    {
        // Templates never seen in training are mapped out of range so that DeepLog flags them
        _vocabulary = normalSessions.SelectMany(e => e).Distinct().Order()
            .Select((id, index) => (id, index))
            .ToDictionary(p => p.id, p => p.index);

        _model = new DeepLog(_vocabulary.Count, new DeepLogOptions { Epochs = epochs });
        _model.Fit(normalSessions.Select(Remap).ToList(), (epoch, loss) => log?.Invoke($"  epoch {epoch}: loss {loss:F4}"));
    }

    /// <summary>Worst rank of an actual next event per session; int.MaxValue when the session has an unseen template.</summary>
    public int[] WorstRanks(IReadOnlyList<LogSession> sessions)
    {
        // Identical sequences are common; predict each distinct one once
        var keys = sessions.Select(s => string.Join(',', s.Events)).ToArray();
        var distinct = keys.Distinct().ToList();
        var index = distinct.Select((key, i) => (key, i)).ToDictionary(p => p.key, p => p.i);

        var ranks = _model.WorstRanks(distinct.Select(key => Remap(key.Split(',').Select(int.Parse).ToArray())).ToList());
        return keys.Select(key => ranks[index[key]]).ToArray();
    }

    public void Dispose() => _model.Dispose();

    private int[] Remap(int[] events) => events.Select(id => _vocabulary.GetValueOrDefault(id, -1)).ToArray();
}
