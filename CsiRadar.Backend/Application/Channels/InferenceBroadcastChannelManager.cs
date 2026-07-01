using System.Threading.Channels;
using CsiRadar.Backend.Core.Entities;

namespace CsiRadar.Backend.Application.Channels;

/// <summary>
/// One per-window inference result on its way to the broadcast pump, optionally
/// carrying a freshly <em>confirmed</em> status (set by the debounce state machine).
/// </summary>
/// <param name="Result">The raw per-window inference result (always broadcast).</param>
/// <param name="ConfirmedStatus">
/// Non-null only on the window that completes a new confirmation — triggers the
/// Home Assistant automation. Null on every other window.
/// </param>
public readonly record struct InferenceBroadcastMessage(
    InferenceResult Result,
    string? ConfirmedStatus);

/// <summary>
/// Bounded channel decoupling inference (produced on the consumer thread) from
/// SignalR/MQTT broadcasting (drained by <c>InferenceBroadcastBackgroundService</c>),
/// mirroring <see cref="DspBroadcastChannelManager"/> for viz frames.
///
/// Inference events matter more than graph frames, so the capacity is larger; but it
/// is still bounded with DropOldest so a wedged client can never back-pressure the
/// inference-critical consumer loop.
/// </summary>
public sealed class InferenceBroadcastChannelManager
{
    private readonly Channel<InferenceBroadcastMessage> _channel;

    public InferenceBroadcastChannelManager()
    {
        var options = new BoundedChannelOptions(capacity: 64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
            SingleReader = true,
        };

        _channel = Channel.CreateBounded<InferenceBroadcastMessage>(options);
    }

    /// <summary>Writer endpoint — used by the processing consumer.</summary>
    public ChannelWriter<InferenceBroadcastMessage> Writer => _channel.Writer;

    /// <summary>Reader endpoint — used by the inference broadcast pump.</summary>
    public ChannelReader<InferenceBroadcastMessage> Reader => _channel.Reader;
}
