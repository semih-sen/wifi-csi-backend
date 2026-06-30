namespace CsiRadar.Backend.Core.Entities;

/// <summary>
/// A time-aligned pair of CSI frames — one from each RX — that share the same
/// <see cref="SeqNo"/> (the TX ping they both measured). Emitted by the alignment
/// buffer once both halves of a seqNo have arrived within the pairing window.
///
/// This is the single artifact the V2 signal path stands on: Phase 2 derives the
/// three modalities per RX from <see cref="Rx0"/>/<see cref="Rx1"/>, and Phase 3
/// fuses them. In Phase 1 only <see cref="Rx0"/> (the primary) feeds the legacy
/// single-stream pipeline; <see cref="Rx1"/> is carried and counted but not yet
/// consumed by DSP.
/// </summary>
public sealed class AlignedCsiFrame
{
    /// <summary>The shared sequence number both RX echoed (the alignment key).</summary>
    public uint SeqNo { get; init; }

    /// <summary>Frame from the RX0 (primary) device.</summary>
    public required CsiData Rx0 { get; init; }

    /// <summary>Frame from the RX1 (secondary) device.</summary>
    public required CsiData Rx1 { get; init; }

    /// <summary>
    /// Absolute difference in backend arrival time (ticks) between the two halves.
    /// A drift/observability signal — large skew flags a struggling RX even while
    /// pairing still succeeds. Alignment is by seqNo, never by this value.
    /// </summary>
    public long ArrivalSkewTicks { get; init; }
}
