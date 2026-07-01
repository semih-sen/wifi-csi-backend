namespace CsiRadar.Backend.Application.Processing.Dsp;

/// <summary>
/// Pure, allocation-light per-frame DSP primitives derived from raw int8 I/Q
/// (Phase 2). All heavy DSP lives server-side (V2 invariant 2): the ESP32 ships raw
/// complex I/Q only; amplitude and phase are derived here.
///
/// Every function is a deterministic transform with a byte-for-byte Python twin
/// (<c>wifi-csi-ml/src/dsp_twin.py</c>) and a golden parity test — the #1 project
/// invariant (train/serve identity). Internal math is done in <see cref="double"/>
/// and rounded to <see cref="float"/> only at the boundary, so the sole cross-language
/// difference is sub-float-ULP libm noise, never a structural divergence.
///
/// Raw layout: interleaved <c>[imag₀, real₀, imag₁, real₁, …]</c> (ESP-IDF order).
/// </summary>
public static class CsiDsp
{
    /// <summary>
    /// Amplitude modality: <c>|CSI|ₖ = sqrt(imagₖ² + realₖ²)</c> per subcarrier
    /// (the V1 magnitude, ported forward). int8²+int8² is exact in int, and float32
    /// <c>sqrt</c> is IEEE-754 correctly-rounded, so this is bit-identical to
    /// <c>numpy.sqrt((i*i+q*q).astype(float32))</c>.
    /// </summary>
    /// <returns>The subcarrier count written.</returns>
    public static int Amplitude(ReadOnlySpan<sbyte> rawIq, Span<float> dst)
    {
        int n = rawIq.Length / 2;
        if (dst.Length < n)
            throw new ArgumentException($"dst ({dst.Length}) < subcarriers ({n}).", nameof(dst));

        for (int k = 0; k < n; k++)
        {
            int imag = rawIq[2 * k];
            int real = rawIq[2 * k + 1];
            dst[k] = MathF.Sqrt(imag * imag + real * real);
        }
        return n;
    }

    /// <summary>
    /// Sanitized-phase modality (single-antenna sanitization).
    ///
    /// Raw CSI phase is dominated by CFO/SFO/STO and PLL noise — unusable as-is. We
    /// apply the standard single-antenna clean-up:
    ///   1. raw phase φₖ = atan2(imagₖ, realₖ);
    ///   2. unwrap across subcarrier index k (numpy.unwrap semantics, discont = π);
    ///   3. remove the linear trend by <b>least-squares</b> over k (slope = the STO
    ///      timing slope, intercept = the constant offset).
    ///
    /// IMPORTANT: this is <b>single-antenna</b> sanitization, NOT the dual-antenna
    /// conjugate-multiplication method (which cancels CFO/SFO by referencing a second
    /// RX chain on the same NIC — our ESP32 hardware cannot do it). The result is a
    /// <i>relative, clean-enough</i> phase that exposes motion, not an absolute phase.
    /// </summary>
    /// <returns>The subcarrier count written.</returns>
    public static int SanitizedPhase(ReadOnlySpan<sbyte> rawIq, Span<float> dst)
    {
        int n = rawIq.Length / 2;
        if (dst.Length < n)
            throw new ArgumentException($"dst ({dst.Length}) < subcarriers ({n}).", nameof(dst));
        if (n == 0)
            return 0;

        // Full-precision scratch; n is tiny (64), so the stack copy is cheap.
        Span<double> phase = stackalloc double[n];
        for (int k = 0; k < n; k++)
            phase[k] = Math.Atan2(rawIq[2 * k], rawIq[2 * k + 1]);

        UnwrapInPlace(phase);
        DetrendLeastSquaresInPlace(phase);

        for (int k = 0; k < n; k++)
            dst[k] = (float)phase[k];
        return n;
    }

    /// <summary>
    /// In-place phase unwrap across the array, replicating <c>numpy.unwrap</c>
    /// (period = 2π, discont = π) exactly so the C# and Python results agree:
    /// wherever a consecutive jump exceeds π, ±2π multiples are added to make it
    /// continuous.
    /// </summary>
    public static void UnwrapInPlace(Span<double> p)
    {
        const double period = 2.0 * Math.PI;
        const double high = Math.PI;   // period/2
        const double low = -Math.PI;   // -period/2
        double cumulative = 0.0;
        // Diffs are taken over the ORIGINAL values (numpy.unwrap semantics); track the
        // previous original sample so the in-place write below can't feed back into the
        // next difference.
        double prevOriginal = p.Length > 0 ? p[0] : 0.0;

        for (int i = 1; i < p.Length; i++)
        {
            double current = p[i];
            double dd = current - prevOriginal;
            // Floored mod into [low, high): ((dd - low) mod period) + low.
            double ddmod = FlooredMod(dd - low, period) + low;
            // numpy boundary fix: a jump landing exactly on -π with positive dd maps to +π.
            if (ddmod == low && dd > 0.0)
                ddmod = high;
            double correction = ddmod - dd;
            if (Math.Abs(dd) < high)   // |dd| < discont ⇒ no correction
                correction = 0.0;
            cumulative += correction;
            p[i] = current + cumulative;
            prevOriginal = current;
        }
    }

    /// <summary>
    /// Removes the least-squares linear trend over the integer index k = 0..N-1
    /// (slope·k + intercept), leaving the residual phase. Closed-form OLS; matches the
    /// twin's <c>numpy.polyfit(k, phase, 1)</c> subtraction.
    /// </summary>
    public static void DetrendLeastSquaresInPlace(Span<double> p)
    {
        int n = p.Length;
        if (n < 2)
            return;

        double meanK = (n - 1) / 2.0;
        double meanP = 0.0;
        for (int k = 0; k < n; k++)
            meanP += p[k];
        meanP /= n;

        double covKP = 0.0;
        double varK = 0.0;
        for (int k = 0; k < n; k++)
        {
            double dk = k - meanK;
            covKP += dk * (p[k] - meanP);
            varK += dk * dk;
        }

        double slope = varK > 0.0 ? covKP / varK : 0.0;
        double intercept = meanP - slope * meanK;

        for (int k = 0; k < n; k++)
            p[k] -= slope * k + intercept;
    }

    /// <summary>Floored modulo with a positive period (Python <c>%</c> semantics).</summary>
    private static double FlooredMod(double x, double period) =>
        x - period * Math.Floor(x / period);
}
