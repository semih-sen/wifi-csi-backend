namespace CsiRadar.Backend.Core.Configuration;

/// <summary>
/// Configuration options for the ONNX model inference pipeline.
/// Bound from appsettings.json section "Inference".
/// </summary>
public sealed class InferenceOptions
{
    public const string SectionName = "Inference";

    /// <summary>
    /// File path to the trained .onnx model. If the file does not exist at startup,
    /// inference is disabled (the graph/recording pipeline still runs) — see
    /// <see cref="Application.MachineLearning.OnnxModelEvaluator"/>.
    /// </summary>
    public string ModelPath { get; set; } = "Models/model.onnx";

    /// <summary>
    /// File path to the label map (labels.json) that ships with the model. The
    /// output-channel order in this file is the contract that maps an argmax index
    /// to a class name; it MUST come from the same checkpoint as the model.
    /// </summary>
    public string LabelsPath { get; set; } = "Models/labels.json";

    /// <summary>
    /// Minimum confidence threshold for a prediction to be considered valid.
    /// When the top confidence is strictly below this, the primary label is clamped
    /// to <see cref="DefaultIdleLabel"/> (both for the UI broadcast and the
    /// automation debounce) instead of reporting a flickery low-confidence class.
    /// </summary>
    public float ConfidenceThreshold { get; set; } = 0.7f;

    /// <summary>
    /// The resting-state label used whenever the top prediction's confidence is
    /// below <see cref="ConfidenceThreshold"/>. This keeps the UI from flickering
    /// with low-confidence labels (e.g. "60% Walking") and gives the debouncer a
    /// real idle class to settle on rather than a synthetic "Unknown".
    /// Must be one of the labels in labels.json for the score map to stay consistent.
    /// </summary>
    public string DefaultIdleLabel { get; set; } = "EmptyRoom";

    /// <summary>
    /// Number of consecutive identical predictions required before triggering
    /// a Home Assistant automation (debounce). At 100 Hz with 100-frame windows,
    /// a value of 3 means ~3 seconds of sustained detection.
    /// </summary>
    public int ConsecutiveCountForAutomation { get; set; } = 3;
}
