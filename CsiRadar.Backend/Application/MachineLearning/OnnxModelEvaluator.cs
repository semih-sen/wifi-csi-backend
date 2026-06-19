using CsiRadar.Backend.Core.Entities;
using CsiRadar.Backend.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace CsiRadar.Backend.Application.MachineLearning;

/// <summary>
/// Wraps the ML.NET PredictionEnginePool to perform thread-safe ONNX inference.
///
/// The trained .onnx model (1D-CNN or LSTM) is loaded at startup.
/// Each prediction takes a filtered signal window and produces
/// a classification label (EmptyRoom, Walking, LyingOnCouch, CatPassed, etc.).
///
/// CRITICAL: PredictionEngine is NOT thread-safe.
/// This service uses PredictionEnginePool via Microsoft.Extensions.ML
/// to handle concurrent inference requests safely.
/// </summary>
public sealed class OnnxModelEvaluator : IOnnxModelEvaluator
{
    private readonly ILogger<OnnxModelEvaluator> _logger;

    public OnnxModelEvaluator(ILogger<OnnxModelEvaluator> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public InferenceResult Predict(float[] processedSignal)
    {
        // TODO: Implement in Step 3
        // 1. Wrap processedSignal into the ML.NET input schema
        // 2. Call PredictionEnginePool.Predict()
        // 3. Map ONNX output to InferenceResult
        _logger.LogDebug("Inference requested for signal of length {Length}.", processedSignal.Length);
        throw new NotImplementedException("ONNX inference will be implemented in Step 3.");
    }
}
