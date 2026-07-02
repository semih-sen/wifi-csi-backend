using CsiRadar.Backend.Application.Processing.Dsp;

namespace CsiRadar.Backend.Application.Processing.Windowing;

/// <summary>
/// Pinned constants + tensor layout of the V2 <b>fusion + windowing</b> stage (Phase 3).
///
/// This is a <b>train/serve contract</b> (Seam A), exactly like <see cref="DspContract"/>:
/// the Python windowing twin in <c>wifi-csi-ml</c> (<c>src/window_twin.py</c>) MUST use
/// identical values and produce a bit-close identical tensor, enforced by a golden
/// cross-language parity test. The V1 lesson is baked in here: <b>a silent transpose
/// feeds the model garbage</b>, so the tensor layout is a pinned contract — not an
/// implementation detail. Changing any value below is a contract break: change both
/// sides and regenerate the golden.
///
/// A fused window carries two tensors, each flattened row-major with an explicit,
/// pinned axis order:
///
///   • <b>Dense</b> — the per-frame multi-modal stack
///     <c>[rx, modality, frame, subcarrier]</c> = <c>[2, 2, WindowFrames, 64]</c>.
///     modality 0 = amplitude, 1 = sanitized phase (both from <see cref="DspContract"/>).
///
///   • <b>Doppler</b> — the windowed per-subcarrier Doppler map
///     <c>[rx, subcarrier, stftFrame, bin]</c> = <c>[2, 64, StftFrames, StftBins]</c>,
///     the STFT (pinned Phase 2 geometry) of each subcarrier's amplitude series over the
///     window. Doppler is a windowed transform (needs a history buffer), so it is
///     naturally computed here, at the windowing stage — over the window's own frames,
///     giving a fixed-shape, boundary-aligned, fully deterministic tensor.
///
/// No normalization is applied here (that is baked into the ONNX graph in a later phase).
/// </summary>
public static class WindowContract
{
    /// <summary>Subcarriers per frame — inherited from the DSP contract (64).</summary>
    public const int Subcarriers = DspContract.Subcarriers;

    /// <summary>Number of RX streams fused per window (RX0, RX1). Single-occupancy V2.</summary>
    public const int RxCount = 2;

    /// <summary>Per-frame modalities stacked in the dense tensor: 0 = amplitude, 1 = sanitized phase.</summary>
    public const int Modalities = 2;

    /// <summary>Dense modality axis index for amplitude.</summary>
    public const int ModalityAmplitude = 0;

    /// <summary>Dense modality axis index for sanitized phase.</summary>
    public const int ModalityPhase = 1;

    // ── Gait window geometry — the pinned length/slide ──
    // Gait identity is LONGER and continuity-sensitive than presence: the window must
    // span several full stride cycles (a stride is ~1 s). At 100 Hz, 256 frames = 2.56 s
    // (≈2–3 stride cycles), satisfying the "≥2–3 s" requirement, and it is a clean
    // multiple of the STFT hop so the Doppler map has an integral frame count.

    /// <summary>Window length in aligned frames (time steps). 256 @ 100 Hz = 2.56 s.</summary>
    public const int WindowFrames = 256;

    /// <summary>
    /// Slide (hop) between consecutive emitted windows, in frames. 128 @ 100 Hz = 1.28 s
    /// → 50% overlap, so a walk is covered by overlapping windows without gaps.
    /// </summary>
    public const int WindowSlide = 128;

    // ── Windowed Doppler geometry (derived from the pinned Phase 2 STFT geometry) ──

    /// <summary>
    /// STFT columns produced across one window: <c>(WindowFrames − StftWindowSize) /
    /// StftHopSize + 1</c>. With the pinned constants: (256 − 64)/16 + 1 = 13.
    /// </summary>
    public const int StftFrames =
        (WindowFrames - DspContract.StftWindowSize) / DspContract.StftHopSize + 1;

    /// <summary>Non-redundant real-DFT magnitude bins per STFT column (DC…Nyquist) = 33.</summary>
    public const int StftBins = DspContract.StftBins;

    /// <summary>Flattened length of the dense tensor <c>[rx, modality, frame, subcarrier]</c>.</summary>
    public const int DenseLength = RxCount * Modalities * WindowFrames * Subcarriers;

    /// <summary>Flattened length of the Doppler tensor <c>[rx, subcarrier, stftFrame, bin]</c>.</summary>
    public const int DopplerLength = RxCount * Subcarriers * StftFrames * StftBins;

    /// <summary>
    /// Row-major flat index into the dense tensor, axis order
    /// <c>[rx, modality, frame, subcarrier]</c>. This ordering is the pinned contract —
    /// the Python twin's <c>numpy</c> reshape/ravel must match it exactly.
    /// </summary>
    public static int DenseIndex(int rx, int modality, int frame, int subcarrier) =>
        ((rx * Modalities + modality) * WindowFrames + frame) * Subcarriers + subcarrier;

    /// <summary>
    /// Row-major flat index into the Doppler tensor, axis order
    /// <c>[rx, subcarrier, stftFrame, bin]</c>. Pinned contract; mirror in the twin.
    /// </summary>
    public static int DopplerIndex(int rx, int subcarrier, int stftFrame, int bin) =>
        ((rx * Subcarriers + subcarrier) * StftFrames + stftFrame) * StftBins + bin;

    /// <summary>Human-readable dense shape for diagnostics, e.g. <c>2x2x256x64</c>.</summary>
    public static string DenseShape => $"{RxCount}x{Modalities}x{WindowFrames}x{Subcarriers}";

    /// <summary>Human-readable Doppler shape for diagnostics, e.g. <c>2x64x13x33</c>.</summary>
    public static string DopplerShape => $"{RxCount}x{Subcarriers}x{StftFrames}x{StftBins}";
}
