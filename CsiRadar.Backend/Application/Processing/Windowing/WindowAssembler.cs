using CsiRadar.Backend.Application.Processing.Dsp;
using CsiRadar.Backend.Core.Entities;

namespace CsiRadar.Backend.Application.Processing.Windowing;

/// <summary>
/// Pure, single-threaded windowing + fusion state machine (Phase 3). Fed one
/// <see cref="AlignedDspFrame"/> at a time in stream order; emits a fused
/// <see cref="FusedWindow"/> every <see cref="WindowContract.WindowSlide"/> frames once a
/// full <see cref="WindowContract.WindowFrames"/>-frame window of <b>contiguous</b>
/// frames is available.
///
/// <b>Continuity/integrity.</b> A frame gap corrupts the Doppler map, so this stage
/// carries the Phase 1 alignment-drop signal into the window: a discontinuity in the
/// shared <c>seqNo</c> (paired frames arrive with monotonic, normally consecutive seqNos;
/// a jump means alignment dropped the intervening frames) resets the contiguous history.
/// A window is only ever built from <see cref="WindowContract.WindowFrames"/> gap-free
/// frames, so <b>no window is ever silently stitched across a gap</b>. A scheduled
/// emission that falls due while the buffer is still refilling after a gap is reported as
/// a gap-rejection, not emitted.
///
/// No locking: the fusion background service is the sole caller, on one thread (same
/// contract as the DSP stage's STFT rings).
/// </summary>
public sealed class WindowAssembler
{
    /// <summary>Outcome of feeding one frame — at most one emission or one rejection per call.</summary>
    public readonly record struct Result(
        FusedWindow? Emitted,
        bool GapDetected,
        bool EmissionRejectedForGap);

    private readonly AlignedDspFrame?[] _ring = new AlignedDspFrame?[WindowContract.WindowFrames];
    private int _writePos;      // next slot to write (== oldest once the ring is full)
    private int _contiguous;    // contiguous frames currently buffered (0..WindowFrames)

    private bool _hasPrev;
    private uint _prevSeq;

    private bool _warmed;       // have we ever reached a first full window?
    private int _slideCountdown; // frames until the next scheduled emission attempt

    // Reused scratch (single-threaded), so building a window allocates only its outputs.
    private readonly double[] _series = new double[WindowContract.WindowFrames];
    private readonly float[] _binScratch = new float[WindowContract.StftBins];

    /// <summary>
    /// Feeds one aligned-DSP frame. Detects a seqNo gap (dropping the contiguous history
    /// so the next window can't span it), appends the frame, and — once warmed — attempts
    /// a scheduled emission every <see cref="WindowContract.WindowSlide"/> frames.
    /// </summary>
    public Result Accept(AlignedDspFrame frame)
    {
        bool gap = false;
        if (_hasPrev && frame.SeqNo != unchecked(_prevSeq + 1))
        {
            // A jump (or non-monotonic seqNo) means the alignment stage dropped the
            // frames in between: the Doppler continuity is broken. Discard the partial
            // window so nothing is stitched across the gap; refill starts at this frame.
            gap = true;
            _contiguous = 0;
        }
        _hasPrev = true;
        _prevSeq = frame.SeqNo;

        _ring[_writePos] = frame;
        _writePos = (_writePos + 1) % WindowContract.WindowFrames;
        if (_contiguous < WindowContract.WindowFrames)
            _contiguous++;

        FusedWindow? emitted = null;
        bool rejected = false;

        if (!_warmed)
        {
            // Warmup: emit exactly when the first full contiguous window is available,
            // then start the fixed slide schedule.
            if (_contiguous == WindowContract.WindowFrames)
            {
                _warmed = true;
                emitted = BuildWindow();
                _slideCountdown = WindowContract.WindowSlide;
            }
        }
        else if (--_slideCountdown <= 0)
        {
            _slideCountdown = WindowContract.WindowSlide;
            if (_contiguous == WindowContract.WindowFrames)
                emitted = BuildWindow();
            else
                // Due to emit, but a recent gap left the buffer short of a full contiguous
                // window — reject this scheduled window (recording-integrity signal).
                rejected = true;
        }

        return new Result(emitted, gap, rejected);
    }

    /// <summary>
    /// Fuses the most recent <see cref="WindowContract.WindowFrames"/> (contiguous) frames
    /// into the pinned dense + Doppler tensors. Reads the ring oldest→newest.
    /// </summary>
    private FusedWindow BuildWindow()
    {
        int wf = WindowContract.WindowFrames;
        int sc = WindowContract.Subcarriers;

        var dense = new float[WindowContract.DenseLength];
        var doppler = new float[WindowContract.DopplerLength];

        // Snapshot the window's frames in time order (oldest at _writePos once full).
        var frames = new AlignedDspFrame[wf];
        for (int t = 0; t < wf; t++)
            frames[t] = _ring[(_writePos + t) % wf]!;

        // ── Dense: [rx, modality, frame, subcarrier] ──
        for (int t = 0; t < wf; t++)
        {
            CopyModalities(frames[t].Rx0, 0, t, dense, sc);
            CopyModalities(frames[t].Rx1, 1, t, dense, sc);
        }

        // ── Doppler: [rx, subcarrier, stftFrame, bin] ──
        // Per RX per subcarrier: STFT the amplitude series over the window (identical
        // geometry + primitive as the Phase 2 streaming STFT, so runtime and twin agree).
        for (int rx = 0; rx < WindowContract.RxCount; rx++)
            for (int k = 0; k < sc; k++)
                FillDopplerColumn(frames, rx, k, doppler);

        return new FusedWindow
        {
            StartSeqNo = frames[0].SeqNo,
            EndSeqNo = frames[wf - 1].SeqNo,
            Dense = dense,
            Doppler = doppler,
        };
    }

    private static void CopyModalities(RxDsp rx, int rxIndex, int frame, float[] dense, int sc)
    {
        int nAmp = Math.Min(sc, rx.Amplitude.Length);
        for (int k = 0; k < nAmp; k++)
            dense[WindowContract.DenseIndex(rxIndex, WindowContract.ModalityAmplitude, frame, k)] =
                rx.Amplitude[k];

        int nPhase = Math.Min(sc, rx.SanitizedPhase.Length);
        for (int k = 0; k < nPhase; k++)
            dense[WindowContract.DenseIndex(rxIndex, WindowContract.ModalityPhase, frame, k)] =
                rx.SanitizedPhase[k];
    }

    private void FillDopplerColumn(AlignedDspFrame[] frames, int rx, int subcarrier, float[] doppler)
    {
        int wf = WindowContract.WindowFrames;

        // Gather this subcarrier's amplitude time series over the window (oldest→newest).
        for (int t = 0; t < wf; t++)
        {
            float[] amp = rx == 0 ? frames[t].Rx0.Amplitude : frames[t].Rx1.Amplitude;
            _series[t] = subcarrier < amp.Length ? amp[subcarrier] : 0.0;
        }

        // Slide the STFT window by the pinned hop and write each magnitude column.
        for (int f = 0; f < WindowContract.StftFrames; f++)
        {
            int start = f * DspContract.StftHopSize;
            StftProcessor.MagnitudeColumn(
                _series.AsSpan(start, DspContract.StftWindowSize), _binScratch);
            for (int m = 0; m < WindowContract.StftBins; m++)
                doppler[WindowContract.DopplerIndex(rx, subcarrier, f, m)] = _binScratch[m];
        }
    }
}
