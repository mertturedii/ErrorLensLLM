using System.Threading.Channels;
using ErrorLensLLM.Core.Models;

namespace ErrorLensLLM.Core.Processing;

/// <summary>
/// In-memory buffer between the request pipeline and background processing.
/// Bounded and drop-oldest so that an error storm never blocks or crashes the host application.
/// </summary>
public sealed class ErrorQueue
{
    private readonly Channel<ErrorEvent> _channel;
    private long _dropped;

    public ErrorQueue(int capacity = 10_000)
    {
        _channel = Channel.CreateBounded<ErrorEvent>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true
            },
            _ => Interlocked.Increment(ref _dropped));
    }

    /// <summary>Events discarded because the queue was full; TryEnqueue still returns true for them.</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    public bool TryEnqueue(ErrorEvent error) => _channel.Writer.TryWrite(error);

    public IAsyncEnumerable<ErrorEvent> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}
