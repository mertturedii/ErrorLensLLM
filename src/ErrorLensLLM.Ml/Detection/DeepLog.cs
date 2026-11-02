using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;

namespace ErrorLensLLM.Ml.Detection;

/// <summary>
/// Sequence anomaly detector (Du et al., "DeepLog: Anomaly Detection and Diagnosis from System Logs through
/// Deep Learning", CCS 2017). An LSTM learns which event usually follows the previous <c>WindowSize</c> events
/// in normal sessions; a session is anomalous when an actual next event is not among the top-g predictions.
/// </summary>
public sealed class DeepLog : IDisposable
{
    // Id 0 pads the start of a session; event ids are shifted by one
    private const int Padding = 0;

    private readonly DeepLogOptions _options;
    private readonly int _eventCount;
    private readonly Model _model;

    /// <param name="eventCount">Number of distinct event ids (ids must be in [0, eventCount)).</param>
    /// <param name="options">Hyper-parameters; paper defaults when null.</param>
    public DeepLog(int eventCount, DeepLogOptions? options = null)
    {
        TorchNative.EnsureLoaded();
        _options = options ?? new DeepLogOptions();
        _eventCount = eventCount;
        random.manual_seed(_options.Seed);
        _model = new Model(eventCount + 1, _options);
    }

    /// <summary>Trains on normal sessions. Returns the mean loss of each epoch.</summary>
    public IReadOnlyList<double> Fit(IReadOnlyList<int[]> normalSessions, Action<int, double>? onEpoch = null)
    {
        var (inputs, targets) = BuildWindows(normalSessions);
        var count = targets.Count;
        var losses = new List<double>();
        var shuffle = new Random(_options.Seed);

        using var optimizer = optim.Adam(_model.parameters(), _options.LearningRate);
        var lossFunction = nn.CrossEntropyLoss();
        _model.train();

        for (var epoch = 0; epoch < _options.Epochs; epoch++)
        {
            var order = Enumerable.Range(0, count).OrderBy(_ => shuffle.Next()).ToArray();
            var totalLoss = 0.0;

            for (var start = 0; start < count; start += _options.BatchSize)
            {
                using var scope = NewDisposeScope();
                var batch = order.AsSpan(start, Math.Min(_options.BatchSize, count - start)).ToArray();
                var x = WindowTensor(batch.Select(i => inputs[i]).ToArray());
                var y = tensor(batch.Select(i => (long)targets[i]).ToArray());

                optimizer.zero_grad();
                var loss = lossFunction.forward(_model.forward(x), y);
                loss.backward();
                optimizer.step();
                totalLoss += loss.item<float>() * batch.Length;
            }

            losses.Add(totalLoss / count);
            onEpoch?.Invoke(epoch + 1, totalLoss / count);
        }

        return losses;
    }

    /// <summary>
    /// For each session, the worst rank of an actual next event among the model's predictions (0 = most likely).
    /// A session is anomalous for top-g when the result is &gt;= g. Unseen event ids get <see cref="int.MaxValue"/>.
    /// </summary>
    public int[] WorstRanks(IReadOnlyList<int[]> sessions)
    {
        var results = new int[sessions.Count];
        var windows = new List<(int Session, int[] Input, int Target)>();

        for (var s = 0; s < sessions.Count; s++)
        {
            if (sessions[s].Any(e => e < 0 || e >= _eventCount))
            {
                results[s] = int.MaxValue;
                continue;
            }

            var (inputs, targets) = BuildWindows([sessions[s]]);
            for (var i = 0; i < targets.Count; i++)
                windows.Add((s, inputs[i], targets[i]));
        }

        _model.eval();
        using var noGrad = no_grad();

        for (var start = 0; start < windows.Count; start += _options.BatchSize)
        {
            using var scope = NewDisposeScope();
            var batch = windows.GetRange(start, Math.Min(_options.BatchSize, windows.Count - start));
            var logits = _model.forward(WindowTensor(batch.Select(w => w.Input).ToArray()));
            var targetLogits = logits.gather(1, tensor(batch.Select(w => (long)w.Target).ToArray()).unsqueeze(1));
            var ranks = logits.gt(targetLogits).sum(1).data<long>().ToArray();

            for (var i = 0; i < batch.Count; i++)
                results[batch[i].Session] = Math.Max(results[batch[i].Session], (int)ranks[i]);
        }

        return results;
    }

    public void Dispose() => _model.Dispose();

    /// <summary>Every event becomes a target predicted from the (left-padded) events before it.</summary>
    private (List<int[]> Inputs, List<int> Targets) BuildWindows(IReadOnlyList<int[]> sessions)
    {
        var inputs = new List<int[]>();
        var targets = new List<int>();
        var size = _options.WindowSize;

        foreach (var session in sessions)
        {
            for (var t = 0; t < session.Length; t++)
            {
                var window = new int[size];
                for (var k = 0; k < size; k++)
                {
                    var source = t - size + k;
                    window[k] = source < 0 ? Padding : session[source] + 1;
                }

                inputs.Add(window);
                targets.Add(session[t] + 1);
            }
        }

        return (inputs, targets);
    }

    private Tensor WindowTensor(int[][] windows)
    {
        var flat = new long[windows.Length * _options.WindowSize];
        for (var i = 0; i < windows.Length; i++)
        {
            for (var k = 0; k < _options.WindowSize; k++)
                flat[i * _options.WindowSize + k] = windows[i][k];
        }

        return tensor(flat).reshape(windows.Length, _options.WindowSize);
    }

    private sealed class Model : nn.Module<Tensor, Tensor>
    {
        private readonly Embedding _embedding;
        private readonly LSTM _lstm;
        private readonly Linear _output;

        public Model(int vocabularySize, DeepLogOptions options) : base(nameof(DeepLog))
        {
            _embedding = nn.Embedding(vocabularySize, options.EmbeddingSize);
            _lstm = nn.LSTM(options.EmbeddingSize, options.HiddenSize, options.Layers, batchFirst: true);
            _output = nn.Linear(options.HiddenSize, vocabularySize);
            RegisterComponents();
        }

        public override Tensor forward(Tensor input)
        {
            var (sequence, _, _) = _lstm.forward(_embedding.forward(input));
            return _output.forward(sequence[.., -1, ..]);
        }
    }
}

/// <summary>Hyper-parameters; defaults follow the DeepLog paper (h = 10, 2 layers, 64 units).</summary>
public sealed record DeepLogOptions
{
    public int WindowSize { get; init; } = 10;
    public int EmbeddingSize { get; init; } = 32;
    public int HiddenSize { get; init; } = 64;
    public int Layers { get; init; } = 2;
    public int Epochs { get; init; } = 10;
    public int BatchSize { get; init; } = 2048;
    public double LearningRate { get; init; } = 0.001;
    public int Seed { get; init; } = 42;
}
