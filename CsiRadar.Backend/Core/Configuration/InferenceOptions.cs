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
    /// Results below this are reported as "Unknown".
    /// </summary>
    public float ConfidenceThreshold { get; set; } = 0.7f;

    /// <summary>
    /// Number of consecutive identical predictions required before triggering
    /// a Home Assistant automation (debounce). At 100 Hz with 100-frame windows,
    /// a value of 3 means ~3 seconds of sustained detection.
    /// </summary>
    public int ConsecutiveCountForAutomation { get; set; } = 3;
}
