namespace AorinEQ.Core;

/// <summary>Dependency-free radix-2 FFT and the spectrum shaping the EQ editor draws:
/// Hann-windowed magnitude in dBFS plus log-frequency banding. Pure math, no audio I/O.</summary>
public static class Fft
{
    /// <summary>Silence floor for the dB spectra (avoids -infinity; anything at or below
    /// this renders as "nothing").</summary>
    public const double FloorDb = -120;

    private const double HannCoherentGain = 0.5; // mean of the Hann window

    /// <summary>In-place iterative radix-2 Cooley–Tukey FFT. Both arrays must share one
    /// power-of-two length.</summary>
    public static void InPlace(double[] real, double[] imag)
    {
        int n = real.Length;
        if (n != imag.Length)
            throw new ArgumentException("real/imag lengths differ.");
        if (n < 2 || (n & (n - 1)) != 0)
            throw new ArgumentException("Length must be a power of two.");

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j |= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = -2 * Math.PI / len;
            double wRe = Math.Cos(angle), wIm = Math.Sin(angle);
            for (int i = 0; i < n; i += len)
            {
                double curRe = 1, curIm = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    double tRe = real[b] * curRe - imag[b] * curIm;
                    double tIm = real[b] * curIm + imag[b] * curRe;
                    real[b] = real[a] - tRe;
                    imag[b] = imag[a] - tIm;
                    real[a] += tRe;
                    imag[a] += tIm;
                    (curRe, curIm) = (curRe * wRe - curIm * wIm, curRe * wIm + curIm * wRe);
                }
            }
        }
    }

    /// <summary>Hann-windowed magnitude spectrum in dBFS: a full-scale bin-centered sine reads
    /// ~0 dBFS, a DC offset reads its own level. Returns N/2 bins (DC..Nyquist-1), floored at
    /// <see cref="FloorDb"/>. Sample count must be a power of two.</summary>
    public static double[] SpectrumDb(float[] samples)
    {
        int n = samples.Length;
        var re = new double[n];
        var im = new double[n];
        for (int i = 0; i < n; i++)
        {
            // Hann window: 0.5 (1 - cos(2πi/N)).
            double w = 0.5 * (1 - Math.Cos(2 * Math.PI * i / n));
            re[i] = samples[i] * w;
        }
        InPlace(re, im);

        var db = new double[n / 2];
        for (int k = 0; k < n / 2; k++)
        {
            double mag = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
            // Amplitude normalization: single-sided doubling for k>0, window coherent gain out.
            double amp = mag * (k == 0 ? 1.0 : 2.0) / (n * HannCoherentGain);
            db[k] = Math.Max(FloorDb, 20 * Math.Log10(Math.Max(amp, 1e-12)));
        }
        return db;
    }

    /// <summary>Groups a linear-frequency dB spectrum into <paramref name="bandCount"/>
    /// log-spaced bands from <paramref name="fMin"/> to <paramref name="fMax"/>, taking the max
    /// bin per band (peaks stay visible). Empty bands inherit the floor.</summary>
    public static double[] LogBins(double[] binDb, double sampleRate, double fMin, double fMax, int bandCount)
    {
        var bands = new double[bandCount];
        Array.Fill(bands, FloorDb);
        double binWidth = sampleRate / (binDb.Length * 2.0);
        double logSpan = Math.Log(fMax / fMin);
        for (int k = 1; k < binDb.Length; k++)
        {
            double f = k * binWidth;
            if (f < fMin || f > fMax)
                continue;
            int band = Math.Min(bandCount - 1, (int)(Math.Log(f / fMin) / logSpan * bandCount));
            bands[band] = Math.Max(bands[band], binDb[k]);
        }
        return bands;
    }
}
