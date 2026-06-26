namespace CsiRadar.Backend.Application.Processing;

/// <summary>
/// A flat, frame-major ring buffer of already-filtered CSI frames, owned by the
/// single consumer loop. Storage is one contiguous <c>float[subcarrierCount *
/// capacity]</c>; frame <c>f</c> occupies <c>[f*sc .. f*sc + sc)</c>. Writing a
/// frame is therefore a single contiguous copy.
///
/// A "window" is produced on demand as a <em>snapshot</em> over the most recent
/// frames — no re-filtering, no per-frame array shuffling. The snapshot is laid
/// out subcarrier-major (the model's expected layout); the transpose is paid
/// once per emitted window, not per frame.
///
/// Capacity is a power of two so wrap-around is a cheap bit-mask. Not
/// thread-safe — single producer/consumer (the processing loop) only.
/// </summary>
internal sealed class CsiRingBuffer
{
    private readonly int _subcarrierCount;
    private readonly int _capacity;
    private readonly int _mask;
    private readonly float[] _buffer; // frame-major: [frame0 sc.., frame1 sc.., ...]

    private int _head;       // index of the next frame slot to write
    private long _written;   // total frames ever written (monotonic)

    public CsiRingBuffer(int subcarrierCount, int capacity)
    {
        if (subcarrierCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(subcarrierCount));
        if (capacity <= 0 || (capacity & (capacity - 1)) != 0)
            throw new ArgumentException("Capacity must be a positive power of two.", nameof(capacity));

        _subcarrierCount = subcarrierCount;
        _capacity = capacity;
        _mask = capacity - 1;
        _buffer = new float[subcarrierCount * capacity];
    }

    /// <summary>Number of subcarriers per frame (the buffer stride).</summary>
    public int SubcarrierCount => _subcarrierCount;

    /// <summary>Frame capacity (power of two).</summary>
    public int Capacity => _capacity;

    /// <summary>Total frames written since construction (monotonic).</summary>
    public long Written => _written;

    /// <summary>
    /// Smallest power of two ≥ <paramref name="value"/> (and ≥ 1).
    /// </summary>
    public static int NextPowerOfTwo(int value)
    {
        if (value <= 1)
            return 1;
        return (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)value);
    }

    /// <summary>
    /// Appends one filtered frame (length must equal <see cref="SubcarrierCount"/>).
    /// </summary>
    public void Write(ReadOnlySpan<float> frame)
    {
        if (frame.Length != _subcarrierCount)
            throw new ArgumentException(
                $"Frame length ({frame.Length}) must equal subcarrier count ({_subcarrierCount}).",
                nameof(frame));

        frame.CopyTo(_buffer.AsSpan(_head * _subcarrierCount, _subcarrierCount));
        _head = (_head + 1) & _mask;
        _written++;
    }

    /// <summary>
    /// Writes the most recent <paramref name="windowSize"/> frames into
    /// <paramref name="destination"/> in <b>subcarrier-major</b> layout:
    /// <c>[sc0_t0 … sc0_t(W-1), sc1_t0 … sc1_t(W-1), …]</c>. Handles wrap-around.
    /// </summary>
    /// <param name="windowSize">Frames to snapshot (≤ <see cref="Capacity"/>, ≤ <see cref="Written"/>).</param>
    /// <param name="destination">Buffer of length ≥ <c>SubcarrierCount * windowSize</c>.</param>
    public void SnapshotSubcarrierMajor(int windowSize, Span<float> destination)
    {
        if (windowSize <= 0 || windowSize > _capacity)
            throw new ArgumentOutOfRangeException(nameof(windowSize));
        if (_written < windowSize)
            throw new InvalidOperationException(
                $"Only {_written} frames written; cannot snapshot {windowSize}.");

        int sc = _subcarrierCount;
        int required = sc * windowSize;
        if (destination.Length < required)
            throw new ArgumentException(
                $"Destination ({destination.Length}) smaller than required ({required}).",
                nameof(destination));

        // Oldest frame in the window: head sits one past the newest frame.
        int startFrame = (_head - windowSize) & _mask;

        for (int t = 0; t < windowSize; t++)
        {
            int slot = (startFrame + t) & _mask;
            ReadOnlySpan<float> frame = _buffer.AsSpan(slot * sc, sc);
            for (int s = 0; s < sc; s++)
                destination[s * windowSize + t] = frame[s]; // transpose into subcarrier-major
        }
    }
}
