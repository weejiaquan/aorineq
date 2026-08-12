using ApoVolume.Core;
using Xunit;
using Xunit.Abstractions;

namespace ApoVolume.Tests;

public class EqResponseTests
{
    private readonly ITestOutputHelper _out;
    public EqResponseTests(ITestOutputHelper output) => _out = output;

    private static double At(EqBand band, double freq) =>
        EqResponse.ResponseDb(new[] { band }, new[] { freq })[0];

    [Fact]
    public void Peak_response_hits_gain_at_fc_and_zero_at_extremes()
    {
        var band = new EqBand(EqBandType.Peak, 1000, 6.0, 1.0);
        double atFc = At(band, 1000);
        double atLow = At(band, 20);
        double atHigh = At(band, 20000);
        _out.WriteLine($"PK 1k +6 Q1: @1k={atFc:0.000} @20={atLow:0.000} @20k={atHigh:0.000}");
        Assert.Equal(6.0, atFc, 2);
        Assert.True(Math.Abs(atLow) < 0.1, $"low extreme {atLow}");
        Assert.True(Math.Abs(atHigh) < 0.35, $"high extreme {atHigh}"); // 20k near Nyquist warps slightly
    }

    [Fact]
    public void Cut_peak_is_symmetric_to_boost()
    {
        var boost = new EqBand(EqBandType.Peak, 1000, 6.0, 1.0);
        var cut = new EqBand(EqBandType.Peak, 1000, -6.0, 1.0);
        foreach (var f in new[] { 500.0, 1000, 2000 })
        {
            double b = At(boost, f), c = At(cut, f);
            _out.WriteLine($"@{f}: boost={b:0.000} cut={c:0.000}");
            Assert.Equal(b, -c, 2);
        }
    }

    [Fact]
    public void Low_shelf_asymptotes_gain_below_and_zero_above()
    {
        var band = new EqBand(EqBandType.LowShelf, 200, 6.0, 0.7);
        double low = At(band, 20);
        double atFc = At(band, 200);
        double high = At(band, 10000);
        _out.WriteLine($"LSC 200 +6: @20={low:0.000} @200={atFc:0.000} @10k={high:0.000}");
        Assert.Equal(6.0, low, 1);
        Assert.Equal(3.0, atFc, 1);   // RBJ shelf midpoint gain at fc
        Assert.True(Math.Abs(high) < 0.1);
    }

    [Fact]
    public void High_shelf_asymptotes_gain_above_and_zero_below()
    {
        var band = new EqBand(EqBandType.HighShelf, 8000, -4.0, 0.7);
        double low = At(band, 100);
        double high = At(band, 20000);
        _out.WriteLine($"HSC 8k -4: @100={low:0.000} @20k={high:0.000}");
        Assert.True(Math.Abs(low) < 0.1);
        Assert.Equal(-4.0, high, 1);
    }

    [Fact]
    public void Notch_cuts_deeply_at_fc_and_recovers_away()
    {
        var band = new EqBand(EqBandType.Notch, 1000, 0, 30);
        double atFc = At(band, 1000);
        double away = At(band, 2000);
        _out.WriteLine($"NO 1k Q30: @1k={atFc:0.0} @2k={away:0.000}");
        Assert.True(atFc < -30, $"notch depth {atFc}");
        Assert.True(Math.Abs(away) < 0.5);
    }

    [Fact]
    public void Butterworth_lowpass_is_minus_3dB_at_fc()
    {
        var band = new EqBand(EqBandType.LowPass, 1000, 0, 0.7071);
        double atFc = At(band, 1000);
        double high = At(band, 8000);
        _out.WriteLine($"LP 1k: @1k={atFc:0.000} @8k={high:0.0}");
        Assert.Equal(-3.01, atFc, 1);
        Assert.True(high < -30, $"stopband {high}");
    }

    [Fact]
    public void Butterworth_highpass_is_minus_3dB_at_fc()
    {
        var band = new EqBand(EqBandType.HighPass, 1000, 0, 0.7071);
        double atFc = At(band, 1000);
        double low = At(band, 125);
        _out.WriteLine($"HP 1k: @1k={atFc:0.000} @125={low:0.0}");
        Assert.Equal(-3.01, atFc, 1);
        Assert.True(low < -30, $"stopband {low}");
    }

    [Fact]
    public void Multiple_bands_sum_in_dB()
    {
        var a = new EqBand(EqBandType.Peak, 1000, 6.0, 1.0);
        var b = new EqBand(EqBandType.Peak, 1000, -2.0, 1.0);
        double summed = EqResponse.ResponseDb(new[] { a, b }, new[] { 1000.0 })[0];
        _out.WriteLine($"+6 and -2 at same fc -> {summed:0.000}");
        Assert.Equal(4.0, summed, 2);
    }

    [Fact]
    public void LogFrequencies_span_the_audio_band_logarithmically()
    {
        var freqs = EqResponse.LogFrequencies(256);
        _out.WriteLine($"first={freqs[0]} last={freqs[^1]} count={freqs.Length}");
        Assert.Equal(256, freqs.Length);
        Assert.Equal(20.0, freqs[0], 6);
        Assert.Equal(20000.0, freqs[^1], 3);
        // Log spacing: ratio between consecutive points is constant.
        double r1 = freqs[1] / freqs[0];
        double r2 = freqs[128] / freqs[127];
        Assert.Equal(r1, r2, 6);
    }

    [Fact]
    public void SuggestPreamp_negates_the_max_positive_response()
    {
        var boost = new EqBand(EqBandType.Peak, 1000, 6.0, 1.0);
        double suggestion = EqResponse.SuggestPreampDb(new[] { boost });
        _out.WriteLine($"single +6 peak -> {suggestion:0.00}");
        Assert.Equal(-6.0, suggestion, 1);

        var cutOnly = new EqBand(EqBandType.Peak, 1000, -6.0, 1.0);
        Assert.Equal(0.0, EqResponse.SuggestPreampDb(new[] { cutOnly }), 3);

        Assert.Equal(0.0, EqResponse.SuggestPreampDb(Array.Empty<EqBand>()), 3);
    }
}
