using System.Threading.Channels;
using CsiRadar.Backend.Core.Entities;

namespace CsiRadar.Backend.Application.Channels;

/// <summary>
/// Manages the bounded Channel&lt;CsiData&gt; that bridges the MQTT Producer
/// and the Signal Processing Consumer.
///
/// Design decisions:
///   - BoundedChannel with capacity of 1024 frames (~10 seconds at 100 Hz)
///   - FullMode.DropOldest: If the consumer falls behind, the oldest frames
///     are silently dropped to prevent memory exhaustion (backpressure)
///   - SingleWriter=true: Only the MQTT listener writes
///   - SingleReader=true: Only the processing pipeline reads
///
/// This configuration is optimal for lock-free, high-throughput scenarios.
/// </summary>
public sealed class CsiDataChannelManager
{
    private readonly Channel<CsiData> _channel;

    public CsiDataChannelManager()
    {
        var options = new BoundedChannelOptions(capacity: 1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
            SingleReader = true
        };

        _channel = Channel.CreateBounded<CsiData>(options);
    }

    /// <summary>
    /// The writer endpoint. Used by the MQTT Producer to enqueue raw CSI data.
    /// </summary>
    public ChannelWriter<CsiData> Writer => _channel.Writer;

    /// <summary>
    /// The reader endpoint. Used by the Processing Consumer to dequeue CSI data.
    /// </summary>
    public ChannelReader<CsiData> Reader => _channel.Reader;
}
