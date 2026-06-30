using CsiRadar.Backend.Core.Configuration;
using CsiRadar.Backend.Core.Entities;
using Microsoft.Extensions.Options;

namespace CsiRadar.Backend.Application.MachineLearning;

/// <summary>
/// Confirms a classification only after it has held for several consecutive windows
/// (Seam A / Phase 2). This is the debounce that turns noisy per-window predictions
/// into a stable automation signal.
///
/// It is deliberately DISTINCT from <c>BroadcastService._lastAutomationStatus</c>:
///   - this component decides WHEN a label has been observed long and confidently
///     enough to be considered "confirmed";
///   - the broadcast service then DEDUPES confirmed statuses before publishing to
///     MQTT, so an unchanged confirmation is not re-published.
///
/// Low-confidence predictions are already clamped to
/// <see cref="InferenceOptions.DefaultIdleLabel"/> upstream in
/// <see cref="OnnxModelEvaluator.Predict"/>, so a flurry of sub-threshold windows
/// confirms the resting state rather than latching the last confident label forever.
/// The threshold is re-applied here defensively in case a result reaches the
/// debouncer without having been clamped.
///
/// Stateful and NOT thread-safe — only the single processing consumer drives it.
/// </summary>
public sealed class InferenceDebouncer
{
    private readonly float _confidenceThreshold;
    private readonly string _defaultIdleLabel;
    private readonly int _consecutiveRequired;

    private string _candidate = string.Empty;
    private int _streak;
    private string _confirmed = string.Empty;

    public InferenceDebouncer(IOptions<InferenceOptions> options)
    {
        var cfg = options.Value;
        _confidenceThreshold = cfg.ConfidenceThreshold;
        _defaultIdleLabel = cfg.DefaultIdleLabel;
        _consecutiveRequired = Math.Max(1, cfg.ConsecutiveCountForAutomation);
    }

    /// <summary>
    /// Feeds one per-window inference result. Returns the newly confirmed status
    /// label when this window completes a fresh confirmation (a label held for the
    /// required consecutive count and different from the last confirmed label);
    /// returns <c>null</c> otherwise.
    /// </summary>
    public string? Push(InferenceResult result)
    {
        string label = result.Confidence >= _confidenceThreshold
            ? result.PredictedLabel
            : _defaultIdleLabel;

        if (label == _candidate)
        {
            _streak++;
        }
        else
        {
            _candidate = label;
            _streak = 1;
        }

        if (_streak >= _consecutiveRequired && _candidate != _confirmed)
        {
            _confirmed = _candidate;
            return _confirmed;
        }

        return null;
    }
}
