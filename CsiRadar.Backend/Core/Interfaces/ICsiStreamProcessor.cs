using CsiRadar.Backend.Core.Entities;

namespace CsiRadar.Backend.Core.Interfaces;

/// <summary>
/// Per-frame CSI signal processor (stream model).
///
/// Each incoming frame is demodulated, baseline-subtracted, and IIR low-pass
/// filtered <b>exactly once</b>, in arrival order, the instant it is dequeued
/// from the channel. Because the stateful IIR filter sees every sample only
/// once, overlapping windows become free and the filter state can never be
/// corrupted by re-feeding overlap regions (the root cause of the previous
/// per-window <c>ProcessWindow</c> design).
///
/// The "window" is no longer a re-filtering unit — it is a cheap snapshot taken
/// over a ring buffer of already-filtered frames (see <c>CsiRingBuffer</c>).
///
/// Threading: <see cref="ProcessFrame"/> mutates per-subcarrier filter state and
/// is intended to be called by the single consumer thread only.
/// <see cref="UpdateBaseline"/> may be called from another thread; it publishes
/// the new baseline via a single volatile reference swap.
/// </summary>
public interface ICsiStreamProcessor
{
    /// <summary>
    /// Number of subcarriers, discovered from the first usable frame.
    /// Zero until the first frame has been processed.
    /// </summary>
    int SubcarrierCount { get; }

    /// <summary>
    /// Whether a baseline calibration has been performed.
    /// </summary>
    bool IsCalibrated { get; }

    /// <summary>
    /// Demodulates one frame to per-subcarrier amplitude (sqrt(I² + Q²)),
    /// subtracts the calibration baseline, then applies the IIR low-pass filter
    /// in place. The filtered amplitudes are written into <paramref name="destination"/>.
    /// </summary>
    /// <param name="frame">A single raw CSI frame.</param>
    /// <param name="destination">
    /// Caller-owned buffer, length ≥ <see cref="SubcarrierCount"/> (after the
    /// first frame). Reused across calls; never pooled per-frame.
    /// </param>
    /// <returns>
    /// Number of subcarriers written, or 0 if the frame is unusable
    /// (e.g. fewer than 2 raw samples).
    /// </returns>
    int ProcessFrame(CsiData frame, Span<float> destination);

    /// <summary>
    /// Recomputes the static baseline reference (mean amplitude per subcarrier)
    /// from a batch of empty-room frames. Should be called periodically
    /// (e.g. nightly) or on demand when the room is known to be empty.
    /// </summary>
    /// <param name="baselineFrames">CSI frames captured in an empty room.</param>
    void UpdateBaseline(ReadOnlySpan<CsiData> baselineFrames);
}
