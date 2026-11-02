using ErrorLensLLM.Core.Models;
using ErrorLensLLM.Core.Processing;

namespace ErrorLensLLM.Core.Tests;

public class ErrorQueueTests
{
    [Fact]
    public void Dropped_events_are_counted()
    {
        var queue = new ErrorQueue(capacity: 2);

        for (var i = 0; i < 5; i++)
            Assert.True(queue.TryEnqueue(new ErrorEvent { Application = "a", ExceptionType = "e", Message = $"m{i}", StackTrace = "" }));

        Assert.Equal(3, queue.DroppedCount);
    }
}
