using System.Threading.Channels;
using CsiRadar.Backend.Infrastructure.Broadcasting;

namespace CsiRadar.Backend.Application.Channels;

/// <summary>
/// Bounded channel that decouples the inference-critical processing consumer
/// from SignalR transport. The consumer enqueues <see cref="CsiSignalDto"/> graph
/// frames via a non-blocking <c>TryWrite</c>; a dedicated pump
/// (<c>BroadcastBackgroundService</c>) drains them to connected clients.
///
/// Design decisions:
///   - Tiny capacity (2) with <c>FullMode.DropOldest</c>: graph data is fully
///     loss-tolerant — only the freshest frame matters. A slow/dead client can
///     never fill this channel and stall the producer; old frames are dropped.
///   - SingleWriter=true: only the processing consumer enqueues.
///   - SingleReader=true: only the broadcast pump dequeues.
///
/// Missing a graph frame is irrelevant; missing an inference frame is not — so
/// this lossy path is intentionally separate from the inbound CSI channel.
/// </summary>
public sealed class SignalBroadcastChannelManager
{
    private readonly Channel<CsiSignalDto> _channel;

    public SignalBroadcastChannelManager()
    {
        var options = new BoundedChannelOptions(capacity: 2)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
            SingleReader = true
        };

        _channel = Channel.CreateBounded<CsiSignalDto>(options);
    }

    /// <summary>Writer endpoint — used by the processing consumer to enqueue graph frames.</summary>
    public ChannelWriter<CsiSignalDto> Writer => _channel.Writer;

    /// <summary>Reader endpoint — used by the broadcast pump to dequeue graph frames.</summary>
    public ChannelReader<CsiSignalDto> Reader => _channel.Reader;
}
