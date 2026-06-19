namespace CsiRadar.Backend.Core.Configuration;

/// <summary>
/// Configuration options for the signal processing pipeline.
/// Bound from appsettings.json section "Processing".
/// </summary>
public sealed class ProcessingOptions
{
    public const string SectionName = "Processing";

    /// <summary>
    /// Number of CSI frames in a single sliding window.
    /// At 100 Hz, a value of 100 means a 1-second window.
    /// </summary>
    public int WindowSize { get; set; } = 100;

    /// <summary>
    /// Number of frames to slide the window forward after each processing cycle.
    /// A value less than WindowSize creates overlapping windows.
    /// </summary>
    public int SlideStep { get; set; } = 50;

    /// <summary>
    /// Cutoff frequency (Hz) for the Butterworth low-pass filter.
    /// Frequencies above this are attenuated to remove thermal noise.
    /// </summary>
    public double LowPassCutoffHz { get; set; } = 20.0;

    /// <summary>
    /// Sampling rate in Hz (should match the ESP32 CSI packet rate).
    /// </summary>
    public double SamplingRateHz { get; set; } = 100.0;

    /// <summary>
    /// Order of the Butterworth filter. Higher = steeper roll-off.
    /// </summary>
    public int FilterOrder { get; set; } = 4;
}
