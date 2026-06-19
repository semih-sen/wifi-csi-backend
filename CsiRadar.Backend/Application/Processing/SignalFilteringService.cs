using CsiRadar.Backend.Core.Configuration;
using CsiRadar.Backend.Core.Entities;
using CsiRadar.Backend.Core.Interfaces;
using MathNet.Filtering;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CsiRadar.Backend.Application.Processing;

/// <summary>
/// Implements signal processing using Math.NET Numerics and Math.NET Filtering.
///
/// Pipeline (per sliding window):
///   1. I/Q Demodulation — converts raw interleaved int[] to float[] amplitudes
///      via sqrt(I² + Q²) for each subcarrier
///   2. Baseline Subtraction — removes the static room signature (calibration)
///   3. Low-pass Butterworth Filter — removes high-frequency thermal noise
///      using MathNet.Filtering.OnlineFilter (IIR Butterworth)
///   4. Returns the smoothed float[] ready for ONNX model input
///
/// Thread-safety: This service is registered as singleton. The Butterworth filter
/// instances are created per-subcarrier and reused across windows. The baseline
/// update uses Interlocked/volatile swap for lock-free thread safety.
/// </summary>
public sealed class SignalFilteringService : ISignalProcessor
{
    private readonly ProcessingOptions _options;
    private readonly ILogger<SignalFilteringService> _logger;

    // ── Butterworth filters (one per subcarrier, lazily initialized) ──
    // Each subcarrier's time-series is filtered independently.
    private OnlineFilter[]? _filters;
    private int _filterCount;

    // ── Baseline reference (volatile swap for thread-safe update) ──
    // _baseline[i] = average amplitude of subcarrier i in empty room.
    private volatile float[] _baseline = [];
    private volatile bool _isCalibrated;

    public SignalFilteringService(
        IOptions<ProcessingOptions> options,
        ILogger<SignalFilteringService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Whether a baseline calibration has been performed.
    /// </summary>
    public bool IsCalibrated => _isCalibrated;

    /// <inheritdoc />
    public float[] ProcessWindow(ReadOnlySpan<CsiData> window)
    {
        if (window.IsEmpty)
            return [];

        // ── Step 1: Determine subcarrier count from the first frame ──
        var firstFrame = window[0];
        int rawLen = Math.Min(firstFrame.RawDataLength, firstFrame.RawCsiData.Length);
        if (rawLen < 2)
            return [];

        int subcarrierCount = rawLen / 2; // I/Q pairs
        int windowSize = window.Length;

        // ── Step 2: Ensure Butterworth filters are initialized ──
        EnsureFiltersInitialized(subcarrierCount);

        // ── Step 3: Extract amplitude time-series per subcarrier ──
        // amplitudeMatrix[subcarrier][timeIndex] — row-major for filter processing
        var amplitudeMatrix = new double[subcarrierCount][];
        for (int sc = 0; sc < subcarrierCount; sc++)
            amplitudeMatrix[sc] = new double[windowSize];

        for (int t = 0; t < windowSize; t++)
        {
            var frame = window[t];
            int frameRawLen = Math.Min(frame.RawDataLength, frame.RawCsiData.Length);
            int frameSc = frameRawLen / 2;

            for (int sc = 0; sc < subcarrierCount; sc++)
            {
                if (sc < frameSc)
                {
                    // I/Q demodulation: amplitude = sqrt(imaginary² + real²)
                    int idx = sc * 2;
                    double imag = frame.RawCsiData[idx];
                    double real = frame.RawCsiData[idx + 1];
                    amplitudeMatrix[sc][t] = Math.Sqrt(imag * imag + real * real);
                }
                // else: zero-padded (default double = 0.0)
            }
        }

        // ── Step 4: Baseline subtraction (if calibrated) ──
        var baseline = _baseline; // volatile read
        if (_isCalibrated && baseline.Length == subcarrierCount)
        {
            for (int sc = 0; sc < subcarrierCount; sc++)
            {
                double baseVal = baseline[sc];
                for (int t = 0; t < windowSize; t++)
                    amplitudeMatrix[sc][t] -= baseVal;
            }
        }

        // ── Step 5: Apply Butterworth low-pass filter per subcarrier ──
        for (int sc = 0; sc < subcarrierCount; sc++)
        {
            amplitudeMatrix[sc] = _filters![sc].ProcessSamples(amplitudeMatrix[sc]);
        }

        // ── Step 6: Flatten to 1D array for model input ──
        // Layout: [sc0_t0, sc0_t1, ..., sc0_tN, sc1_t0, ..., scM_tN]
        var result = new float[subcarrierCount * windowSize];
        for (int sc = 0; sc < subcarrierCount; sc++)
        {
            int offset = sc * windowSize;
            for (int t = 0; t < windowSize; t++)
                result[offset + t] = (float)amplitudeMatrix[sc][t];
        }

        return result;
    }

    /// <inheritdoc />
    public void UpdateBaseline(ReadOnlySpan<CsiData> baselineFrames)
    {
        if (baselineFrames.IsEmpty)
        {
            _logger.LogWarning("Baseline update called with zero frames. Ignoring.");
            return;
        }

        // Determine subcarrier count from first frame
        var first = baselineFrames[0];
        int rawLen = Math.Min(first.RawDataLength, first.RawCsiData.Length);
        int subcarrierCount = rawLen / 2;

        if (subcarrierCount == 0)
        {
            _logger.LogWarning("Baseline frames contain no valid subcarrier data.");
            return;
        }

        // Average amplitude per subcarrier across all baseline frames
        var newBaseline = new float[subcarrierCount];
        var sumAmplitudes = new double[subcarrierCount];
        int validFrames = 0;

        for (int f = 0; f < baselineFrames.Length; f++)
        {
            var frame = baselineFrames[f];
            int frameRawLen = Math.Min(frame.RawDataLength, frame.RawCsiData.Length);
            int frameSc = frameRawLen / 2;

            if (frameSc < subcarrierCount)
                continue; // Skip incomplete frames

            validFrames++;
            for (int sc = 0; sc < subcarrierCount; sc++)
            {
                int idx = sc * 2;
                double imag = frame.RawCsiData[idx];
                double real = frame.RawCsiData[idx + 1];
                sumAmplitudes[sc] += Math.Sqrt(imag * imag + real * real);
            }
        }

        if (validFrames == 0)
        {
            _logger.LogWarning("No valid frames in baseline data.");
            return;
        }

        for (int sc = 0; sc < subcarrierCount; sc++)
            newBaseline[sc] = (float)(sumAmplitudes[sc] / validFrames);

        // Volatile swap — atomic reference assignment, no lock needed
        _baseline = newBaseline;
        _isCalibrated = true;

        _logger.LogInformation(
            "Baseline calibration updated: {SubcarrierCount} subcarriers, {FrameCount} frames averaged.",
            subcarrierCount, validFrames);
    }

    /// <summary>
    /// Lazily creates or recreates the per-subcarrier Butterworth filter array
    /// if the subcarrier count has changed.
    /// </summary>
    private void EnsureFiltersInitialized(int subcarrierCount)
    {
        if (_filters is not null && _filterCount == subcarrierCount)
            return;

        _filters = new OnlineFilter[subcarrierCount];
        for (int i = 0; i < subcarrierCount; i++)
        {
            _filters[i] = OnlineFilter.CreateLowpass(
                ImpulseResponse.Infinite,               // IIR Butterworth
                _options.SamplingRateHz,                 // 100 Hz
                _options.LowPassCutoffHz,                // 20 Hz
                _options.FilterOrder);                   // 4th order
        }

        _filterCount = subcarrierCount;

        _logger.LogInformation(
            "Initialized {Count} Butterworth IIR low-pass filters " +
            "(Fs={SampleRate} Hz, Fc={Cutoff} Hz, Order={Order}).",
            subcarrierCount, _options.SamplingRateHz,
            _options.LowPassCutoffHz, _options.FilterOrder);
    }
}
