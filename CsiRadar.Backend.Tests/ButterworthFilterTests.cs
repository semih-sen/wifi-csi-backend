using System.Numerics;
using CsiRadar.Backend.Application.Processing;

namespace CsiRadar.Backend.Tests;

/// <summary>
/// Validates the in-house Butterworth biquad cascade against the analytical
/// Butterworth magnitude response (unit DC gain, −3 dB at the cutoff, monotonic
/// roll-off into the stopband). The transfer function is evaluated directly from
/// the section coefficients, so there are no transient/settling concerns.
/// </summary>
public class ButterworthFilterTests
{
    private const double Fs = 100.0;
    private const double Fc = 20.0;
    private const int Order = 4;

    private static (int sections, float[] b0, float[] b1, float[] b2, float[] a1, float[] a2) Coeffs()
        => CsiStreamProcessor.ComputeButterworthCascade(Fs, Fc, Order);

    /// <summary>
    /// |H(e^{jω})| of the full cascade at a given frequency, evaluated exactly
    /// from the coefficients: H(z) = Π_s (b0 + b1 z⁻¹ + b2 z⁻²)/(1 + a1 z⁻¹ + a2 z⁻²).
    /// </summary>
    private static double Magnitude(
        double freqHz,
        (int sections, float[] b0, float[] b1, float[] b2, float[] a1, float[] a2) c)
    {
        double omega = 2.0 * Math.PI * freqHz / Fs;
        Complex z1 = Complex.FromPolarCoordinates(1.0, -omega); // z⁻¹
        Complex z2 = z1 * z1;                                    // z⁻²

        Complex h = Complex.One;
        for (int s = 0; s < c.sections; s++)
        {
            Complex num = c.b0[s] + c.b1[s] * z1 + c.b2[s] * z2;
            Complex den = Complex.One + c.a1[s] * z1 + c.a2[s] * z2;
            h *= num / den;
        }
        return h.Magnitude;
    }

    [Fact]
    public void Order4_ProducesTwoSections()
    {
        Assert.Equal(2, Coeffs().sections);
    }

    [Fact]
    public void OddOrder_IsRoundedUpToEvenCascade()
    {
        // Order 3 → 2 sections (rounded up to 4) so the realization stays a biquad cascade.
        Assert.Equal(2, CsiStreamProcessor.ComputeButterworthCascade(Fs, Fc, 3).sectionCount);
    }

    [Fact]
    public void DcGain_IsUnity()
    {
        Assert.Equal(1.0, Magnitude(0.0, Coeffs()), precision: 4);
    }

    [Fact]
    public void GainAtCutoff_IsMinus3dB()
    {
        // 1/sqrt(2) ≈ 0.70711. Bilinear prewarp places −3 dB exactly at Fc.
        double mag = Magnitude(Fc, Coeffs());
        Assert.InRange(mag, 0.702, 0.712);
    }

    [Fact]
    public void Passband_IsNearUnity()
    {
        var c = Coeffs();
        Assert.True(Magnitude(5.0, c) > 0.98, $"5 Hz gain {Magnitude(5.0, c)}");
        Assert.True(Magnitude(10.0, c) > 0.95, $"10 Hz gain {Magnitude(10.0, c)}");
    }

    [Fact]
    public void Stopband_IsStronglyAttenuated()
    {
        var c = Coeffs();
        Assert.True(Magnitude(40.0, c) < 0.05, $"40 Hz gain {Magnitude(40.0, c)}");
        Assert.True(Magnitude(45.0, c) < Magnitude(40.0, c));
    }

    [Fact]
    public void Magnitude_IsMonotonicallyDecreasing()
    {
        var c = Coeffs();
        double prev = Magnitude(0.0, c);
        for (double f = 1.0; f <= 49.0; f += 1.0)
        {
            double m = Magnitude(f, c);
            Assert.True(m <= prev + 1e-9, $"Not monotonic at {f} Hz: {m} > {prev}");
            prev = m;
        }
    }

    [Fact]
    public void ProcessFrame_PassesDc()
    {
        // Constant amplitude must pass through at unity gain once the filter settles.
        var proc = TestFrames.NewProcessor(Fs, Fc, Order);
        var dst = new float[1];
        float last = 0f;
        for (int n = 0; n < 400; n++)
        {
            proc.ProcessFrame(TestFrames.FromAmplitudes(100), dst);
            last = dst[0];
        }
        Assert.InRange(last, 99.0f, 101.0f);
    }

    [Fact]
    public void ProcessFrame_AttenuatesHighFrequencyRipple()
    {
        // A 50 Hz (Nyquist) square ripple around a DC level must be heavily damped.
        var proc = TestFrames.NewProcessor(Fs, Fc, Order);
        var dst = new float[1];

        for (int n = 0; n < 400; n++)                 // settle on DC = 80
            proc.ProcessFrame(TestFrames.FromAmplitudes(80), dst);

        float min = float.MaxValue, max = float.MinValue;
        for (int n = 0; n < 200; n++)
        {
            int amp = (n % 2 == 0) ? 40 : 120;        // p2p 80 at Fs/2
            proc.ProcessFrame(TestFrames.FromAmplitudes(amp), dst);
            if (n > 100) { min = Math.Min(min, dst[0]); max = Math.Max(max, dst[0]); }
        }

        Assert.True(max - min < 8.0f, $"50 Hz ripple not attenuated: output p2p = {max - min}");
    }
}
