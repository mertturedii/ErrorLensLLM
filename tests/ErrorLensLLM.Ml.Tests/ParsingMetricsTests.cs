using ErrorLensLLM.Ml.Evaluation;

namespace ErrorLensLLM.Ml.Tests;

public class ParsingMetricsTests
{
    [Fact]
    public void Perfect_grouping_scores_one_even_with_different_ids()
    {
        var result = ParsingMetrics.GroupingAccuracy([(1, "E5"), (1, "E5"), (2, "E9")]);

        Assert.Equal(1.0, result.Accuracy);
        Assert.Empty(result.Merged);
        Assert.Empty(result.Split);
    }

    [Fact]
    public void Merged_events_make_all_their_messages_wrong()
    {
        // Template 1 mixes "terminating" (E8) and "interrupted" (E11); template 2 is correct
        var result = ParsingMetrics.GroupingAccuracy([(1, "E8"), (1, "E8"), (1, "E11"), (2, "E3")]);

        Assert.Equal(0.25, result.Accuracy);
        Assert.Equal(["E8", "E11"], result.Merged[1]);
    }

    [Fact]
    public void Split_event_makes_all_its_messages_wrong()
    {
        var result = ParsingMetrics.GroupingAccuracy([(1, "E7"), (2, "E7"), (3, "E3"), (3, "E3")]);

        Assert.Equal(0.5, result.Accuracy);
        Assert.Equal([1, 2], result.Split["E7"]);
    }
}
