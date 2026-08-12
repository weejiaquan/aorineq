using ApoVolume.Core;
using Xunit;
using Xunit.Abstractions;

namespace ApoVolume.Tests;

public class MeterMathTests
{
    private readonly ITestOutputHelper _out;
    public MeterMathTests(ITestOutputHelper output) => _out = output;

    private static float[] Sine(double amplitude, int n = 4800)
    {
        var s = new float[n];
        for (int i = 0; i < n; i++)
            s[i] = (float)(amplitude * Math.Sin(2 * Math.PI * 100 * i / n));
        return s;
    }

    [Fact]
    public void Full_scale_sine_peaks_at_zero_dBFS_and_rms_minus_3()
    {
        var s = Sine(1.0);
        double peak = MeterMath.PeakDb(s);
        double rms = MeterMath.RmsDb(s);
        _out.WriteLine($"peak={peak:0.00} rms={rms:0.00}");
        Assert.Equal(0.0, peak, 1);
        Assert.Equal(-3.01, rms, 1);
    }

    [Fact]
    public void Half_scale_sine_peaks_at_minus_6_dBFS()
    {
        Assert.Equal(-6.02, MeterMath.PeakDb(Sine(0.5)), 1);
    }

    [Fact]
    public void Silence_and_empty_read_the_floor()
    {
        Assert.Equal(MeterMath.FloorDb, MeterMath.PeakDb(new float[128]));
        Assert.Equal(MeterMath.FloorDb, MeterMath.RmsDb(new float[128]));
        Assert.Equal(MeterMath.FloorDb, MeterMath.PeakDb(Array.Empty<float>()));
        Assert.Equal(MeterMath.FloorDb, MeterMath.RmsDb(Array.Empty<float>()));
    }

    [Fact]
    public void ClipDetector_latches_and_counts_rising_edges_only()
    {
        var clip = new ClipDetector();
        Assert.False(clip.Latched);

        clip.Observe(-3.0);
        Assert.False(clip.Latched);
        Assert.Equal(0, clip.Count);

        clip.Observe(-0.05); // >= -0.1 dBFS threshold: clipping
        clip.Observe(0.0);   // still clipping: same event, not a second count
        Assert.True(clip.Latched);
        Assert.Equal(1, clip.Count);

        clip.Observe(-12.0); // recovered
        Assert.True(clip.Latched); // latch holds until reset
        clip.Observe(-0.02); // second distinct clip event
        Assert.Equal(2, clip.Count);
        _out.WriteLine($"count={clip.Count} latched={clip.Latched}");

        clip.Reset();
        Assert.False(clip.Latched);
        Assert.Equal(0, clip.Count);
    }
}
