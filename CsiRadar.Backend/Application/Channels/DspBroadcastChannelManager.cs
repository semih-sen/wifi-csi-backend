using System.Threading.Channels;
using CsiRadar.Backend.Infrastructure.Broadcasting;

namespace CsiRadar.Backend.Application.Channels;

/// <summary>
/// Shallow, loss-tolerant channel that decouples the Phase 2 DSP stage from SignalR
/// transport for the live viz tap. The DSP consumer enqueues <see cref="DspFrameDto"/>
/// via a non-blocking <c>TryWrite</c> (already throttled to 10 Hz); the
/// <c>DspBroadcastBackgroundService</c> pump drains them to clients.
///
/// Depth 2 + <see cref="BoundedChannelFullMode.DropOldest"/>: viz frames are fully
/// loss-tolerant — only the freshest matters, and a slow/dead client can never fill
/// this channel or stall the DSP stage. Mirrors the retired V1 graph-broadcast
/// decoupling exactly.
/// </summary>
public sealed class DspBroadcastChannelManager
{
    private readonly Channel<DspFrameDto> _channel;

    public DspBroadcastChannelManager()
    {
        var options = new BoundedChannelOptions(capacity: 2)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true, // the DSP consumer
            SingleReader = true, // the DSP broadcast pump
        };
        _channel = Channel.CreateBounded<DspFrameDto>(options);
    }

    public ChannelWriter<DspFrameDto> Writer => _channel.Writer;
    public ChannelReader<DspFrameDto> Reader => _channel.Reader;
}
