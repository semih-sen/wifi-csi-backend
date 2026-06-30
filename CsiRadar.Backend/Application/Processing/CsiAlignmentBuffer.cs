using CsiRadar.Backend.Core.Entities;

namespace CsiRadar.Backend.Application.Processing;

/// <summary>
/// The seqNo alignment buffer — the linchpin of the V2 multi-RX design (Phase 1).
///
/// The two ESP32 clocks are independent (<c>esp_timer</c> is device-local), so the
/// streams are aligned <b>by seqNo, never by wall-clock</b>. Each incoming frame is
/// mapped to an RX0/RX1 slot by its <see cref="CsiData.DeviceMac"/>; a half-paired
/// seqNo waits in <c>_pending</c> until its partner arrives (→ emit an
/// <see cref="AlignedCsiFrame"/>) or ages out (→ counted as an unpaired drop, so an
/// RX dropping out is a first-class observable).
///
/// Pure and single-threaded by contract: it is driven only by the alignment service's
/// consumer loop, so the dictionary needs no lock. All cross-thread visibility lives
/// in <see cref="IngestionDiagnostics"/> (Interlocked), which it updates as it runs.
/// </summary>
public sealed class CsiAlignmentBuffer
{
    private sealed class Pending
    {
        public CsiData? Rx0;
        public CsiData? Rx1;
        public long ArrivalTicks;
    }

    private readonly Dictionary<uint, Pending> _pending = new();
    private readonly long _rx0Mac;
    private readonly long _rx1Mac;
    private readonly long _timeoutTicks; // ≤0 → time-based eviction disabled
    private readonly int _maxPending;
    private readonly IngestionDiagnostics _diag;

    /// <param name="rx0Mac">Packed MAC for the RX0 slot, or a value matching no device to disable it.</param>
    /// <param name="rx1Mac">Packed MAC for the RX1 slot.</param>
    /// <param name="timeoutTicks">Pairing window in ticks; ≤0 disables time eviction.</param>
    /// <param name="maxPending">Safety cap on outstanding half-paired entries.</param>
    public CsiAlignmentBuffer(
        long rx0Mac, long rx1Mac, long timeoutTicks, int maxPending, IngestionDiagnostics diag)
    {
        _rx0Mac = rx0Mac;
        _rx1Mac = rx1Mac;
        _timeoutTicks = timeoutTicks;
        _maxPending = Math.Max(1, maxPending);
        _diag = diag;
    }

    /// <summary>
    /// Feeds one raw per-RX frame into the buffer. Returns a completed
    /// <see cref="AlignedCsiFrame"/> if this frame paired with a waiting partner,
    /// otherwise null. Stale/over-cap entries are evicted (and counted) on each call.
    /// </summary>
    public AlignedCsiFrame? Accept(CsiData frame, long nowTicks)
    {
        int slot = SlotFor(frame.DeviceMac);
        if (slot < 0)
        {
            _diag.IncUnknownDevice();
            return null;
        }

        if (slot == 0) _diag.IncRx0Frames();
        else _diag.IncRx1Frames();

        AlignedCsiFrame? completed = null;

        if (_pending.TryGetValue(frame.SeqNo, out var entry))
        {
            bool sameSlotFilled = slot == 0 ? entry.Rx0 is not null : entry.Rx1 is not null;
            if (sameSlotFilled)
            {
                // Same RX produced this seqNo twice (retransmit / firmware wrap quirk).
                // Keep the newest and refresh its age; do not pair against itself.
                _diag.IncDuplicateSeq();
                if (slot == 0) entry.Rx0 = frame; else entry.Rx1 = frame;
                entry.ArrivalTicks = nowTicks;
            }
            else
            {
                if (slot == 0) entry.Rx0 = frame; else entry.Rx1 = frame;
                completed = new AlignedCsiFrame
                {
                    SeqNo = frame.SeqNo,
                    Rx0 = entry.Rx0!,
                    Rx1 = entry.Rx1!,
                    ArrivalSkewTicks = Math.Abs(entry.Rx0!.TimestampTicks - entry.Rx1!.TimestampTicks),
                };
                _pending.Remove(frame.SeqNo);
                _diag.IncPairsEmitted();
            }
        }
        else
        {
            var fresh = new Pending { ArrivalTicks = nowTicks };
            if (slot == 0) fresh.Rx0 = frame; else fresh.Rx1 = frame;
            _pending[frame.SeqNo] = fresh;
        }

        EvictStale(nowTicks);
        _diag.SetPending(_pending.Count);
        return completed;
    }

    private int SlotFor(long mac) =>
        mac == _rx0Mac ? 0 : mac == _rx1Mac ? 1 : -1;

    /// <summary>
    /// Evicts half-paired entries older than the pairing window, then enforces the
    /// MaxPending cap by dropping the oldest. Each eviction counts the present half as
    /// an unpaired drop for its RX. O(n) over a small buffer — fine at 100 Hz × 2 RX.
    /// </summary>
    private void EvictStale(long nowTicks)
    {
        if (_pending.Count == 0)
            return;

        if (_timeoutTicks > 0)
        {
            List<uint>? expired = null;
            foreach (var kv in _pending)
            {
                if (nowTicks - kv.Value.ArrivalTicks > _timeoutTicks)
                    (expired ??= []).Add(kv.Key);
            }
            if (expired is not null)
                foreach (uint key in expired)
                    DropUnpaired(key);
        }

        while (_pending.Count > _maxPending)
        {
            uint oldestKey = 0;
            long oldest = long.MaxValue;
            foreach (var kv in _pending)
            {
                if (kv.Value.ArrivalTicks < oldest)
                {
                    oldest = kv.Value.ArrivalTicks;
                    oldestKey = kv.Key;
                }
            }
            DropUnpaired(oldestKey);
        }
    }

    private void DropUnpaired(uint key)
    {
        if (!_pending.Remove(key, out var entry))
            return;
        if (entry.Rx0 is not null) _diag.IncUnpairedRx0();
        if (entry.Rx1 is not null) _diag.IncUnpairedRx1();
    }
}
