using ApoVolume.Core;
using Xunit;
using Xunit.Abstractions;

namespace ApoVolume.Tests;

public class FftTests
{
    private readonly ITestOutputHelper _out;
    public FftTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Transform_of_dc_puts_all_energy_in_bin_zero()
    {
        int n = 1024;
        var re = new double[n];
        var im = new double[n];
        Array.Fill(re, 1.0);
        Fft.InPlace(re, im);
        double bin0 = Math.Sqrt(re[0] * re[0] + im[0] * im[0]);
        double bin5 = Math.Sqrt(re[5] * re[5] + im[5] * im[5]);
        _out.WriteLine($"DC: bin0={bin0} bin5={bin5}");
        Assert.Equal(n, bin0, 6);
        Assert.True(bin5 < 1e-9, $"leakage {bin5}");
    }

    [Fact]
    public void Transform_of_bin_centered_sine_peaks_at_that_bin()
    {
        int n = 1024, k = 37;
        var re = new double[n];
        var im = new double[n];
        for (int i = 0; i < n; i++)
            re[i] = Math.Sin(2 * Math.PI * k * i / n);
        Fft.InPlace(re, im);
        double atK = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
        double atOther = Math.Sqrt(re[k + 3] * re[k + 3] + im[k + 3] * im[k + 3]);
        _out.WriteLine($"sine bin {k}: |X[k]|={atK} |X[k+3]|={atOther}");
        Assert.Equal(n / 2.0, atK, 6); // amplitude 1 sine -> N/2 at the bin
        Assert.True(atOther < 1e-9, $"leakage {atOther}");
    }

    [Fact]
    public void InPlace_rejects_non_power_of_two_lengths()
    {
        Assert.Throws<ArgumentException>(() => Fft.InPlace(new double[100], new double[100]));
        Assert.Throws<ArgumentException>(() => Fft.InPlace(new double[8], new double[4]));
    }

    [Fact]
    public void SpectrumDb_full_scale_bin_centered_sine_reads_zero_dBFS()
    {
        int n = 4096, k = 64;
        var samples = new float[n];
        for (int i = 0; i < n; i++)
            samples[i] = (float)Math.Sin(2 * Math.PI * k * i / n);
        var db = Fft.SpectrumDb(samples);
        _out.WriteLine($"bins={db.Length} peak@{k}={db[k]:0.00} dBFS; @{k + 20}={db[k + 20]:0.0}");
        Assert.Equal(n / 2, db.Length);
        Assert.Equal(0.0, db[k], 1);              // Hann-corrected full scale
        Assert.True(db[k + 20] < -60, $"far bin {db[k + 20]}"); // window sidelobes well down
    }

    [Fact]
    public void SpectrumDb_dc_level_reads_its_dBFS_value()
    {
        int n = 4096;
        var samples = new float[n];
        Array.Fill(samples, 0.5f);
        var db = Fft.SpectrumDb(samples);
        _out.WriteLine($"DC 0.5 -> bin0 {db[0]:0.00} dBFS");
        Assert.Equal(20 * Math.Log10(0.5), db[0], 1); // -6.02 dBFS
    }

    [Fact]
    public void SpectrumDb_silence_reads_the_floor()
    {
        var db = Fft.SpectrumDb(new float[4096]);
        _out.WriteLine($"silence -> {db[100]:0.0} dBFS");
        Assert.True(db[100] <= Fft.FloorDb);
    }

    [Fact]
    public void LogBins_group_linear_bins_into_log_bands_taking_the_max()
    {
        int n = 4096, k = 64; // 64 * 48000/4096 = 750 Hz
        var samples = new float[n];
        for (int i = 0; i < n; i++)
            samples[i] = (float)Math.Sin(2 * Math.PI * k * i / n);
        var db = Fft.SpectrumDb(samples);
        var bands = Fft.LogBins(db, 48000, 20, 20000, 64);
        Assert.Equal(64, bands.Length);
        int peakBand = Array.IndexOf(bands, bands.Max());
        // 750 Hz sits at log position ln(750/20)/ln(20000/20) ~= 0.525 -> band ~33/64
        double logPos = Math.Log(750.0 / 20) / Math.Log(20000.0 / 20) * 64;
        _out.WriteLine($"peak band {peakBand}, expected near {logPos:0.0}; value {bands[peakBand]:0.00}");
        Assert.InRange(peakBand, (int)logPos - 1, (int)logPos + 1);
        Assert.Equal(0.0, bands[peakBand], 1);
    }
}
