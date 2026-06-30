namespace CsiRadar.Backend.Core.Configuration;

/// <summary>
/// Configuration for the V2 multi-source ingestion contract (Phase 1).
/// Bound from appsettings.json section "Ingestion".
///
/// Two RX devices publish raw I/Q over the binary MQTT contract; the backend
/// distinguishes them by MAC and aligns their streams <b>by seqNo</b>. The two
/// MACs below pin which physical device occupies the RX0 (primary) and RX1
/// (secondary) slot — until Phase 3 fusion, only RX0 feeds the legacy pipeline.
/// </summary>
public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";

    /// <summary>
    /// MAC of the RX device mapped to the <b>RX0 (primary)</b> slot, e.g.
    /// "AA:BB:CC:DD:EE:F0". Colons/dashes optional. Frames whose MAC matches
    /// neither RX0 nor RX1 are counted as <c>UnknownDevice</c> and discarded.
    /// </summary>
    public string Rx0Mac { get; set; } = string.Empty;

    /// <summary>
    /// MAC of the RX device mapped to the <b>RX1 (secondary)</b> slot.
    /// </summary>
    public string Rx1Mac { get; set; } = string.Empty;

    /// <summary>
    /// Pairing window in wall-clock milliseconds: a half-paired seqNo entry older
    /// than this is evicted and counted as an unpaired drop (an RX dropping out is a
    /// first-class observable, not a silent gap). Pairing is keyed by seqNo; this
    /// timeout only bounds how long we wait for the partner. ≤0 disables time-based
    /// eviction (the <see cref="MaxPending"/> cap still applies).
    /// </summary>
    public int PairingTimeoutMs { get; set; } = 200;

    /// <summary>
    /// Safety cap on outstanding half-paired entries. When exceeded, the oldest
    /// entry is evicted (and counted unpaired) so a stuck RX cannot grow the buffer
    /// unbounded. ~256 ≈ 2.5 s of one-sided frames at 100 Hz.
    /// </summary>
    public int MaxPending { get; set; } = 256;
}
