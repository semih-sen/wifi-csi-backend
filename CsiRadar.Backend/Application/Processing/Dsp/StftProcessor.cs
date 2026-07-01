namespace CsiRadar.Backend.Application.Processing.Dsp;

/// <summary>
/// Short-Time Fourier Transform → Doppler time-frequency map (Phase 2).
///
/// Doppler is the third modality: motion modulates each subcarrier's magnitude over
/// time, and its short-time spectrum exposes gait cadence. We take the STFT of each
/// subcarrier's <b>amplitude</b> time series (not the raw phase — single-antenna phase
/// is too noisy to carry usable Doppler, and amplitude cadence is what gait needs).
///
/// This is a <b>windowed</b> transform: it needs a history buffer, coupling it to the
/// windowing stage (Phase 3). The geometry (window, hop, bins) is pinned in
/// <see cref="DspContract"/> and is part of the train/serve contract.
///
/// Implementation is a direct real DFT (O(W²)) with a Hann window. W is small (64),
/// so this is cheap and — crucially — trivially reproducible bit-close in the Python
/// twin; a library FFT would introduce an algorithm-dependent rounding path that makes
/// golden parity harder to reason about.
/// </summary>
public static class StftProcessor
{
    /// <summary>
    /// One STFT magnitude column: Hann-window the length-<see cref="DspContract.StftWindowSize"/>
    /// series, take its real DFT, and write the <see cref="DspContract.StftBins"/>
    /// magnitudes (DC … Nyquist). This is the streaming primitive — the background
    /// service calls it once per hop on the most recent window.
    /// </summary>
    public static void MagnitudeColumn(ReadOnlySpan<double> window, Span<float> dstBins)
    {
        int w = DspContract.StftWindowSize;
        int bins = DspContract.StftBins;
        if (window.Length != w)
            throw new ArgumentException($"window ({window.Length}) != StftWindowSize ({w}).", nameof(window));
        if (dstBins.Length < bins)
            throw new ArgumentException($"dstBins ({dstBins.Length}) < StftBins ({bins}).", nameof(dstBins));

        double[] hann = DspContract.HannWindow;

        for (int m = 0; m < bins; m++)
        {
            double re = 0.0;
            double im = 0.0;
            double omega = 2.0 * Math.PI * m / w;
            for (int n = 0; n < w; n++)
            {
                double xn = window[n] * hann[n];
                double angle = omega * n;
                re += xn * Math.Cos(angle);
                im -= xn * Math.Sin(angle);
            }
            dstBins[m] = (float)Math.Sqrt(re * re + im * im);
        }
    }

    /// <summary>
    /// Full STFT of a 1-D series: slides the analysis window by
    /// <see cref="DspContract.StftHopSize"/> and stacks the magnitude columns.
    /// Returns a row-major <c>[numFrames, StftBins]</c> spectrogram (frame-major, time
    /// increasing down the rows). Used by the golden parity harness; the runtime path
    /// uses <see cref="MagnitudeColumn"/> per hop.
    /// </summary>
    public static float[,] Spectrogram(ReadOnlySpan<double> series)
    {
        int w = DspContract.StftWindowSize;
        int hop = DspContract.StftHopSize;
        int bins = DspContract.StftBins;

        int numFrames = series.Length < w ? 0 : (series.Length - w) / hop + 1;
        var spec = new float[numFrames, bins];

        Span<float> col = stackalloc float[bins];
        for (int f = 0; f < numFrames; f++)
        {
            int start = f * hop;
            MagnitudeColumn(series.Slice(start, w), col);
            for (int m = 0; m < bins; m++)
                spec[f, m] = col[m];
        }
        return spec;
    }
}
