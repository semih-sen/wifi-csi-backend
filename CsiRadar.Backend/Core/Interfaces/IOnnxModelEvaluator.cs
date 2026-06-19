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
    /// Runs inference on a processed (filtered) signal window
    /// and returns the predicted activity label with confidence scores.
    /// </summary>
    /// <param name="processedSignal">
    /// The cleaned signal array from the processing pipeline.
    /// </param>
    /// <returns>Inference result containing the predicted label and scores.</returns>
    InferenceResult Predict(float[] processedSignal);
}
