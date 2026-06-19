using CsiRadar.Backend.Core.Entities;

namespace CsiRadar.Backend.Core.Interfaces;

/// <summary>
/// Abstraction for the signal processing pipeline.
/// Responsible for:
///   1. Baseline subtraction (static room reference removal)
///   2. Low-pass / Butterworth filtering via Math.NET Numerics
///   3. Preparing the cleaned signal window for ML inference
/// </summary>
public interface ISignalProcessor
{
    /// <summary>
    /// Applies baseline subtraction and filtering to a window of CSI frames.
    /// Returns the cleaned amplitude array ready for ONNX inference.
    /// </summary>
    /// <param name="window">Sliding window of raw CSI data frames.</param>
    /// <returns>Filtered/cleaned signal array suitable for model input.</returns>
    float[] ProcessWindow(ReadOnlySpan<CsiData> window);

    /// <summary>
    /// Updates the static baseline reference (calibration data).
    /// Should be called periodically (e.g., nightly) or on demand
    /// when the room is known to be empty.
    /// </summary>
    /// <param name="baselineFrames">CSI frames captured in an empty room.</param>
    void UpdateBaseline(ReadOnlySpan<CsiData> baselineFrames);
}
