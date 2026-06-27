using System.Text.Json.Serialization;

namespace CsiRadar.Backend.Core.Entities;

/// <summary>
/// Immutable snapshot of the recorder state, returned to the frontend over
/// SignalR (hub method return value and the "RecordingState" broadcast event).
/// </summary>
public sealed record RecordingStatus
{
    [JsonPropertyName("isRecording")]
    public bool IsRecording { get; init; }

    [JsonPropertyName("sessionId")]
    public long SessionId { get; init; }

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    /// <summary>
    /// Who performed the activity (e.g. the person walking). Empty when not
    /// applicable (e.g. EmptyRoom). Enables person/gait recognition downstream.
    /// </summary>
    [JsonPropertyName("subject")]
    public string Subject { get; init; } = string.Empty;

    [JsonPropertyName("framesCaptured")]
    public long FramesCaptured { get; init; }

    [JsonPropertyName("framesDropped")]
    public long FramesDropped { get; init; }

    [JsonPropertyName("startedAtUnixMs")]
    public long StartedAtUnixMs { get; init; }

    /// <summary>The canonical "not recording" value.</summary>
    public static readonly RecordingStatus Idle = new();
}

/// <summary>
/// Self-describing session metadata. Captured at <c>Start</c> from the active
/// processing configuration so each recording is fully reproducible offline:
/// the Python team can reconstruct the exact windowing and knows precisely how
/// the signal was filtered. Persisted into both the binary header and the JSON
/// manifest.
/// </summary>
public sealed class RecordingSessionInfo
{
    public long SessionId { get; init; }
    public string Label { get; init; } = string.Empty;

    /// <summary>
    /// Who performed the activity (the person walking, etc.). Empty when not
    /// applicable. Persisted into the binary header and the JSON manifest so the
    /// model team can train person/gait recognition, not just activity.
    /// </summary>
    public string Subject { get; init; } = string.Empty;

    public double SampleRateHz { get; init; }
    public double LowPassCutoffHz { get; init; }
    public int FilterOrder { get; init; }
    public int WindowSize { get; init; }
    public int SlideStep { get; init; }

    public bool CaptureRaw { get; init; }

    /// <summary>
    /// Whether baseline subtraction was active in the processor at session start
    /// (i.e. the recorded amplitudes are baseline-corrected). The model team must
    /// know this to keep training and inference consistent.
    /// </summary>
    public bool BaselineApplied { get; init; }

    public long StartedAtUnixMs { get; init; }
}