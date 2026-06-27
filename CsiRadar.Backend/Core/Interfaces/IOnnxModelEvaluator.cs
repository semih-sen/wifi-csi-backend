using CsiRadar.Backend.Core.Entities;

namespace CsiRadar.Backend.Core.Interfaces;

/// <summary>
/// Abstraction for the ONNX model evaluator.
/// Wraps ML.NET PredictionEnginePool for thread-safe inference
/// against the trained 1D-CNN / LSTM model.
/// </summary>
public interface IOnnxModelEvaluator
{
    /// <summary>
    /// True when a model and its label map are loaded and validated. When false the
    /// pipeline runs without inference (graph + recording only); callers must skip
    /// <see cref="Predict"/>.
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// The loaded class labels in output-channel order (empty when not ready).
    /// Surfaced by the server-info handshake so the frontend can self-configure.
    /// </summary>
    IReadOnlyList<string> Labels { get; }

    /// <summary>
    /// Runs inference on one exact-length, subcarrier-major filtered window
    /// (<see cref="Application.MachineLearning.OnnxInput.Length"/> floats) and
    /// returns the predicted activity label with per-class probabilities.
    /// </summary>
    /// <param name="window">
    /// The filtered window, exactly 64 × 100 floats in subcarrier-major order. The
    /// caller may hand a slice of a larger pooled buffer; the evaluator copies the
    /// exact length it needs and never retains the span.
    /// </param>
    /// <returns>Inference result containing the predicted label and scores.</returns>
    InferenceResult Predict(ReadOnlySpan<float> window);
}
