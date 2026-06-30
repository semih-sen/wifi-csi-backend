using CsiRadar.Backend.Application.Processing;
using CsiRadar.Backend.Core.Entities;
using Xunit;

namespace CsiRadar.Backend.Tests;

/// <summary>
/// Covers the seqNo alignment buffer — the linchpin of the multi-RX design: pairing,
/// order-independence, duplicate handling, unknown devices, and unpaired-drop
/// eviction (the "an RX dropped out" observable).
/// </summary>
public class CsiAlignmentBufferTests
{
    private const long Rx0 = 0xAABBCCDDEE00;
    private const long Rx1 = 0xAABBCCDDEE01;
    private const long TimeoutTicks = 200 * TimeSpan.TicksPerMillisecond;

    private static CsiData Frame(long mac, uint seq, long ticks = 0) => new()
    {
        DeviceMac = mac,
        SeqNo = seq,
        TimestampTicks = ticks,
        RawCsiData = [1, 0],
        RawDataLength = 2,
    };

    private static (CsiAlignmentBuffer buffer, IngestionDiagnostics diag) NewBuffer(
        long timeoutTicks = TimeoutTicks, int maxPending = 256)
    {
        var diag = new IngestionDiagnostics();
        var buffer = new CsiAlignmentBuffer(Rx0, Rx1, timeoutTicks, maxPending, diag);
        return (buffer, diag);
    }

    [Fact]
    public void Accept_Rx0ThenRx1_SameSeq_EmitsPair()
    {
        var (buffer, diag) = NewBuffer();

        Assert.Null(buffer.Accept(Frame(Rx0, 7, ticks: 10), nowTicks: 0));
        var pair = buffer.Accept(Frame(Rx1, 7, ticks: 14), nowTicks: 0);

        Assert.NotNull(pair);
        Assert.Equal(7u, pair!.SeqNo);
        Assert.Equal(Rx0, pair.Rx0.DeviceMac);
        Assert.Equal(Rx1, pair.Rx1.DeviceMac);
        Assert.Equal(4, pair.ArrivalSkewTicks); // |14 - 10|

        var s = diag.Snapshot();
        Assert.Equal(1, s.PairsEmitted);
        Assert.Equal(1, s.Rx0Frames);
        Assert.Equal(1, s.Rx1Frames);
        Assert.Equal(0, s.Pending);
    }

    [Fact]
    public void Accept_Rx1FirstThenRx0_StillPairs()
    {
        var (buffer, _) = NewBuffer();

        Assert.Null(buffer.Accept(Frame(Rx1, 9), nowTicks: 0));
        var pair = buffer.Accept(Frame(Rx0, 9), nowTicks: 0);

        Assert.NotNull(pair);
        Assert.Equal(Rx0, pair!.Rx0.DeviceMac);
        Assert.Equal(Rx1, pair.Rx1.DeviceMac);
    }

    [Fact]
    public void Accept_InterleavedSequences_PairIndependently()
    {
        var (buffer, diag) = NewBuffer();

        Assert.Null(buffer.Accept(Frame(Rx0, 1), 0));
        Assert.Null(buffer.Accept(Frame(Rx0, 2), 0));
        Assert.NotNull(buffer.Accept(Frame(Rx1, 2), 0)); // pairs seq 2 while 1 still waits
        Assert.NotNull(buffer.Accept(Frame(Rx1, 1), 0));

        var s = diag.Snapshot();
        Assert.Equal(2, s.PairsEmitted);
        Assert.Equal(0, s.Pending);
    }

    [Fact]
    public void Accept_UnknownDevice_IsCountedAndDropped()
    {
        var (buffer, diag) = NewBuffer();

        var result = buffer.Accept(Frame(0xDEADBEEF, 1), 0);

        Assert.Null(result);
        var s = diag.Snapshot();
        Assert.Equal(1, s.UnknownDevice);
        Assert.Equal(0, s.Rx0Frames);
        Assert.Equal(0, s.Rx1Frames);
    }

    [Fact]
    public void Accept_DuplicateSeqFromSameRx_DoesNotSelfPair()
    {
        var (buffer, diag) = NewBuffer();

        Assert.Null(buffer.Accept(Frame(Rx0, 5), 0));
        Assert.Null(buffer.Accept(Frame(Rx0, 5), 0)); // same RX, same seq again

        var s = diag.Snapshot();
        Assert.Equal(1, s.DuplicateSeq);
        Assert.Equal(0, s.PairsEmitted);
        Assert.Equal(1, s.Pending); // still half-paired

        // The partner can still complete it afterwards.
        Assert.NotNull(buffer.Accept(Frame(Rx1, 5), 0));
    }

    [Fact]
    public void EvictStale_TimedOutHalfPair_CountsUnpairedForItsRx()
    {
        var (buffer, diag) = NewBuffer();

        // RX0 arrives but RX1 never does; a later frame advances the clock past timeout.
        Assert.Null(buffer.Accept(Frame(Rx0, 100), nowTicks: 0));
        buffer.Accept(Frame(Rx0, 101), nowTicks: TimeoutTicks + 1); // triggers eviction sweep

        var s = diag.Snapshot();
        Assert.Equal(1, s.UnpairedRx0);
        Assert.Equal(0, s.UnpairedRx1);
        Assert.Equal(0, s.PairsEmitted);
    }

    [Fact]
    public void EvictStale_OverMaxPending_DropsOldest()
    {
        var (buffer, diag) = NewBuffer(timeoutTicks: 0, maxPending: 2); // time eviction off

        buffer.Accept(Frame(Rx0, 1), nowTicks: 1);
        buffer.Accept(Frame(Rx0, 2), nowTicks: 2);
        buffer.Accept(Frame(Rx0, 3), nowTicks: 3); // exceeds cap → oldest (seq 1) evicted

        var s = diag.Snapshot();
        Assert.Equal(2, s.Pending);
        Assert.Equal(1, s.UnpairedRx0);
    }

    [Fact]
    public void Accept_SameMacForBothSlots_NeverPairs()
    {
        var diag = new IngestionDiagnostics();
        var buffer = new CsiAlignmentBuffer(Rx0, Rx0, TimeoutTicks, 256, diag);

        Assert.Null(buffer.Accept(Frame(Rx0, 1), 0));
        // Second frame is the same slot (slot 0) → duplicate, not a pair.
        Assert.Null(buffer.Accept(Frame(Rx0, 1), 0));

        Assert.Equal(0, diag.Snapshot().PairsEmitted);
    }
}
