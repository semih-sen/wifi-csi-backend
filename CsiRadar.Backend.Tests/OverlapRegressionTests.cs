using CsiRadar.Backend.Application.Processing;
using CsiRadar.Backend.Core.Entities;

namespace CsiRadar.Backend.Tests;

/// <summary>
/// Regression guard for BUG-4 (the original per-window design re-fed overlapping
/// samples through the stateful IIR filter, corrupting the response). With the
/// per-frame stream model, a frame is filtered exactly once and its filtered
/// value is independent of the window/slide configuration.
///
/// These tests assert:
///   1. The per-frame filtered stream is byte-for-byte independent of SlideStep.
///   2. It matches an independent single-pass DF2T cascade reference.
///   3. Each window snapshot faithfully reflects that single-pass stream — so
///      overlapping windows (SlideStep &lt; WindowSize) carry the same values as
///      non-overlapping windows.
/// </summary>
public class OverlapRegressionTests
{
    private const double Fs = 100.0;
    private const double Fc = 20.0;
    private const int Order = 4;
    private const int Sc = 4;
    private const int WindowSize = 100;
    private const int FrameCount = 350;
    private const float Eps = 1e-4f;

    /// <summary>Deterministic input amplitude for (frame t, subcarrier s), in 0..127.</summary>
    private static int Amp(int t, int s)
    {
        double v = 64.0
                 + 40.0 * Math.Sin(2.0 * Math.PI * (3 + s) * t / Fs)   // in-band component
                 + 10.0 * Math.Sin(2.0 * Math.PI * 35 * t / Fs);       // out-of-band noise
        return (int)Math.Round(Math.Clamp(v, 0, 127));
    }

    private static CsiData Frame(int t)
    {
        var amps = new int[Sc];
        for (int s = 0; s < Sc; s++)
            amps[s] = Amp(t, s);
        return TestFrames.FromAmplitudes(amps);
    }

    /// <summary>
    /// Independent single-pass reference: the same float DF2T cascade applied to
    /// the raw amplitude stream, with no notion of windows.
    /// </summary>
    private static float[][] Reference()
    {
        var c = CsiStreamProcessor.ComputeButterworthCascade(Fs, Fc, Order);
        int sections = c.sectionCount;
        var s1 = new float[Sc * sections];
        var s2 = new float[Sc * sections];

        var outp = new float[FrameCount][];
        for (int t = 0; t < FrameCount; t++)
        {
            outp[t] = new float[Sc];
            for (int s = 0; s < Sc; s++)
            {
                float x = Amp(t, s);
                int b = s * sections;
                for (int k = 0; k < sections; k++)
                {
                    int j = b + k;
                    float y = c.b0[k] * x + s1[j];
                    s1[j] = c.b1[k] * x - c.a1[k] * y + s2[j];
                    s2[j] = c.b2[k] * x - c.a2[k] * y;
                    x = y;
                }
                outp[t][s] = x;
            }
        }
        return outp;
    }

    /// <summary>
    /// Runs the real processor + ring buffer for a given slide step, replicating
    /// the consumer's window-emission logic. Returns the per-frame filtered stream
    /// and the list of (endFrameIndex, subcarrier-major snapshot) windows.
    /// </summary>
    private static (float[][] perFrame, List<(int end, float[] snap)> windows) RunPass(int slideStep)
    {
        var proc = TestFrames.NewProcessor(Fs, Fc, Order, WindowSize, slideStep);
        var ring = new CsiRingBuffer(Sc, CsiRingBuffer.NextPowerOfTwo(WindowSize));
        var frameBuf = new float[Sc];

        var perFrame = new float[FrameCount][];
        var windows = new List<(int, float[])>();
        int framesSinceWindow = 0;

        for (int i = 0; i < FrameCount; i++)
        {
            int n = proc.ProcessFrame(Frame(i), frameBuf);
            Assert.Equal(Sc, n);

            perFrame[i] = frameBuf[..Sc].ToArray();
            ring.Write(frameBuf.AsSpan(0, Sc));
            framesSinceWindow++;

            if (ring.Written < WindowSize || framesSinceWindow < slideStep)
                continue;

            framesSinceWindow = 0;
            var snap = new float[Sc * WindowSize];
            ring.SnapshotSubcarrierMajor(WindowSize, snap);
            windows.Add((i, snap));
        }

        return (perFrame, windows);
    }

    [Fact]
    public void FilteredStream_IsIndependentOfSlideStep()
    {
        var (perFrame50, _) = RunPass(slideStep: 50);   // overlapping
        var (perFrame100, _) = RunPass(slideStep: 100); // non-overlapping

        for (int t = 0; t < FrameCount; t++)
            for (int s = 0; s < Sc; s++)
                Assert.Equal(perFrame100[t][s], perFrame50[t][s], Eps);
    }

    [Fact]
    public void ProcessFrame_MatchesSinglePassReference()
    {
        var reference = Reference();
        var (perFrame, _) = RunPass(slideStep: 50);

        for (int t = 0; t < FrameCount; t++)
            for (int s = 0; s < Sc; s++)
                Assert.Equal(reference[t][s], perFrame[t][s], Eps);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(100)]
    public void WindowSnapshots_ReflectSinglePassStream(int slideStep)
    {
        var (perFrame, windows) = RunPass(slideStep);

        Assert.NotEmpty(windows);
        foreach (var (end, snap) in windows)
        {
            int start = end - WindowSize + 1;
            for (int s = 0; s < Sc; s++)
                for (int t = 0; t < WindowSize; t++)
                    Assert.Equal(perFrame[start + t][s], snap[s * WindowSize + t], Eps);
        }
    }
}
