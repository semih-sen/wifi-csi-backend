using CsiRadar.Backend.Core.Entities;

namespace CsiRadar.Backend.Application.Recording;

/// <summary>Ordered command kinds carried on the recording channel.</summary>
internal enum RecordingCommandKind : byte
{
    Start,
    Frame,
    Stop,
}

/// <summary>
/// A single item on the recording channel. Start/Frame/Stop all flow through one
/// FIFO channel so the writer observes them in submission order, which guarantees
/// that all of a session's frames are written before its Stop is processed. Each
/// envelope is stamped with its <see cref="SessionId"/> so the writer can discard
/// the rare late frame that races a Stop.
/// </summary>
internal sealed class RecordingEnvelope
{
    public RecordingCommandKind Kind { get; init; }
    public long SessionId { get; init; }

    // ── Frame payload (Kind == Frame) ──
    public long TimestampMs { get; init; }
    public int Rssi { get; init; }
    public float[]? Filtered { get; init; } // length == subcarrier count
    public sbyte[]? Raw { get; init; }      // populated only when CaptureRaw

    // ── Session payload (Kind == Start) ──
    public RecordingSessionInfo? SessionInfo { get; init; }

    // ── Final counters (Kind == Stop) ──
    public long DroppedFrames { get; init; }
}

/// <summary>
/// Live state for one active session, owned by the <c>RecordingService</c>.
/// Counters are mutated with <see cref="System.Threading.Interlocked"/> from the
/// hot path, so they are exposed as fields (Interlocked needs a ref).
/// </summary>
internal sealed class RecordingSession
{
    public long Id { get; }
    public RecordingSessionInfo Info { get; }

    public long Captured; // frames successfully enqueued
    public long Dropped;  // frames lost to a full channel (must invalidate the session)

    public RecordingSession(long id, RecordingSessionInfo info)
    {
        Id = id;
        Info = info;
    }
}