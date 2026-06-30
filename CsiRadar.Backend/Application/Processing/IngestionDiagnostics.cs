using CsiRadar.Backend.Infrastructure.Mqtt;

namespace CsiRadar.Backend.Application.Processing;

/// <summary>
/// Lock-free counters for the V2 multi-source ingestion path (Phase 1). Written by
/// the MQTT hot path (decode side) and the alignment service (pairing side) via
/// <see cref="System.Threading.Interlocked"/>; read by the <c>/health</c> endpoint.
///
/// This is the Phase 1 exit-criteria surface: per-RX frame rate, pairing rate, and
/// unpaired drops are all derivable here, so "is RX1 alive?" and "why aren't frames
/// pairing?" are answerable without a debugger.
/// </summary>
public sealed class IngestionDiagnostics
{
    private readonly long _startedTicks = DateTime.UtcNow.Ticks;

    // ── Decode side (MQTT hot path) ──
    private long _messagesReceived;
    private long _decoded;        // valid frames written to the raw channel
    private long _dropped;        // raw channel full (DropOldest writer completed)
    private long _badMagic;
    private long _versionMismatch;
    private long _truncated;      // TooShort + Truncated

    // ── Alignment side (pairing) ──
    private long _rx0Frames;
    private long _rx1Frames;
    private long _unknownDevice;
    private long _pairsEmitted;
    private long _unpairedRx0;    // RX0 frame evicted without an RX1 partner
    private long _unpairedRx1;    // RX1 frame evicted without an RX0 partner
    private long _duplicateSeq;   // same seqNo seen twice from the same RX
    private long _pending;        // gauge: half-paired entries currently buffered

    // ── Decode-side increments ──
    public void IncMessagesReceived() => Interlocked.Increment(ref _messagesReceived);
    public void IncDecoded() => Interlocked.Increment(ref _decoded);
    public void IncDropped() => Interlocked.Increment(ref _dropped);
    public void IncBadMagic() => Interlocked.Increment(ref _badMagic);
    public void IncVersionMismatch() => Interlocked.Increment(ref _versionMismatch);
    public void IncTruncated() => Interlocked.Increment(ref _truncated);

    // ── Alignment-side increments ──
    public void IncRx0Frames() => Interlocked.Increment(ref _rx0Frames);
    public void IncRx1Frames() => Interlocked.Increment(ref _rx1Frames);
    public void IncUnknownDevice() => Interlocked.Increment(ref _unknownDevice);
    public void IncPairsEmitted() => Interlocked.Increment(ref _pairsEmitted);
    public void IncUnpairedRx0() => Interlocked.Increment(ref _unpairedRx0);
    public void IncUnpairedRx1() => Interlocked.Increment(ref _unpairedRx1);
    public void IncDuplicateSeq() => Interlocked.Increment(ref _duplicateSeq);
    public void SetPending(int count) => Interlocked.Exchange(ref _pending, count);

    /// <summary>Atomic-enough snapshot for <c>/health</c> (counters read independently).</summary>
    public IngestionSnapshot Snapshot()
    {
        double elapsed = Math.Max(
            (DateTime.UtcNow.Ticks - _startedTicks) / (double)TimeSpan.TicksPerSecond, 1e-3);

        long rx0 = Interlocked.Read(ref _rx0Frames);
        long rx1 = Interlocked.Read(ref _rx1Frames);
        long pairs = Interlocked.Read(ref _pairsEmitted);

        return new IngestionSnapshot
        {
            ProtocolVersion = CsiBinaryProtocol.Version,
            MessagesReceived = Interlocked.Read(ref _messagesReceived),
            Decoded = Interlocked.Read(ref _decoded),
            Dropped = Interlocked.Read(ref _dropped),
            BadMagic = Interlocked.Read(ref _badMagic),
            VersionMismatch = Interlocked.Read(ref _versionMismatch),
            Truncated = Interlocked.Read(ref _truncated),
            UnknownDevice = Interlocked.Read(ref _unknownDevice),
            Rx0Frames = rx0,
            Rx1Frames = rx1,
            PairsEmitted = pairs,
            UnpairedRx0 = Interlocked.Read(ref _unpairedRx0),
            UnpairedRx1 = Interlocked.Read(ref _unpairedRx1),
            DuplicateSeq = Interlocked.Read(ref _duplicateSeq),
            Pending = Interlocked.Read(ref _pending),
            Rx0FrameRateHz = rx0 / elapsed,
            Rx1FrameRateHz = rx1 / elapsed,
            PairRateHz = pairs / elapsed,
            // Fraction of RX0 frames that found a partner; the headline "are both RX healthy?" number.
            PairingRatio = rx0 > 0 ? pairs / (double)rx0 : 0.0,
        };
    }
}

/// <summary>Immutable view of <see cref="IngestionDiagnostics"/> for serialization.</summary>
public sealed class IngestionSnapshot
{
    public int ProtocolVersion { get; init; }
    public long MessagesReceived { get; init; }
    public long Decoded { get; init; }
    public long Dropped { get; init; }
    public long BadMagic { get; init; }
    public long VersionMismatch { get; init; }
    public long Truncated { get; init; }
    public long UnknownDevice { get; init; }
    public long Rx0Frames { get; init; }
    public long Rx1Frames { get; init; }
    public long PairsEmitted { get; init; }
    public long UnpairedRx0 { get; init; }
    public long UnpairedRx1 { get; init; }
    public long DuplicateSeq { get; init; }
    public long Pending { get; init; }
    public double Rx0FrameRateHz { get; init; }
    public double Rx1FrameRateHz { get; init; }
    public double PairRateHz { get; init; }
    public double PairingRatio { get; init; }
}
