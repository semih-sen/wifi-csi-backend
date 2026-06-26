using CsiRadar.Backend.Core.Configuration;
using CsiRadar.Backend.Core.Entities;
using CsiRadar.Backend.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CsiRadar.Backend.Application.Processing;

/// <summary>
/// Per-frame CSI stream processor (replaces the old per-window
/// <c>SignalFilteringService</c>).
///
/// Pipeline (per frame, exactly once, in arrival order):
///   1. I/Q demodulation — amplitude = sqrt(imag² + real²) per subcarrier
///      (sbyte → int → float; no <c>double</c> promotion).
///   2. Baseline subtraction — removes the static room signature (per sample).
///   3. Butterworth IIR low-pass — applied <b>in place</b> as a cascade of
///      second-order sections (Direct Form II Transposed), zero allocation.
///
/// Because the IIR state advances by one sample per frame and is never reset on
/// window boundaries, overlapping windows are free and the frequency/phase
/// response can no longer be corrupted by re-feeding overlap regions.
///
/// Filter design: a Butterworth low-pass specified by (SamplingRateHz,
/// LowPassCutoffHz, FilterOrder), realized via the bilinear transform with
/// frequency pre-warping. Coefficients are computed analytically at construction
/// and validated against the textbook Butterworth magnitude response (unit DC
/// gain, −3 dB at the cutoff) by the test suite — not matched bit-for-bit
/// against any third-party library.
///
/// Threading: <see cref="ProcessFrame"/> mutates per-subcarrier filter state and
/// must be called by the single consumer thread only. <see cref="UpdateBaseline"/>
/// publishes a new baseline via a single volatile reference swap and is safe to
/// call concurrently.
/// </summary>
public sealed class CsiStreamProcessor : ICsiStreamProcessor
{
    private readonly ProcessingOptions _options;
    private readonly ILogger<CsiStreamProcessor> _logger;

    // ── Biquad cascade coefficients (shared across all subcarriers) ──
    // Same Fs/Fc/order ⇒ identical sections for every subcarrier.
    private readonly int _sectionCount;
    private readonly float[] _b0;
    private readonly float[] _b1;
    private readonly float[] _b2;
    private readonly float[] _a1;
    private readonly float[] _a2;

    // ── Per-(subcarrier, section) DF2T state registers, indexed [sc*_sectionCount + s] ──
    // Allocated once when the subcarrier count is discovered; never per-frame.
    private float[] _s1 = [];
    private float[] _s2 = [];
    private int _subcarrierCount;

    // ── Baseline reference (volatile swap for lock-free thread-safe update) ──
    private volatile float[] _baseline = [];
    private volatile bool _isCalibrated;

    public CsiStreamProcessor(
        IOptions<ProcessingOptions> options,
        ILogger<CsiStreamProcessor> logger)
    {
        _options = options.Value;
        _logger = logger;

        // Coefficients depend only on (Fs, Fc, order) — compute once.
        (_sectionCount, _b0, _b1, _b2, _a1, _a2) = ComputeButterworthCascade(
            _options.SamplingRateHz, _options.LowPassCutoffHz, _options.FilterOrder);

        _logger.LogInformation(
            "Butterworth low-pass cascade ready: {Sections} biquad section(s) " +
            "(Fs={SampleRate} Hz, Fc={Cutoff} Hz, Order={Order}).",
            _sectionCount, _options.SamplingRateHz,
            _options.LowPassCutoffHz, _options.FilterOrder);
    }

    /// <inheritdoc />
    public int SubcarrierCount => _subcarrierCount;

    /// <inheritdoc />
    public bool IsCalibrated => _isCalibrated;

    /// <inheritdoc />
    public int ProcessFrame(CsiData frame, Span<float> destination)
    {
        int rawLen = Math.Min(frame.RawDataLength, frame.RawCsiData.Length);
        if (rawLen < 2)
            return 0;

        int subcarrierCount = rawLen / 2; // interleaved I/Q pairs

        // Discover / resize per-subcarrier filter state on first frame (or width change).
        EnsureStateSized(subcarrierCount);

        if (destination.Length < subcarrierCount)
            throw new ArgumentException(
                $"Destination span ({destination.Length}) is smaller than the subcarrier " +
                $"count ({subcarrierCount}).", nameof(destination));

        var raw = frame.RawCsiData;
        var baseline = _baseline; // single volatile read → stable snapshot
        bool useBaseline = _isCalibrated && baseline.Length == subcarrierCount;

        Span<float> amp = destination[..subcarrierCount];

        // ── Step 1+2: demodulate, then baseline-subtract per sample ──
        for (int i = 0; i < subcarrierCount; i++)
        {
            int idx = i * 2;
            int imag = raw[idx];      // sbyte promotes to int (−128..127)
            int real = raw[idx + 1];
            float a = MathF.Sqrt(imag * imag + real * real);
            if (useBaseline)
                a -= baseline[i];
            amp[i] = a;
        }

        // ── Step 3: IIR low-pass in place (advances state by one sample) ──
        FilterInPlace(amp, subcarrierCount);

        return subcarrierCount;
    }

    /// <inheritdoc />
    public void UpdateBaseline(ReadOnlySpan<CsiData> baselineFrames)
    {
        if (baselineFrames.IsEmpty)
        {
            _logger.LogWarning("Baseline update called with zero frames. Ignoring.");
            return;
        }

        var first = baselineFrames[0];
        int rawLen = Math.Min(first.RawDataLength, first.RawCsiData.Length);
        int subcarrierCount = rawLen / 2;

        if (subcarrierCount == 0)
        {
            _logger.LogWarning("Baseline frames contain no valid subcarrier data.");
            return;
        }

        var newBaseline = new float[subcarrierCount];
        var sumAmplitudes = new double[subcarrierCount];
        int validFrames = 0;

        for (int f = 0; f < baselineFrames.Length; f++)
        {
            var frame = baselineFrames[f];
            int frameRawLen = Math.Min(frame.RawDataLength, frame.RawCsiData.Length);
            int frameSc = frameRawLen / 2;
            if (frameSc < subcarrierCount)
                continue; // skip incomplete frames

            validFrames++;
            var raw = frame.RawCsiData;
            for (int sc = 0; sc < subcarrierCount; sc++)
            {
                int idx = sc * 2;
                int imag = raw[idx];
                int real = raw[idx + 1];
                sumAmplitudes[sc] += Math.Sqrt((double)(imag * imag + real * real));
            }
        }

        if (validFrames == 0)
        {
            _logger.LogWarning("No valid frames in baseline data.");
            return;
        }

        for (int sc = 0; sc < subcarrierCount; sc++)
            newBaseline[sc] = (float)(sumAmplitudes[sc] / validFrames);

        // Volatile swap — atomic reference assignment, no lock needed.
        _baseline = newBaseline;
        _isCalibrated = true;

        _logger.LogInformation(
            "Baseline calibration updated: {SubcarrierCount} subcarriers, {FrameCount} frames averaged.",
            subcarrierCount, validFrames);
    }

    /// <summary>
    /// In-place biquad cascade (Direct Form II Transposed) over one frame's
    /// amplitude vector. State registers carry across frames (the IIR memory).
    /// </summary>
    private void FilterInPlace(Span<float> amp, int subcarrierCount)
    {
        int sections = _sectionCount;
        float[] s1 = _s1, s2 = _s2;
        float[] b0 = _b0, b1 = _b1, b2 = _b2, a1 = _a1, a2 = _a2;

        for (int i = 0; i < subcarrierCount; i++)
        {
            float x = amp[i];
            int baseIdx = i * sections;
            for (int s = 0; s < sections; s++)
            {
                int j = baseIdx + s;
                float y = b0[s] * x + s1[j];
                s1[j] = b1[s] * x - a1[s] * y + s2[j];
                s2[j] = b2[s] * x - a2[s] * y;
                x = y; // output of this section is the input of the next
            }
            amp[i] = x;
        }
    }

    /// <summary>
    /// Allocates / reallocates the per-subcarrier state registers when the
    /// subcarrier count is first discovered or changes mid-stream. A width
    /// change resets all filter state (treated as a stream restart by the
    /// consumer). Consumer-thread-only.
    /// </summary>
    private void EnsureStateSized(int subcarrierCount)
    {
        if (_subcarrierCount == subcarrierCount && _s1.Length != 0)
            return;

        int len = subcarrierCount * _sectionCount;
        _s1 = new float[len];
        _s2 = new float[len];
        _subcarrierCount = subcarrierCount;

        _logger.LogInformation(
            "Filter state sized for {SubcarrierCount} subcarriers ({Sections} sections).",
            subcarrierCount, _sectionCount);
    }

    /// <summary>
    /// Designs a Butterworth low-pass as a cascade of second-order sections via
    /// the bilinear transform with cutoff pre-warping. Returns per-section
    /// coefficients with the sign convention
    /// <c>y = b0·x + b1·x[-1] + b2·x[-2] − a1·y[-1] − a2·y[-2]</c>.
    /// Computed in <c>double</c> for accuracy, stored as <c>float</c>.
    ///
    /// Odd filter orders are rounded up to the next even order so the realization
    /// is a uniform biquad cascade (the configured default is 4).
    /// </summary>
    internal static (int sectionCount, float[] b0, float[] b1, float[] b2, float[] a1, float[] a2)
        ComputeButterworthCascade(double samplingRateHz, double cutoffHz, int filterOrder)
    {
        int order = filterOrder < 2 ? 2 : filterOrder;
        if (order % 2 != 0)
            order++; // even order ⇒ all second-order sections

        int sections = order / 2;

        // Pre-warped, normalized cutoff. K = tan(π·Fc/Fs).
        double k = Math.Tan(Math.PI * cutoffHz / samplingRateHz);
        double k2 = k * k;

        var b0 = new float[sections];
        var b1 = new float[sections];
        var b2 = new float[sections];
        var a1 = new float[sections];
        var a2 = new float[sections];

        for (int s = 0; s < sections; s++)
        {
            // Butterworth pole angle from the negative real axis for section s.
            // θ_s = π·(2s + 1) / (2·order)  ⇒  Q_s = 1 / (2·cos θ_s).
            double theta = Math.PI * (2 * s + 1) / (2.0 * order);
            double q = 1.0 / (2.0 * Math.Cos(theta));

            double norm = 1.0 / (1.0 + k / q + k2);
            b0[s] = (float)(k2 * norm);
            b1[s] = (float)(2.0 * k2 * norm);
            b2[s] = (float)(k2 * norm);
            a1[s] = (float)(2.0 * (k2 - 1.0) * norm);
            a2[s] = (float)((1.0 - k / q + k2) * norm);
        }

        return (sections, b0, b1, b2, a1, a2);
    }
}
