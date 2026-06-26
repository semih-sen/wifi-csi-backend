using CsiRadar.Backend.Application.Processing;

namespace CsiRadar.Backend.Tests;

/// <summary>
/// Unit tests for <see cref="CsiRingBuffer"/>: write/wrap correctness and the
/// subcarrier-major transpose of the window snapshot (including the wrap boundary).
/// </summary>
public class CsiRingBufferTests
{
    /// <summary>Canonical value stored for (frame f, subcarrier s).</summary>
    private static float Value(int f, int s) => f * 10f + s;

    private static float[] FrameOf(int f, int sc)
    {
        var frame = new float[sc];
        for (int s = 0; s < sc; s++)
            frame[s] = Value(f, s);
        return frame;
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(100, 128)]
    [InlineData(128, 128)]
    [InlineData(129, 256)]
    public void NextPowerOfTwo_RoundsUp(int input, int expected)
    {
        Assert.Equal(expected, CsiRingBuffer.NextPowerOfTwo(input));
    }

    [Fact]
    public void Constructor_RejectsNonPowerOfTwoCapacity()
    {
        Assert.Throws<ArgumentException>(() => new CsiRingBuffer(2, 100));
    }

    [Fact]
    public void Snapshot_NoWrap_TransposesToSubcarrierMajor()
    {
        const int sc = 3, capacity = 8, windowSize = 4;
        var ring = new CsiRingBuffer(sc, capacity);
        for (int f = 0; f < 5; f++)          // 5 frames, no wrap
            ring.Write(FrameOf(f, sc));

        var snap = new float[sc * windowSize];
        ring.SnapshotSubcarrierMajor(windowSize, snap);

        // Window covers the last 4 frames: 1, 2, 3, 4.
        for (int s = 0; s < sc; s++)
            for (int t = 0; t < windowSize; t++)
                Assert.Equal(Value(1 + t, s), snap[s * windowSize + t]);
    }

    [Fact]
    public void Snapshot_WithWrap_ReturnsMostRecentFrames()
    {
        const int sc = 2, capacity = 4, windowSize = 4;
        var ring = new CsiRingBuffer(sc, capacity);
        for (int f = 0; f < 10; f++)         // wraps multiple times
            ring.Write(FrameOf(f, sc));

        var snap = new float[sc * windowSize];
        ring.SnapshotSubcarrierMajor(windowSize, snap);

        // Window covers the last 4 frames: 6, 7, 8, 9 — across the wrap boundary.
        for (int s = 0; s < sc; s++)
            for (int t = 0; t < windowSize; t++)
                Assert.Equal(Value(6 + t, s), snap[s * windowSize + t]);
    }

    [Fact]
    public void Write_RejectsWrongFrameLength()
    {
        var ring = new CsiRingBuffer(3, 8);
        Assert.Throws<ArgumentException>(() => ring.Write(new float[2]));
    }

    [Fact]
    public void Snapshot_BeforeEnoughFramesWritten_Throws()
    {
        var ring = new CsiRingBuffer(2, 8);
        ring.Write(FrameOf(0, 2));
        Assert.Throws<InvalidOperationException>(
            () => ring.SnapshotSubcarrierMajor(4, new float[8]));
    }

    [Fact]
    public void Snapshot_LargerThanCapacity_Throws()
    {
        var ring = new CsiRingBuffer(2, 4);
        for (int f = 0; f < 4; f++)
            ring.Write(FrameOf(f, 2));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ring.SnapshotSubcarrierMajor(8, new float[16]));
    }

    [Fact]
    public void WrittenCount_Increments()
    {
        var ring = new CsiRingBuffer(2, 4);
        Assert.Equal(0, ring.Written);
        ring.Write(FrameOf(0, 2));
        ring.Write(FrameOf(1, 2));
        Assert.Equal(2, ring.Written);
    }
}
