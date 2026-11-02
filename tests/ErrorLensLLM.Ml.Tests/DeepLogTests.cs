using ErrorLensLLM.Ml.Detection;

namespace ErrorLensLLM.Ml.Tests;

public class DeepLogTests
{
    [Fact]
    public void Learned_order_is_normal_and_broken_order_is_anomalous()
    {
        // Normal workflow: 0 → 1 → 2 → 3 (e.g. receive → write → ack → close)
        var normal = Enumerable.Repeat(new[] { 0, 1, 2, 3 }, 200).ToList();

        using var model = new DeepLog(eventCount: 4, new DeepLogOptions { WindowSize = 3, Epochs = 30, BatchSize = 64, LearningRate = 0.01 });
        model.Fit(normal);

        var ranks = model.WorstRanks([[0, 1, 2, 3], [0, 3, 1, 2]]);

        Assert.Equal(0, ranks[0]);
        Assert.True(ranks[1] >= 1, $"broken order rank {ranks[1]}");
    }

    [Fact]
    public void Unknown_event_is_always_anomalous()
    {
        using var model = new DeepLog(eventCount: 2, new DeepLogOptions { Epochs = 1 });
        model.Fit([[0, 1]]);

        Assert.Equal(int.MaxValue, model.WorstRanks([[0, 5]])[0]);
    }
}
