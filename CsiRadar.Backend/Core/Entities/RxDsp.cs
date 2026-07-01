namespace CsiRadar.Backend.Core.Entities;

/// <summary>
/// The three derived modalities for a <b>single RX</b> at one aligned time step
/// (Phase 2). Derived entirely server-side from that RX's raw int8 I/Q — the RX
/// devices publish raw complex I/Q only (V2 invariant 2).
///
/// Per-RX only: RX0 and RX1 are carried side by side but <b>never fused here</b>
/// (fusion is Phase 3). No normalization is applied (that is baked into the ONNX graph
/// in a later phase).
/// </summary>
public sealed class RxDsp
{
    /// <summary>Packed source MAC of the RX this was derived from (see <see cref="CsiData.DeviceMac"/>).</summary>
    public long DeviceMac { get; init; }

    /// <summary><c>|CSI|</c> per subcarrier — <c>sqrt(I²+Q²)</c>. Length = subcarrier count.</summary>
    public required float[] Amplitude { get; init; }

    /// <summary>
    /// Single-antenna sanitized phase per subcarrier (unwrapped + linearly detrended).
    /// Length = subcarrier count. Relative/clean-enough, not absolute.
    /// </summary>
    public required float[] SanitizedPhase { get; init; }

    /// <summary>
    /// Latest Doppler column when a hop boundary produced one this step, else null.
    /// Row-major <c>[subcarrier, StftBins]</c>: one magnitude spectrum per subcarrier
    /// over the most recent STFT window. Null on the frames between hops (STFT is a
    /// windowed transform emitted every <c>StftHopSize</c> samples).
    /// </summary>
    public float[,]? DopplerColumn { get; init; }
}
