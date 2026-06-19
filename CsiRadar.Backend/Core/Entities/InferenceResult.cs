namespace CsiRadar.Backend.Core.Entities;

/// <summary>
/// Represents the output of the ONNX inference model.
/// Contains the predicted activity label and confidence scores.
/// </summary>
public sealed class InferenceResult
{
    /// <summary>
    /// The predicted activity label (e.g., EmptyRoom, Walking, LyingOnCouch, CatPassed).
    /// </summary>
    public string PredictedLabel { get; set; } = string.Empty;

    /// <summary>
    /// Confidence score (probability) for each possible label.
    /// Key: label name, Value: confidence (0.0 - 1.0).
    /// </summary>
    public Dictionary<string, float> Scores { get; set; } = [];

    /// <summary>
    /// The highest confidence score from the prediction.
    /// </summary>
    public float Confidence { get; set; }

    /// <summary>
    /// UTC timestamp (ticks) when the inference was performed.
    /// </summary>
    public long TimestampTicks { get; set; }
}
