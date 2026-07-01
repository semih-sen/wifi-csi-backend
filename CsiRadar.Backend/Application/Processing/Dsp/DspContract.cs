namespace CsiRadar.Backend.Application.Processing.Dsp;

/// <summary>
/// Pinned constants of the V2 per-RX DSP layer (Phase 2). These are part of the
/// <b>train/serve contract</b> (Seam A): the Python twin in <c>wifi-csi-ml</c>
/// (<c>src/dsp_twin.py</c>) MUST use identical values, and every stage ships with a
/// golden cross-language parity test. Changing any value here is a contract break —
/// bump nothing silently.
///
/// Nothing here normalizes: normalization is baked into the ONNX graph in a later
/// phase (the V1 "normalize-in-graph" principle). These transforms emit the clean,
/// physically-meaningful signal only.
/// </summary>
public static class DspContract
{
    /// <summary>Subcarriers per frame (ESP32 HT-LTF, 64). One <c>|CSI|</c>/phase value each.</summary>
    public const int Subcarriers = 64;

    // ── STFT (Doppler) parameters — the windowed transform's pinned geometry ──
    // At 100 Hz these give a 0.64 s analysis window stepped every 0.16 s: fine enough
    // to resolve gait cadence (~1–2 Hz) while several columns fit inside a Phase 3 gait
    // window (≥2–3 s). window == FFT length, so there is no zero-padding.

    /// <summary>STFT analysis window length in samples (also the DFT length).</summary>
    public const int StftWindowSize = 64;

    /// <summary>STFT hop (stride between consecutive analysis windows), in samples.</summary>
    public const int StftHopSize = 16;

    /// <summary>
    /// Number of non-redundant real-DFT magnitude bins: <c>StftWindowSize/2 + 1</c>
    /// (DC … Nyquist). A real input has a conjugate-symmetric spectrum, so only these
    /// bins are kept.
    /// </summary>
    public const int StftBins = StftWindowSize / 2 + 1;

    /// <summary>
    /// Symmetric Hann window, <c>w[n] = 0.5 − 0.5·cos(2π·n/(W−1))</c> — identical to
    /// <c>numpy.hanning(W)</c>. Precomputed once; reused for every STFT column. Stored
    /// as <see cref="double"/> so the windowed DFT accumulates in full precision before
    /// the final <see cref="float"/> round (keeps cross-language drift below a float ULP).
    /// </summary>
    public static readonly double[] HannWindow = BuildHann(StftWindowSize);

    private static double[] BuildHann(int w)
    {
        var win = new double[w];
        for (int n = 0; n < w; n++)
            win[n] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * n / (w - 1));
        return win;
    }
}
