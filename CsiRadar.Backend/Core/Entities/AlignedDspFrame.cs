namespace CsiRadar.Backend.Core.Entities;

/// <summary>
/// Per-RX DSP output for one aligned time step (Phase 2): the seqNo plus each RX's
/// three derived modalities. This is the artifact Phase 3 fusion/windowing will
/// consume; in Phase 2 it is emitted and observed but not yet fused or fed to a model.
///
/// The originating <see cref="AlignedCsiFrame"/> is carried so downstream stages keep
/// access to the raw I/Q (V2 invariant 3: record raw, derive everything).
/// </summary>
public sealed class AlignedDspFrame
{
    /// <summary>Shared sequence number of the aligned pair this was derived from.</summary>
    public uint SeqNo { get; init; }

    /// <summary>Derived modalities for the RX0 (primary) stream.</summary>
    public required RxDsp Rx0 { get; init; }

    /// <summary>Derived modalities for the RX1 (secondary) stream.</summary>
    public required RxDsp Rx1 { get; init; }

    /// <summary>The raw aligned pair these modalities were derived from.</summary>
    public required AlignedCsiFrame Source { get; init; }
}
