using System.Threading.Channels;
using CsiRadar.Backend.Core.Entities;

namespace CsiRadar.Backend.Application.Channels;

/// <summary>
/// Fusion-stage <b>output</b> channel (Phase 3): carries the fused, windowed
/// <see cref="FusedWindow"/> tensor (dense multi-modal stack + windowed Doppler map).
/// This is the seam Phase 4 (cascade orchestration) will read from. In Phase 3 nothing
/// consumes it yet; DropOldest keeps it bounded (old windows are GC'd) — a laid pipe with
/// the water not yet turned on.
///
/// Loss-tolerant (DropOldest): a fused window is model-input / inference-class, not
/// must-deliver. Capacity is small — a window is large (~120k floats) and produced only
/// every <see cref="Windowing.WindowContract.WindowSlide"/> frames, so a shallow buffer
/// bounds memory while tolerating brief consumer stalls.
/// </summary>
public sealed class FusedWindowChannelManager
{
    private readonly Channel<FusedWindow> _channel;

    public FusedWindowChannelManager()
    {
        var options = new BoundedChannelOptions(capacity: 8)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,  // the fusion/windowing service
            SingleReader = false, // Phase 4 cascade (not wired yet)
        };
        _channel = Channel.CreateBounded<FusedWindow>(options);
    }

    public ChannelWriter<FusedWindow> Writer => _channel.Writer;
    public ChannelReader<FusedWindow> Reader => _channel.Reader;
}
