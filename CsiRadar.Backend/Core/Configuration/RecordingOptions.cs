namespace CsiRadar.Backend.Core.Configuration;

/// <summary>
/// Configuration options for the training-data recording subsystem.
/// Bound from appsettings.json section "Recording".
/// </summary>
public sealed class RecordingOptions
{
    public const string SectionName = "Recording";

    /// <summary>
    /// Directory where per-session files are written (.csibin payload + .json
    /// manifest). Created on first use. Relative paths resolve against the
    /// process working directory.
    /// </summary>
    public string OutputDirectory { get; set; } = "Recordings";

    /// <summary>
    /// Bounded capacity (frames) of the recording channel — the in-flight buffer
    /// between the consumer and the disk writer. At 100 Hz the sequential writer
    /// drains far faster than ingestion, so this should never fill in practice;
    /// it is slack against transient disk stalls. A full channel does NOT block
    /// the consumer (non-blocking TryWrite) — it counts a dropped frame instead.
    /// </summary>
    public int ChannelCapacity { get; set; } = 512;

    /// <summary>
    /// Also persist the raw interleaved int8 I/Q payload alongside the filtered
    /// amplitudes. Enables offline re-filtering / filter experiments at the cost
    /// of larger files. Default off — the filtered stream is what the model sees,
    /// so recording it eliminates train/serve skew.
    /// </summary>
    public bool CaptureRaw { get; set; } = false;

    /// <summary>
    /// Flush the file stream to disk every N frames. Bounds data loss on an
    /// unclean process exit. 200 ≈ 2 s at 100 Hz.
    /// </summary>
    public int FlushEveryNFrames { get; set; } = 200;
}