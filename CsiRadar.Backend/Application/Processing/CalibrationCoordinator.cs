namespace CsiRadar.Backend.Application.Processing;

/// <summary>
/// Thread-safe hand-off that lets any thread (a SignalR hub call, an HTTP endpoint,
/// a nightly timer) ask the processing consumer to (re)compute the empty-room
/// baseline (Phase 4 / Seam A data quality).
///
/// The request is just an atomic frame count. The consumer owns the actual capture:
/// it drains the next N raw frames and calls <c>ICsiStreamProcessor.UpdateBaseline</c>
/// on its own thread, so the filter state is only ever touched by one thread and no
/// lock is needed on the hot path.
/// </summary>
public sealed class CalibrationCoordinator
{
    /// <summary>Default capture size (~2 s at 100 Hz) when a caller doesn't specify one.</summary>
    public const int DefaultFrames = 200;

    private int _pendingFrames;

    /// <summary>Request a baseline calibration over the next <paramref name="frames"/> frames.</summary>
    public void Request(int frames)
        => Interlocked.Exchange(ref _pendingFrames, Math.Max(1, frames));

    /// <summary>
    /// Consumer-side: atomically take and clear any pending request. Returns the
    /// requested frame count, or 0 if none is pending.
    /// </summary>
    public int TakeRequest() => Interlocked.Exchange(ref _pendingFrames, 0);
}
