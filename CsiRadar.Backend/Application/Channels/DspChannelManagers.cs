using System.Threading.Channels;
using CsiRadar.Backend.Core.Entities;

namespace CsiRadar.Backend.Application.Channels;

/// <summary>
/// DSP-stage <b>input</b> channel (Phase 2). The alignment service fans each
/// <see cref="AlignedCsiFrame"/> out to this channel <i>in addition to</i> the legacy
/// <see cref="AlignedCsiChannelManager"/>, so the new per-RX DSP path runs as an
/// isolated, independently-observable branch without perturbing the proven Phase 1
/// live graph/recording path.
///
/// Loss-tolerant (DropOldest): DSP frames are graph/inference-class, not must-deliver.
/// </summary>
public sealed class DspInputChannelManager
{
    private readonly Channel<AlignedCsiFrame> _channel;

    public DspInputChannelManager()
    {
        var options = new BoundedChannelOptions(capacity: 512)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true, // the alignment service
            SingleReader = true, // the DSP service
        };
        _channel = Channel.CreateBounded<AlignedCsiFrame>(options);
    }

    public ChannelWriter<AlignedCsiFrame> Writer => _channel.Writer;
    public ChannelReader<AlignedCsiFrame> Reader => _channel.Reader;
}

/// <summary>
/// DSP-stage <b>output</b> channel (Phase 2): carries <see cref="AlignedDspFrame"/>
/// (per-RX amplitude · sanitized phase · Doppler). This is the seam Phase 3 fusion
/// will read from. In Phase 2 nothing consumes it yet; DropOldest keeps it bounded
/// (old frames are simply GC'd) — a laid pipe with the water not yet turned on.
/// </summary>
public sealed class DspOutputChannelManager
{
    private readonly Channel<AlignedDspFrame> _channel;

    public DspOutputChannelManager()
    {
        var options = new BoundedChannelOptions(capacity: 256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,  // the DSP service
            SingleReader = false, // Phase 3 fusion (not wired yet)
        };
        _channel = Channel.CreateBounded<AlignedDspFrame>(options);
    }

    public ChannelWriter<AlignedDspFrame> Writer => _channel.Writer;
    public ChannelReader<AlignedDspFrame> Reader => _channel.Reader;
}
