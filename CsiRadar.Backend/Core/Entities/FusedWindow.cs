using CsiRadar.Backend.Application.Processing.Windowing;

namespace CsiRadar.Backend.Core.Entities;

/// <summary>
/// The fused, windowed multi-modal model input tensor (Phase 3) — the single artifact
/// both the Phase 4 activity model and the Phase 5 identity model will consume. Emitted
/// onto the Phase 4 seam once a full <see cref="WindowContract.WindowFrames"/> window of
/// <b>contiguous</b> aligned frames has accumulated.
///
/// Both payloads are flattened row-major with the axis order pinned in
/// <see cref="WindowContract"/> — the layout is a contract (a silent transpose feeds the
/// model garbage), proven bit-for-bit against the Python windowing twin.
///
/// A <see cref="FusedWindow"/> is only ever produced from frames that span <b>no</b>
/// alignment gap: a window straddling a Phase 1 alignment drop is rejected upstream and
/// never stitched (its Doppler map would be corrupt). So an emitted window is, by
/// construction, gap-free — <see cref="StartSeqNo"/>…<see cref="EndSeqNo"/> are contiguous.
///
/// No normalization is applied (that is baked into the ONNX graph in a later phase).
/// </summary>
public sealed class FusedWindow
{
    /// <summary>Sequence number of the oldest frame in the window.</summary>
    public uint StartSeqNo { get; init; }

    /// <summary>
    /// Sequence number of the newest frame in the window. Because the window is gap-free,
    /// this equals <c>StartSeqNo + WindowFrames − 1</c> (modulo uint32 wrap).
    /// </summary>
    public uint EndSeqNo { get; init; }

    /// <summary>
    /// Dense per-frame modality stack, flattened <c>[rx, modality, frame, subcarrier]</c>
    /// (see <see cref="WindowContract.DenseIndex"/>). Length =
    /// <see cref="WindowContract.DenseLength"/>.
    /// </summary>
    public required float[] Dense { get; init; }

    /// <summary>
    /// Windowed per-subcarrier Doppler map, flattened <c>[rx, subcarrier, stftFrame, bin]</c>
    /// (see <see cref="WindowContract.DopplerIndex"/>). Length =
    /// <see cref="WindowContract.DopplerLength"/>.
    /// </summary>
    public required float[] Doppler { get; init; }
}
