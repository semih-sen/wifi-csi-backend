using CsiRadar.Backend.Application.Processing.Dsp;
using Xunit;

namespace CsiRadar.Backend.Tests;

/// <summary>
/// Unit tests for the Phase 2 per-RX DSP primitives. Parity against the Python twin
/// is covered separately by the golden fixture (<see cref="DspGoldenParityTests"/> +
/// wifi-csi-ml/tests/test_dsp_parity.py); these pin the maths itself.
/// </summary>
public class DspTests
{
    [Fact]
    public void Amplitude_IsExactSqrtOfIQ()
    {
        // [imag, real] pairs: (3,4)->5, (0,0)->0, (-5,12)->13, (127,0)->127.
        sbyte[] raw = [3, 4, 0, 0, -5, 12, 127, 0];
        var amp = new float[4];

        int n = CsiDsp.Amplitude(raw, amp);

        Assert.Equal(4, n);
        Assert.Equal(5f, amp[0]);
        Assert.Equal(0f, amp[1]);
        Assert.Equal(13f, amp[2]);
        Assert.Equal(127f, amp[3]);
    }

    [Fact]
    public void Unwrap_RemovesTwoPiJumps()
    {
        // A ramp that wraps once: … 3.0, -3.0 (jump of -6.0 ≈ -2π) should become continuous.
        double[] p = [0.0, 3.0, -3.0 + 2 * Math.PI * 0, 3.0];
        // Build a clean wrapped ramp instead: true = 0,1,2,3,4,5 rad wrapped to (-π,π].
        double[] trueRamp = [0, 1, 2, 3, 4, 5, 6, 7];
        var wrapped = new double[trueRamp.Length];
        for (int i = 0; i < trueRamp.Length; i++)
        {
            double x = trueRamp[i];
            // wrap into (-π, π]
            x = x - 2 * Math.PI * Math.Floor((x + Math.PI) / (2 * Math.PI));
            wrapped[i] = x;
        }

        CsiDsp.UnwrapInPlace(wrapped);

        for (int i = 0; i < trueRamp.Length; i++)
            Assert.True(Math.Abs(wrapped[i] - trueRamp[i]) < 1e-9,
                $"index {i}: {wrapped[i]} != {trueRamp[i]}");
    }

    [Fact]
    public void Detrend_ZeroesAPureLinearRamp()
    {
        // A perfect line 2k+7 has zero residual after least-squares detrend.
        var p = new double[16];
        for (int k = 0; k < p.Length; k++)
            p[k] = 2.0 * k + 7.0;

        CsiDsp.DetrendLeastSquaresInPlace(p);

        foreach (double v in p)
            Assert.True(Math.Abs(v) < 1e-9, $"residual {v} not ~0");
    }

    [Fact]
    public void SanitizedPhase_OfLinearPhaseSignal_IsNearZero()
    {
        // Construct I/Q with a linear phase slope across subcarriers; sanitization
        // (unwrap + detrend) should collapse it to ~0 residual.
        int n = 64;
        var raw = new sbyte[2 * n];
        for (int k = 0; k < n; k++)
        {
            double phi = 0.3 * k;               // linear phase
            raw[2 * k] = (sbyte)Math.Round(60 * Math.Sin(phi));  // imag
            raw[2 * k + 1] = (sbyte)Math.Round(60 * Math.Cos(phi)); // real
        }
        var phase = new float[n];

        CsiDsp.SanitizedPhase(raw, phase);

        // Residual is small (quantization to int8 leaves a little, but no linear trend).
        double maxAbs = 0;
        foreach (float v in phase) maxAbs = Math.Max(maxAbs, Math.Abs(v));
        Assert.True(maxAbs < 0.15, $"max residual {maxAbs} too large for a linear-phase input");
    }

    [Fact]
    public void Stft_Shape_MatchesContract()
    {
        var series = new double[128];
        for (int nn = 0; nn < series.Length; nn++)
            series[nn] = Math.Sin(2 * Math.PI * 8 * nn / DspContract.StftWindowSize);

        float[,] spec = StftProcessor.Spectrogram(series);

        int expectedFrames = (128 - DspContract.StftWindowSize) / DspContract.StftHopSize + 1;
        Assert.Equal(expectedFrames, spec.GetLength(0));
        Assert.Equal(DspContract.StftBins, spec.GetLength(1));
    }

    [Fact]
    public void Stft_PureTone_PeaksAtItsBin()
    {
        // A tone at exactly bin 8 (integer cycles per window) should peak in bin 8.
        const int bin = 8;
        var series = new double[DspContract.StftWindowSize];
        for (int nn = 0; nn < series.Length; nn++)
            series[nn] = Math.Cos(2 * Math.PI * bin * nn / DspContract.StftWindowSize);

        var col = new float[DspContract.StftBins];
        StftProcessor.MagnitudeColumn(series, col);

        int peak = 0;
        for (int m = 1; m < col.Length; m++)
            if (col[m] > col[peak]) peak = m;
        Assert.Equal(bin, peak);
    }

    [Fact]
    public void StreamingColumn_EqualsLastSpectrogramRow()
    {
        // The runtime path calls MagnitudeColumn on the most-recent window; that must
        // equal the last row Spectrogram() produces over the same series.
        var series = new double[128];
        for (int nn = 0; nn < series.Length; nn++)
            series[nn] = 17 * Math.Sin(0.11 * nn) + 5 * Math.Cos(0.37 * nn);

        float[,] spec = StftProcessor.Spectrogram(series);
        int lastRow = spec.GetLength(0) - 1;
        int lastStart = lastRow * DspContract.StftHopSize;

        var col = new float[DspContract.StftBins];
        StftProcessor.MagnitudeColumn(
            series.AsSpan(lastStart, DspContract.StftWindowSize), col);

        for (int m = 0; m < DspContract.StftBins; m++)
            Assert.Equal(spec[lastRow, m], col[m]);
    }
}
