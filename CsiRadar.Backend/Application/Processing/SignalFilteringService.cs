using CsiRadar.Backend.Core.Entities;
using CsiRadar.Backend.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace CsiRadar.Backend.Application.Processing;

/// <summary>
/// Implements signal processing using Math.NET Numerics and Math.NET Filtering.
///
/// Pipeline:
///   1. Baseline Subtraction — removes the static room signature
///   2. Low-pass Butterworth Filter — removes high-frequency thermal noise
///   3. Amplitude normalization — prepares the signal for ONNX model input
///
/// Zero-allocation goal: Uses Span&lt;T&gt; and pre-allocated buffers where possible.
/// </summary>
public sealed class SignalFilteringService : ISignalProcessor
{
    private readonly ILogger<SignalFilteringService> _logger;

    // Pre-allocated baseline reference array (updated nightly or on-demand)
    private float[] _baseline = [];

    public SignalFilteringService(ILogger<SignalFilteringService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public float[] ProcessWindow(ReadOnlySpan<CsiData> window)
    {
        // TODO: Implement in Step 2
        // 1. Extract amplitude matrix from the window
        // 2. Subtract baseline
        // 3. Apply Butterworth low-pass filter via MathNet.Filtering
        // 4. Flatten/normalize for model input
        throw new NotImplementedException("Signal processing pipeline will be implemented in Step 2.");
    }

    /// <inheritdoc />
    public void UpdateBaseline(ReadOnlySpan<CsiData> baselineFrames)
    {
        // TODO: Implement in Step 2
        // Average the amplitudes across all baseline frames per subcarrier
        _logger.LogInformation("Baseline update requested with {FrameCount} frames.", baselineFrames.Length);
        throw new NotImplementedException("Baseline calibration will be implemented in Step 2.");
    }
}
