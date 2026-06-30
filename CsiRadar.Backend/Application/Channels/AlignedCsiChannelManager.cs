using System.Threading.Channels;
using CsiRadar.Backend.Core.Entities;

namespace CsiRadar.Backend.Application.Channels;

/// <summary>
/// Bounded Channel&lt;AlignedCsiFrame&gt; bridging the alignment service (writer) and
/// the processing consumer (reader). Sits one stage past
/// <see cref="CsiDataChannelManager"/>: raw per-RX frames are paired by seqNo, then
/// the resulting aligned frames flow here.
///
/// Loss-tolerant by design (DropOldest): graph/inference frames may drop without
/// harm (V2 invariant 5). Confirmed status / automation ride a separate must-deliver
/// path and are unaffected by drops here.
/// </summary>
public sealed class AlignedCsiChannelManager
{
    private readonly Channel<AlignedCsiFrame> _channel;

    public AlignedCsiChannelManager()
    {
        var options = new BoundedChannelOptions(capacity: 512)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true, // the alignment background service
            SingleReader = true, // the processing consumer
        };
        _channel = Channel.CreateBounded<AlignedCsiFrame>(options);
    }

    public ChannelWriter<AlignedCsiFrame> Writer => _channel.Writer;
    public ChannelReader<AlignedCsiFrame> Reader => _channel.Reader;
}
