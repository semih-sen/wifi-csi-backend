namespace CsiRadar.Backend.Application.Processing;

/// <summary>
/// Lock-free observability counters for the Phase 2 per-RX DSP stage, surfaced at
/// <c>/health</c>. Proves the "observable layer" exit criterion: amplitude + sanitized
/// phase are emitted every aligned frame, and Doppler columns appear on hop
/// boundaries — per RX, so a stalled modality is visible without a debugger.
/// </summary>
public sealed class DspDiagnostics
{
    private readonly long _startedTicks = DateTime.UtcNow.Ticks;

    private long _framesProcessed;      // aligned frames that produced per-RX modalities
    private long _rx0DopplerColumns;    // STFT columns emitted for RX0
    private long _rx1DopplerColumns;    // STFT columns emitted for RX1
    private long _subcarrierMismatch;   // frames whose subcarrier count != contract
    private long _lastSubcarriers;      // gauge: subcarriers seen on the last frame

    public void IncFramesProcessed() => Interlocked.Increment(ref _framesProcessed);
    public void IncRx0DopplerColumns() => Interlocked.Increment(ref _rx0DopplerColumns);
    public void IncRx1DopplerColumns() => Interlocked.Increment(ref _rx1DopplerColumns);
    public void IncSubcarrierMismatch() => Interlocked.Increment(ref _subcarrierMismatch);
    public void SetLastSubcarriers(int n) => Interlocked.Exchange(ref _lastSubcarriers, n);

    public DspSnapshot Snapshot()
    {
        double elapsed = Math.Max(
            (DateTime.UtcNow.Ticks - _startedTicks) / (double)TimeSpan.TicksPerSecond, 1e-3);
        long frames = Interlocked.Read(ref _framesProcessed);

        return new DspSnapshot
        {
            FramesProcessed = frames,
            FrameRateHz = frames / elapsed,
            Rx0DopplerColumns = Interlocked.Read(ref _rx0DopplerColumns),
            Rx1DopplerColumns = Interlocked.Read(ref _rx1DopplerColumns),
            SubcarrierMismatch = Interlocked.Read(ref _subcarrierMismatch),
            LastSubcarriers = Interlocked.Read(ref _lastSubcarriers),
        };
    }
}

/// <summary>Immutable <c>/health</c> view of <see cref="DspDiagnostics"/>.</summary>
public sealed class DspSnapshot
{
    public long FramesProcessed { get; init; }
    public double FrameRateHz { get; init; }
    public long Rx0DopplerColumns { get; init; }
    public long Rx1DopplerColumns { get; init; }
    public long SubcarrierMismatch { get; init; }
    public long LastSubcarriers { get; init; }
}
