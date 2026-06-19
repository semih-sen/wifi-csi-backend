namespace CsiRadar.Backend.Core.Entities;

/// <summary>
/// Represents a single raw CSI (Channel State Information) data frame
/// received from the ESP32 via MQTT.
/// Designed for zero-allocation hot-path: uses pre-allocated arrays
/// from ArrayPool&lt;T&gt; where possible.
/// </summary>
public sealed class CsiData
{
    /// <summary>
    /// UTC timestamp when the data was received by the backend (ticks).
    /// Using long (ticks) instead of DateTime to avoid heap allocation.
    /// </summary>
    public long TimestampTicks { get; set; }

    /// <summary>
    /// Raw interleaved CSI I/Q values directly from the ESP32 payload.
    /// Format: [imaginary_0, real_0, imaginary_1, real_1, ...].
    /// Amplitude and phase are computed downstream in the Processing layer.
    /// </summary>
    public int[] RawCsiData { get; set; } = [];

    /// <summary>
    /// Number of valid elements in <see cref="RawCsiData"/>.
    /// Allows reusing array buffers larger than needed (ArrayPool).
    /// </summary>
    public int RawDataLength { get; set; }

    /// <summary>
    /// Processed CSI amplitude values per subcarrier.
    /// Computed from RawCsiData in the Processing layer via sqrt(I² + Q²).
    /// Empty until processed by <see cref="Core.Interfaces.ISignalProcessor"/>.
    /// </summary>
    public float[] SubcarrierAmplitudes { get; set; } = [];

    /// <summary>
    /// Processed CSI phase values (radians) per subcarrier.
    /// Computed from RawCsiData in the Processing layer via atan2(I, Q).
    /// Empty until processed by <see cref="Core.Interfaces.ISignalProcessor"/>.
    /// </summary>
    public float[] SubcarrierPhases { get; set; } = [];

    /// <summary>
    /// RSSI (Received Signal Strength Indicator) from the packet header.
    /// </summary>
    public int Rssi { get; set; }

    /// <summary>
    /// Number of subcarriers (computed from RawDataLength / 2).
    /// Set by the Processing layer after I/Q separation.
    /// </summary>
    public int SubcarrierCount { get; set; }

    /// <summary>
    /// Source MAC address of the transmitting ESP32.
    /// </summary>
    public string? SourceMac { get; set; }
}
