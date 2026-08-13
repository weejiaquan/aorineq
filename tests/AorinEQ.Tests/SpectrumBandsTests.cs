using AorinEQ.Core;

namespace AorinEQ.Tests;

/// <summary>The spectrum widget's band mapping, smoothing/falloff and peak hold. Pure math over
/// the SAME log binning the EQ editor's spectrum already uses (Fft.LogBins) — this class adds the
/// per-frame ballistics a persistent widget needs and the editor's inline code did not have.</summary>
public class SpectrumBandsTests
{
    private static double[] Flat(int bins, double db)
    {
        var a = new double[bins];
        Array.Fill(a, db);
        return a;
    }

    /// <summary>A spectrum with one loud bin, at the frequency asked for.</summary>
    private static double[] Tone(int bins, double sampleRate, double hz, double db)
    {
        var a = Flat(bins, Fft.FloorDb);
        double binWidth = sampleRate / (bins * 2.0);
        a[(int)Math.Round(hz / binWidth)] = db;
        return a;
    }

    [Fact]
    public void A_new_band_set_starts_silent()
    {
        var bands = new SpectrumBands(16, smoothing: 0.5, peakDecayDbPerSecond: 20);

        Assert.Equal(16, bands.Levels.Count);
        Assert.All(bands.Levels, v => Assert.Equal(Fft.FloorDb, v));
        Assert.All(bands.Peaks, v => Assert.Equal(Fft.FloorDb, v));
    }

    [Fact]
    public void A_tone_lights_the_band_that_covers_its_frequency_and_no_other()
    {
        var bands = new SpectrumBands(24, smoothing: 0, peakDecayDbPerSecond: 20);
        const double rate = 48000;
        var spectrum = Tone(2048, rate, 1000, -6);

        bands.Update(spectrum, rate, 20, 20000, TimeSpan.FromMilliseconds(33));

        // Which band 1 kHz falls into, computed the same way the log mapping does.
        int expected = (int)(Math.Log(1000.0 / 20) / Math.Log(20000.0 / 20) * 24);
        for (int i = 0; i < bands.Levels.Count; i++)
        {
            if (i == expected)
                Assert.True(bands.Levels[i] > -12, $"band {i} should carry the tone, was {bands.Levels[i]}");
            else
                Assert.Equal(Fft.FloorDb, bands.Levels[i]);
        }
    }

    [Fact]
    public void Smoothing_zero_follows_the_input_exactly()
    {
        var bands = new SpectrumBands(8, smoothing: 0, peakDecayDbPerSecond: 20);
        const double rate = 48000;

        bands.Update(Flat(1024, -30), rate, 20, 20000, TimeSpan.FromMilliseconds(33));
        var first = bands.Levels.ToArray();
        bands.Update(Flat(1024, -30), rate, 20, 20000, TimeSpan.FromMilliseconds(33));

        Assert.Equal(first, bands.Levels.ToArray());
        Assert.All(bands.Levels, v => Assert.Equal(-30, v, 6));
    }

    [Fact]
    public void Smoothing_pulls_a_falling_band_down_gradually_instead_of_dropping_it()
    {
        var bands = new SpectrumBands(4, smoothing: 0.8, peakDecayDbPerSecond: 20);
        const double rate = 48000;
        var frame = TimeSpan.FromMilliseconds(33);

        // Settle high.
        for (int i = 0; i < 200; i++) bands.Update(Flat(1024, -10), rate, 20, 20000, frame);
        Assert.All(bands.Levels, v => Assert.Equal(-10, v, 1));

        // Then silence: the band must move DOWN, but nowhere near all the way in one frame.
        bands.Update(Flat(1024, Fft.FloorDb), rate, 20, 20000, frame);
        foreach (var v in bands.Levels)
        {
            Assert.True(v < -10, "a smoothed band must still fall");
            Assert.True(v > -40, $"one frame of smoothing must not fall off a cliff, was {v}");
        }
    }

    [Fact]
    public void Smoothing_does_not_delay_a_rise_only_a_fall()
    {
        // Instant attack, smooth release — the standard meter ballistic, and what makes a
        // spectrum look right rather than sluggish.
        var bands = new SpectrumBands(4, smoothing: 0.9, peakDecayDbPerSecond: 20);
        const double rate = 48000;

        bands.Update(Flat(1024, -6), rate, 20, 20000, TimeSpan.FromMilliseconds(33));

        Assert.All(bands.Levels, v => Assert.Equal(-6, v, 6));
    }

    [Fact]
    public void A_held_peak_sits_above_the_level_and_decays_at_the_rate_it_was_given()
    {
        var bands = new SpectrumBands(4, smoothing: 0, peakDecayDbPerSecond: 30);
        const double rate = 48000;
        var frame = TimeSpan.FromMilliseconds(100);

        bands.Update(Flat(1024, -6), rate, 20, 20000, frame);
        Assert.All(bands.Peaks, v => Assert.Equal(-6, v, 6));

        bands.Update(Flat(1024, -60), rate, 20, 20000, frame);
        // 30 dB/s over 100 ms = 3 dB.
        Assert.All(bands.Peaks, v => Assert.Equal(-9, v, 6));
        Assert.All(bands.Levels, v => Assert.Equal(-60, v, 6));
    }

    [Fact]
    public void A_peak_never_decays_below_the_level_underneath_it()
    {
        var bands = new SpectrumBands(4, smoothing: 0, peakDecayDbPerSecond: 1000);
        const double rate = 48000;

        bands.Update(Flat(1024, -6), rate, 20, 20000, TimeSpan.FromMilliseconds(100));
        bands.Update(Flat(1024, -20), rate, 20, 20000, TimeSpan.FromSeconds(5));

        Assert.All(bands.Peaks, v => Assert.Equal(-20, v, 6));
    }

    [Fact]
    public void A_longer_frame_decays_a_peak_further_than_a_short_one()
    {
        // Frame time is real and variable — a decay expressed per FRAME would run at a different
        // speed on a busy machine than on an idle one.
        var fast = new SpectrumBands(1, smoothing: 0, peakDecayDbPerSecond: 20);
        var slow = new SpectrumBands(1, smoothing: 0, peakDecayDbPerSecond: 20);
        const double rate = 48000;

        fast.Update(Flat(1024, 0), rate, 20, 20000, TimeSpan.FromMilliseconds(10));
        slow.Update(Flat(1024, 0), rate, 20, 20000, TimeSpan.FromMilliseconds(10));
        fast.Update(Flat(1024, Fft.FloorDb), rate, 20, 20000, TimeSpan.FromMilliseconds(10));
        slow.Update(Flat(1024, Fft.FloorDb), rate, 20, 20000, TimeSpan.FromMilliseconds(500));

        Assert.True(slow.Peaks[0] < fast.Peaks[0]);
    }

    [Fact]
    public void Normalized_height_maps_the_display_window_onto_zero_to_one()
    {
        Assert.Equal(0, SpectrumBands.Normalize(SpectrumBands.BottomDb, SpectrumBands.BottomDb, SpectrumBands.TopDb), 6);
        Assert.Equal(1, SpectrumBands.Normalize(SpectrumBands.TopDb, SpectrumBands.BottomDb, SpectrumBands.TopDb), 6);
        Assert.Equal(0, SpectrumBands.Normalize(-500, SpectrumBands.BottomDb, SpectrumBands.TopDb), 6);
        Assert.Equal(1, SpectrumBands.Normalize(50, SpectrumBands.BottomDb, SpectrumBands.TopDb), 6);
        Assert.Equal(0.5, SpectrumBands.Normalize(
            (SpectrumBands.BottomDb + SpectrumBands.TopDb) / 2, SpectrumBands.BottomDb, SpectrumBands.TopDb), 6);
    }

    [Fact]
    public void Resizing_the_band_count_keeps_the_set_usable_rather_than_throwing()
    {
        var bands = new SpectrumBands(8, smoothing: 0.5, peakDecayDbPerSecond: 20);
        bands.Update(Flat(1024, -10), 48000, 20, 20000, TimeSpan.FromMilliseconds(33));

        bands.Resize(32);

        Assert.Equal(32, bands.Levels.Count);
        Assert.Equal(32, bands.Peaks.Count);
        // A resize starts the new bands from silence rather than from a stale neighbour's value.
        Assert.All(bands.Levels, v => Assert.Equal(Fft.FloorDb, v));
    }

    [Fact]
    public void Resizing_to_the_same_count_keeps_the_running_state()
    {
        var bands = new SpectrumBands(8, smoothing: 0.5, peakDecayDbPerSecond: 20);
        bands.Update(Flat(1024, -10), 48000, 20, 20000, TimeSpan.FromMilliseconds(33));
        var before = bands.Levels.ToArray();

        bands.Resize(8);

        Assert.Equal(before, bands.Levels.ToArray());
    }
}
