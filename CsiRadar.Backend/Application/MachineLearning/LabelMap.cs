using System.Text.Json;
using CsiRadar.Backend.Core.Entities;

namespace CsiRadar.Backend.Application.MachineLearning;

/// <summary>
/// The output-index → class-name map for the loaded ONNX model (Seam A).
///
/// This is the single nastiest failure mode in the system: if the label order here
/// disagrees with the order the model was trained against, the model runs perfectly
/// and every prediction is silently mislabeled. The order is pinned end-to-end on
/// the ML side (dataset class order → checkpoint["classes"] → export_onnx →
/// labels.json) and this loader is the backend half of that contract.
///
/// It is deliberately fail-fast: a missing/empty/garbled labels.json or a length
/// that disagrees with the model's output dimension throws at startup / first
/// prediction rather than mislabeling at runtime.
/// </summary>
public sealed class LabelMap
{
    public IReadOnlyList<string> Labels { get; }

    private LabelMap(IReadOnlyList<string> labels) => Labels = labels;

    /// <summary>Number of classes (== the model's expected output dimension).</summary>
    public int Count => Labels.Count;

    /// <summary>
    /// Loads and validates <paramref name="path"/> (a JSON array of class names in
    /// output-channel order). Throws <see cref="InvalidOperationException"/> if the
    /// file is missing, empty, malformed, or contains duplicate/blank labels.
    /// </summary>
    public static LabelMap Load(string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"Label map '{path}' not found. It must ship alongside the ONNX model " +
                "(copy wifi-csi-ml/artifacts/labels.json). Inference cannot map output " +
                "indices to class names without it.");

        string[]? labels;
        try
        {
            labels = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Label map '{path}' is not a valid JSON array of strings.", ex);
        }

        if (labels is null || labels.Length == 0)
            throw new InvalidOperationException($"Label map '{path}' is empty.");

        if (labels.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException($"Label map '{path}' contains a blank label.");

        if (labels.Distinct(StringComparer.Ordinal).Count() != labels.Length)
            throw new InvalidOperationException($"Label map '{path}' contains duplicate labels.");

        return new LabelMap(labels);
    }

    /// <summary>
    /// Asserts the model's output dimension matches the label count. Call once the
    /// first <see cref="OnnxOutput"/> is available — a mismatch means the wrong
    /// labels.json shipped with this model, which would mislabel every prediction.
    /// </summary>
    public void AssertMatchesModelOutput(int outputDimension)
    {
        if (outputDimension != Count)
            throw new InvalidOperationException(
                $"Model output dimension ({outputDimension}) != label count ({Count}). " +
                "labels.json does not match this model — a silent label-order swap would " +
                "mislabel every prediction. Re-export both from the same checkpoint.");
    }

    /// <summary>
    /// Maps an ONNX probability vector to an <see cref="InferenceResult"/>: argmax →
    /// label, full {label: prob} score map, and the top confidence.
    /// </summary>
    public InferenceResult Map(ReadOnlySpan<float> probabilities, long timestampTicks)
    {
        AssertMatchesModelOutput(probabilities.Length);

        int argmax = 0;
        float best = probabilities[0];
        var scores = new Dictionary<string, float>(Count);
        for (int i = 0; i < probabilities.Length; i++)
        {
            scores[Labels[i]] = probabilities[i];
            if (probabilities[i] > best)
            {
                best = probabilities[i];
                argmax = i;
            }
        }

        return new InferenceResult
        {
            PredictedLabel = Labels[argmax],
            Confidence = best,
            Scores = scores,
            TimestampTicks = timestampTicks,
        };
    }
}
