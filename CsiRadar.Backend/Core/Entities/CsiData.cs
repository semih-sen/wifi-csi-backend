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
    /// Raw CSI amplitude values extracted from the ESP32 payload.
    /// Each element represents the amplitude of a subcarrier.
    /// Typical length: 64 or 128 subcarriers depending on bandwidth.
    /// </summary>
    public float[] SubcarrierAmplitudes { get; set; } = [];

    /// <summary>
    /// Optional: Raw CSI phase values (radians) per subcarrier.
    /// </summary>
    public float[] SubcarrierPhases { get; set; } = [];

    /// <summary>
    /// RSSI (Received Signal Strength Indicator) from the packet header.
    /// </summary>
    public int Rssi { get; set; }

    /// <summary>
    /// The length of valid data in SubcarrierAmplitudes.
    /// Allows reusing array buffers larger than needed.
    /// </summary>
    public int SubcarrierCount { get; set; }
}
