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
    /// Each sample is an ESP int8_t (−128..127), so this is <see cref="sbyte"/>[]
    /// (1 byte/sample). Amplitude and phase are computed downstream in the
    /// Processing layer; demodulation promotes sbyte→int (max imag²+real² fits int).
    ///
    /// NOTE: this array travels through the bounded channel and may be silently
    /// discarded by <c>BoundedChannelFullMode.DropOldest</c>, so it must NOT be
    /// rented from an <c>ArrayPool</c> (a dropped frame would never be returned).
    /// </summary>
    public sbyte[] RawCsiData { get; set; } = [];

    /// <summary>
    /// Number of valid elements in <see cref="RawCsiData"/>.
    /// Allows reusing array buffers larger than needed (ArrayPool).
    /// </summary>
    public int RawDataLength { get; set; }

    /// <summary>
    /// Processed CSI amplitude values per subcarrier.
    /// Computed from RawCsiData in the Processing layer via sqrt(I² + Q²).
    /// Empty until processed by <see cref="Core.Interfaces.ICsiStreamProcessor"/>.
    /// </summary>
    public float[] SubcarrierAmplitudes { get; set; } = [];

    /// <summary>
    /// Processed CSI phase values (radians) per subcarrier.
    /// Computed from RawCsiData in the Processing layer via atan2(I, Q).
    /// Empty until processed by <see cref="Core.Interfaces.ICsiStreamProcessor"/>.
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

    /// <summary>
    /// V2 multi-source ingestion: 48-bit source MAC of the RX device that measured
    /// this frame, packed big-endian into a <see cref="long"/> (MSB first:
    /// <c>b0&lt;&lt;40 | b1&lt;&lt;32 | … | b5</c>). This is the alignment key's
    /// device discriminator — the alignment buffer maps it to the RX0/RX1 slot
    /// configured in <c>IngestionOptions</c>. Cheap to compare (one long), unlike the
    /// formatted <see cref="SourceMac"/> string (which the hot path never sets).
    /// </summary>
    public long DeviceMac { get; set; }

    /// <summary>
    /// V2 multi-source ingestion: monotonic sequence number echoed from the TX ping
    /// that produced this frame. Both RX echo the <c>seqNo</c> of the frame they
    /// measured, so the backend aligns the two streams <b>by seqNo</b> — never by
    /// wall-clock, since the two ESP32 clocks are independent. uint32 wraps after
    /// ~2³² frames (≈497 days at 100 Hz), which the alignment window tolerates.
    /// </summary>
    public uint SeqNo { get; set; }
}
