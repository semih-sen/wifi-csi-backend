using CsiRadar.Backend.Core.Entities;

namespace CsiRadar.Backend.Core.Interfaces;

/// <summary>
/// Backend-owned recording service. The frontend only issues start/stop/label
/// commands (via the SignalR hub); data is captured server-side directly from
/// the processing consumer and written to disk by a dedicated background writer.
///
/// Control commands (<see cref="Start"/>/<see cref="Stop"/>) are rare and must
/// never be lost. <see cref="Capture"/> is the hot path (≈100 Hz): it is a single
/// volatile read and an early return when idle, and a non-blocking enqueue when
/// recording — it never awaits disk I/O, so the inference-critical consumer loop
/// cannot be stalled by the recorder.
/// </summary>
public interface IRecordingService
{
    /// <summary>True while a session is active.</summary>
    bool IsRecording { get; }

    /// <summary>Current recorder state snapshot (for the frontend).</summary>
    RecordingStatus Status { get; }

    /// <summary>
    /// Begins a new labelled session. If a session is already active the call is
    /// ignored (the current status is returned). Returns the resulting status.
    /// </summary>
    RecordingStatus Start(string label);

    /// <summary>
    /// Finalizes the active session (the writer flushes, closes the payload file
    /// and emits the manifest). No-op if not recording. Returns the final status.
    /// </summary>
    RecordingStatus Stop();

    /// <summary>
    /// HOT PATH — invoked by the processing consumer for every filtered frame.
    /// Copies the frame and enqueues it for the writer when recording; a cheap
    /// no-op otherwise. The <paramref name="filtered"/> span is the consumer's
    /// reused buffer, so it is copied before enqueue.
    /// </summary>
    /// <param name="filtered">Per-frame filtered amplitudes (length = subcarrier count).</param>
    /// <param name="source">The source frame, for timestamp/RSSI and optional raw capture.</param>
    void Capture(ReadOnlySpan<float> filtered, CsiData source);
}