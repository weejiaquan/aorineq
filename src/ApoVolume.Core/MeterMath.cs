namespace ApoVolume.Core;

/// <summary>Pure level math for the EQ window's meters, fed by loopback sample blocks.</summary>
public static class MeterMath
{
    public const double FloorDb = -120;

    /// <summary>Peaks at or above this are treated as clipping (near full scale).</summary>
    public const double ClipThresholdDb = -0.1;

    public static double PeakDb(float[] samples)
    {
        float peak = 0;
        foreach (var s in samples)
        {
            float abs = Math.Abs(s);
            if (abs > peak) peak = abs;
        }
        return ToDb(peak);
    }

    public static double RmsDb(float[] samples)
    {
        if (samples.Length == 0)
            return FloorDb;
        double sum = 0;
        foreach (var s in samples)
            sum += (double)s * s;
        return ToDb(Math.Sqrt(sum / samples.Length));
    }

    private static double ToDb(double linear) =>
        linear <= 0 ? FloorDb : Math.Max(FloorDb, 20 * Math.Log10(linear));
}

/// <summary>Clip indicator state: latches on the first near-full-scale peak until the user
/// resets it, counting distinct clip events (a run of consecutive clipping blocks is one
/// event — the count answers "how many times did it clip", not "for how long").</summary>
public sealed class ClipDetector
{
    private bool _inClip;

    public bool Latched { get; private set; }
    public int Count { get; private set; }

    public void Observe(double peakDb)
    {
        bool clipping = peakDb >= MeterMath.ClipThresholdDb;
        if (clipping && !_inClip)
        {
            Latched = true;
            Count++;
        }
        _inClip = clipping;
    }

    public void Reset()
    {
        Latched = false;
        Count = 0;
        _inClip = false;
    }
}
