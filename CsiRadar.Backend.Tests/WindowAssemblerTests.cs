using CsiRadar.Backend.Application.Processing.Dsp;
using CsiRadar.Backend.Application.Processing.Windowing;
using CsiRadar.Backend.Core.Entities;
using Xunit;

namespace CsiRadar.Backend.Tests;

/// <summary>
/// Unit tests for the Phase 3 windowing + fusion state machine. The cross-language
/// tensor-layout parity is covered separately by <see cref="WindowGoldenParityTests"/>;
/// these pin the emission cadence, the tensor shapes, and — critically — the
/// gap-continuity rule (no window is silently stitched across an alignment gap).
/// </summary>
public class WindowAssemblerTests
{
    private const int Wf = WindowContract.WindowFrames;
    private const int Slide = WindowContract.WindowSlide;

    /// <summary>Builds a trivial aligned-DSP frame with the given seqNo and constant modalities.</summary>
    private static AlignedDspFrame Frame(uint seq, float ampFill = 1.0f)
    {
        var amp = new float[WindowContract.Subcarriers];
        var phase = new float[WindowContract.Subcarriers];
        Array.Fill(amp, ampFill);

        var dummyRx = new CsiData { RawCsiData = [], RawDataLength = 0 };
        return new AlignedDspFrame
        {
            SeqNo = seq,
            Rx0 = new RxDsp { Amplitude = amp, SanitizedPhase = phase },
            Rx1 = new RxDsp { Amplitude = amp, SanitizedPhase = phase },
            Source = new AlignedCsiFrame { SeqNo = seq, Rx0 = dummyRx, Rx1 = dummyRx },
        };
    }

    [Fact]
    public void FirstFullWindow_EmitsExactlyAtWindowFrames_WithPinnedShapes()
    {
        var a = new WindowAssembler();
        FusedWindow? emitted = null;
        int emissions = 0;

        for (uint i = 0; i < Wf; i++)
        {
            var r = a.Accept(Frame(i));
            if (r.Emitted is not null)
            {
                emissions++;
                emitted = r.Emitted;
                Assert.Equal(Wf - 1, (int)i); // only on the last frame of the first window
            }
        }

        Assert.Equal(1, emissions);
        Assert.NotNull(emitted);
        Assert.Equal(WindowContract.DenseLength, emitted!.Dense.Length);
        Assert.Equal(WindowContract.DopplerLength, emitted.Doppler.Length);
        Assert.Equal(0u, emitted.StartSeqNo);
        Assert.Equal((uint)(Wf - 1), emitted.EndSeqNo);
    }

    [Fact]
    public void SlidingWindows_EmitEverySlideFrames()
    {
        var a = new WindowAssembler();
        var starts = new List<uint>();

        // Feed two windows' worth plus one slide.
        for (uint i = 0; i < Wf + 2 * Slide; i++)
        {
            var r = a.Accept(Frame(i));
            if (r.Emitted is not null)
                starts.Add(r.Emitted.StartSeqNo);
        }

        // First at frame Wf-1 (start 0), then every Slide frames after.
        Assert.Equal(new uint[] { 0, (uint)Slide, (uint)(2 * Slide) }, starts.ToArray());
    }

    [Fact]
    public void GapMidWindow_RejectsAndNeverStitches_ThenRecovers()
    {
        var a = new WindowAssembler();
        var emitted = new List<FusedWindow>();
        int gaps = 0, rejects = 0;

        // Warm up one clean window.
        uint seq = 0;
        for (; seq < Wf; seq++)
        {
            var r = a.Accept(Frame(seq));
            if (r.Emitted is not null) emitted.Add(r.Emitted);
        }
        Assert.Single(emitted);

        // Introduce a gap: skip 10 seqNos, then feed a long contiguous run so the
        // schedule fires several times while the buffer refills (those are rejections),
        // and eventually emits a clean, gap-free window again.
        seq += 10;
        int contiguousAfterGap = Wf + 2 * Slide;
        for (int j = 0; j < contiguousAfterGap; j++, seq++)
        {
            var r = a.Accept(Frame(seq));
            if (r.GapDetected) gaps++;
            if (r.EmissionRejectedForGap) rejects++;
            if (r.Emitted is not null) emitted.Add(r.Emitted);
        }

        Assert.Equal(1, gaps);
        Assert.True(rejects >= 1, "at least one scheduled window must be rejected while refilling after the gap");

        // Every emitted window is internally contiguous (never stitched across the gap).
        foreach (var w in emitted)
            Assert.Equal((uint)(Wf - 1), w.EndSeqNo - w.StartSeqNo);

        // No emitted window straddles the gap: its span must be entirely before the gap
        // (ends < first post-gap seq) or entirely after (starts > gap boundary).
        uint gapBoundary = (uint)(Wf - 1); // last pre-gap seq; post-gap stream starts at Wf+10
        foreach (var w in emitted)
            Assert.True(w.EndSeqNo <= gapBoundary || w.StartSeqNo >= (uint)(Wf + 10),
                $"window {w.StartSeqNo}..{w.EndSeqNo} straddles the alignment gap");
    }

    [Fact]
    public void DenseTensor_HasCorrectAxisOrderAndValues()
    {
        var a = new WindowAssembler();
        FusedWindow? win = null;

        // Distinct per-(rx,modality,frame,subcarrier) values so a transpose would be caught.
        for (uint f = 0; f < Wf; f++)
        {
            var frame = MakeDistinctFrame(f);
            var r = a.Accept(frame);
            if (r.Emitted is not null) win = r.Emitted;
        }

        Assert.NotNull(win);
        // Spot-check a few cells against the pinned index formula.
        for (int rx = 0; rx < WindowContract.RxCount; rx++)
            foreach (int f in new[] { 0, Wf / 2, Wf - 1 })
                foreach (int k in new[] { 0, 31, 63 })
                {
                    float expAmp = DistinctAmp(rx, f, k);
                    float expPhase = DistinctPhase(rx, f, k);
                    Assert.Equal(expAmp,
                        win!.Dense[WindowContract.DenseIndex(rx, WindowContract.ModalityAmplitude, f, k)]);
                    Assert.Equal(expPhase,
                        win.Dense[WindowContract.DenseIndex(rx, WindowContract.ModalityPhase, f, k)]);
                }
    }

    private static float DistinctAmp(int rx, int frame, int sc) => rx * 100000 + frame * 100 + sc;
    private static float DistinctPhase(int rx, int frame, int sc) => -(rx * 100000 + frame * 100 + sc) - 1;

    private static AlignedDspFrame MakeDistinctFrame(uint f)
    {
        RxDsp Rx(int rx)
        {
            var amp = new float[WindowContract.Subcarriers];
            var phase = new float[WindowContract.Subcarriers];
            for (int k = 0; k < amp.Length; k++)
            {
                amp[k] = DistinctAmp(rx, (int)f, k);
                phase[k] = DistinctPhase(rx, (int)f, k);
            }
            return new RxDsp { Amplitude = amp, SanitizedPhase = phase };
        }

        var dummyRx = new CsiData { RawCsiData = [], RawDataLength = 0 };
        return new AlignedDspFrame
        {
            SeqNo = f,
            Rx0 = Rx(0),
            Rx1 = Rx(1),
            Source = new AlignedCsiFrame { SeqNo = f, Rx0 = dummyRx, Rx1 = dummyRx },
        };
    }

    [Fact]
    public void StftFramesConstant_MatchesGeometry()
    {
        Assert.Equal((Wf - DspContract.StftWindowSize) / DspContract.StftHopSize + 1,
            WindowContract.StftFrames);
        Assert.Equal(13, WindowContract.StftFrames);
    }
}
