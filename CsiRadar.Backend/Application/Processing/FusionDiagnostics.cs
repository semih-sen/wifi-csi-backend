using CsiRadar.Backend.Application.Processing.Windowing;

namespace CsiRadar.Backend.Application.Processing;

/// <summary>
/// Lock-free observability counters for the Phase 3 fusion + windowing stage, surfaced at
/// <c>/health</c>. Proves the exit criteria: fused windows are emitted on cadence, windows
/// that would span a Phase 1 alignment gap are rejected (never silently stitched) and
/// counted — the recording-integrity signal — and the last window's shape is visible so a
/// silent layout/geometry drift is caught without a debugger.
/// </summary>
public sealed class FusionDiagnostics
{
    private long _framesConsumed;        // aligned-DSP frames fed into the assembler
    private long _windowsEmitted;        // fused windows written to the Phase 4 seam
    private long _windowsRejectedForGap; // scheduled windows skipped because a gap left the buffer short
    private long _gapEvents;             // seqNo discontinuities observed (alignment drops)
    private long _lastWindowStartSeq;    // gauge: seqNo of the last emitted window's first frame
    private long _lastWindowEndSeq;      // gauge: seqNo of the last emitted window's last frame

    public void IncFramesConsumed() => Interlocked.Increment(ref _framesConsumed);
    public void IncWindowsEmitted() => Interlocked.Increment(ref _windowsEmitted);
    public void IncWindowsRejectedForGap() => Interlocked.Increment(ref _windowsRejectedForGap);
    public void IncGapEvents() => Interlocked.Increment(ref _gapEvents);

    public void SetLastWindow(uint startSeq, uint endSeq)
    {
        Interlocked.Exchange(ref _lastWindowStartSeq, startSeq);
        Interlocked.Exchange(ref _lastWindowEndSeq, endSeq);
    }

    public FusionSnapshot Snapshot() => new()
    {
        FramesConsumed = Interlocked.Read(ref _framesConsumed),
        WindowsEmitted = Interlocked.Read(ref _windowsEmitted),
        WindowsRejectedForGap = Interlocked.Read(ref _windowsRejectedForGap),
        GapEvents = Interlocked.Read(ref _gapEvents),
        LastWindowStartSeq = Interlocked.Read(ref _lastWindowStartSeq),
        LastWindowEndSeq = Interlocked.Read(ref _lastWindowEndSeq),
        // The pinned tensor shapes — constant, but surfaced so /health documents exactly
        // what the model input looks like and any contract change is visible live.
        WindowFrames = WindowContract.WindowFrames,
        WindowSlide = WindowContract.WindowSlide,
        DenseShape = WindowContract.DenseShape,
        DopplerShape = WindowContract.DopplerShape,
    };
}

/// <summary>Immutable <c>/health</c> view of <see cref="FusionDiagnostics"/>.</summary>
public sealed class FusionSnapshot
{
    public long FramesConsumed { get; init; }
    public long WindowsEmitted { get; init; }
    public long WindowsRejectedForGap { get; init; }
    public long GapEvents { get; init; }
    public long LastWindowStartSeq { get; init; }
    public long LastWindowEndSeq { get; init; }
    public int WindowFrames { get; init; }
    public int WindowSlide { get; init; }
    public string DenseShape { get; init; } = "";
    public string DopplerShape { get; init; } = "";
}
