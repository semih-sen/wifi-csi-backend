namespace CsiRadar.Backend.Application.Processing;

/// <summary>
/// Thread-safe hand-off + shared state for the empty-room baseline calibration
/// ("tare"). Any thread (a SignalR hub call, an HTTP endpoint, a nightly timer) can
/// <see cref="Request"/> a calibration; the processing consumer owns the actual
/// capture — it drains the next N raw frames and calls
/// <c>ICsiStreamProcessor.UpdateBaseline</c> on its own thread, so the filter state
/// (and the baseline it gates) is only ever touched by one thread, lock-free.
///
/// This type is also the single source of truth for calibration STATE
/// (<see cref="IsCalibrating"/> / <see cref="IsBaselineActive"/> / <see cref="LastFailed"/>).
/// <see cref="StateChanged"/> lets a broadcaster push the state to the UI without the
/// consumer ever touching the network.
///
/// IMPORTANT — failure handling: calibration can only complete if CSI frames are
/// actually flowing (the consumer averages them). If none arrive, the request would
/// otherwise hang forever AND fire unexpectedly whenever the sensor later reconnects.
/// So a request arms a <see cref="RequestTimeout"/>; on expiry the request is cleared,
/// the state goes to "failed", and listeners are notified — the UI fails fast.
/// </summary>
public sealed class CalibrationCoordinator : IDisposable
{
    /// <summary>Default capture size (~5 s at 100 Hz) when a caller doesn't specify one.</summary>
    public const int DefaultFrames = 500;

    /// <summary>
    /// Max time a calibration may stay pending/in-progress before it is aborted.
    /// Re-armed when capture actually begins, so it bounds "waiting for frames" and
    /// "capturing" independently.
    /// </summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private int _pendingFrames;                 // hot path: Interlocked only
    private volatile bool _isCalibrating;
    private volatile bool _baselineActive;
    private volatile bool _lastFailed;
    private volatile int _framesRequested;
    private Timer? _timeoutTimer;               // guarded by _gate

    /// <summary>Raised whenever the calibration state changes (start / finish / fail).</summary>
    public event Action? StateChanged;

    /// <summary>True while a calibration request is pending or capturing.</summary>
    public bool IsCalibrating => _isCalibrating;

    /// <summary>True once a baseline has been captured and is being subtracted live.</summary>
    public bool IsBaselineActive => _baselineActive;

    /// <summary>True if the most recent calibration attempt failed (e.g. no frames).</summary>
    public bool LastFailed => _lastFailed;

    /// <summary>Frame count of the most recent calibration request (for UI display).</summary>
    public int FramesRequested => _framesRequested;

    /// <summary>
    /// Request a baseline calibration over the next <paramref name="frames"/> frames.
    /// Enters the calibrating state immediately and arms the abort timeout.
    /// </summary>
    public void Request(int frames)
    {
        frames = Math.Max(1, frames);
        Interlocked.Exchange(ref _pendingFrames, frames);
        _framesRequested = frames;
        _lastFailed = false;
        _isCalibrating = true;
        lock (_gate)
            ArmTimeoutLocked();
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Consumer-side hot path: atomically take and clear any pending request. Returns
    /// the requested frame count, or 0 if none is pending. No lock — runs per frame.
    /// </summary>
    public int TakeRequest() => Interlocked.Exchange(ref _pendingFrames, 0);

    /// <summary>Consumer-side: capture actually began — re-arm the timeout for the capture window.</summary>
    public void NotifyStarted(int frames)
    {
        _framesRequested = frames;
        _isCalibrating = true;
        lock (_gate)
            ArmTimeoutLocked();
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Consumer-side: capture finished. <paramref name="baselineSet"/> reflects whether
    /// a usable baseline was actually computed (false → treated as a failure).
    /// </summary>
    public void NotifyCompleted(bool baselineSet)
    {
        _isCalibrating = false;
        _lastFailed = !baselineSet;
        if (baselineSet)
            _baselineActive = true;
        lock (_gate)
            DisposeTimerLocked();
        StateChanged?.Invoke();
    }

    private void ArmTimeoutLocked()
    {
        DisposeTimerLocked();
        _timeoutTimer = new Timer(_ => OnTimeout(), null, RequestTimeout, Timeout.InfiniteTimeSpan);
    }

    private void DisposeTimerLocked()
    {
        _timeoutTimer?.Dispose();
        _timeoutTimer = null;
    }

    // Abort a calibration that never received enough frames. Clears the pending
    // request so it cannot fire later when the sensor reconnects.
    private void OnTimeout()
    {
        bool fire = false;
        lock (_gate)
        {
            if (_isCalibrating)
            {
                _isCalibrating = false;
                _lastFailed = true;
                Interlocked.Exchange(ref _pendingFrames, 0);
                fire = true;
            }
            DisposeTimerLocked();
        }
        if (fire)
            StateChanged?.Invoke();
    }

    public void Dispose()
    {
        lock (_gate)
            DisposeTimerLocked();
    }
}
