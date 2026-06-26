using CsiRadar.Backend.Application.Processing;
using CsiRadar.Backend.Core.Configuration;
using CsiRadar.Backend.Core.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CsiRadar.Backend.Tests;

/// <summary>
/// Shared helpers for building CSI frames and processors in tests.
/// </summary>
internal static class TestFrames
{
    /// <summary>
    /// Builds a <see cref="CsiStreamProcessor"/> with the given filter parameters.
    /// </summary>
    public static CsiStreamProcessor NewProcessor(
        double fs = 100.0, double fc = 20.0, int order = 4,
        int windowSize = 100, int slideStep = 50)
    {
        var options = Options.Create(new ProcessingOptions
        {
            SamplingRateHz = fs,
            LowPassCutoffHz = fc,
            FilterOrder = order,
            WindowSize = windowSize,
            SlideStep = slideStep
        });

        return new CsiStreamProcessor(options, NullLogger<CsiStreamProcessor>.Instance);
    }

    /// <summary>
    /// Builds a CSI frame whose per-subcarrier amplitude equals the given values.
    /// Encodes each amplitude as the I component with Q = 0, so
    /// amplitude = sqrt(I² + 0²) = |I| (values must be in 0..127).
    /// </summary>
    public static CsiData FromAmplitudes(params int[] amplitudes)
    {
        var raw = new sbyte[amplitudes.Length * 2];
        for (int k = 0; k < amplitudes.Length; k++)
        {
            raw[k * 2] = checked((sbyte)amplitudes[k]); // I
            raw[k * 2 + 1] = 0;                          // Q
        }

        return new CsiData
        {
            RawCsiData = raw,
            RawDataLength = raw.Length,
            Rssi = -50,
            TimestampTicks = 0,
            SourceMac = "test"
        };
    }
}
