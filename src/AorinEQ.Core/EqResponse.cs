namespace AorinEQ.Core;

/// <summary>Normalized biquad coefficients (a0 divided through).</summary>
public readonly record struct Biquad(double B0, double B1, double B2, double A1, double A2);

/// <summary>Pure frequency-response math for the EQ editor: RBJ audio-EQ-cookbook biquad
/// coefficients per band type and log-spaced magnitude evaluation. No DSP runs in the UI —
/// curves are computed here and only drawn there.</summary>
public static class EqResponse
{
    /// <summary>Display/analysis rate. EAPO runs at the device rate, but at audio band
    /// frequencies the response difference between common rates is negligible for display.</summary>
    public const double SampleRate = 48000;

    public const double MinFrequency = 20, MaxFrequency = 20000;

    /// <summary>Log-spaced frequency grid from <paramref name="from"/> to <paramref name="to"/>
    /// inclusive — the editor's x-axis sampling.</summary>
    public static double[] LogFrequencies(int count, double from = MinFrequency, double to = MaxFrequency)
    {
        if (count < 2)
            throw new ArgumentOutOfRangeException(nameof(count));
        var freqs = new double[count];
        double ratio = Math.Log(to / from);
        for (int i = 0; i < count; i++)
            freqs[i] = from * Math.Exp(ratio * i / (count - 1));
        return freqs;
    }

    /// <summary>RBJ audio-EQ-cookbook coefficients for one band at <see cref="SampleRate"/>.</summary>
    public static Biquad Coefficients(EqBand band)
    {
        double a = Math.Pow(10, band.GainDb / 40.0); // sqrt of linear gain, per cookbook
        double w0 = 2 * Math.PI * Math.Clamp(band.Fc, 1, SampleRate / 2 - 1) / SampleRate;
        double cos = Math.Cos(w0), sin = Math.Sin(w0);
        double q = Math.Max(band.Q, 1e-4);
        double alpha = sin / (2 * q);
        double b0, b1, b2, a0, a1, a2;
        switch (band.Type)
        {
            case EqBandType.Peak:
                b0 = 1 + alpha * a;
                b1 = -2 * cos;
                b2 = 1 - alpha * a;
                a0 = 1 + alpha / a;
                a1 = -2 * cos;
                a2 = 1 - alpha / a;
                break;
            case EqBandType.LowShelf:
            {
                double sqrtA = Math.Sqrt(a), twoSqrtAAlpha = 2 * sqrtA * alpha;
                b0 = a * ((a + 1) - (a - 1) * cos + twoSqrtAAlpha);
                b1 = 2 * a * ((a - 1) - (a + 1) * cos);
                b2 = a * ((a + 1) - (a - 1) * cos - twoSqrtAAlpha);
                a0 = (a + 1) + (a - 1) * cos + twoSqrtAAlpha;
                a1 = -2 * ((a - 1) + (a + 1) * cos);
                a2 = (a + 1) + (a - 1) * cos - twoSqrtAAlpha;
                break;
            }
            case EqBandType.HighShelf:
            {
                double sqrtA = Math.Sqrt(a), twoSqrtAAlpha = 2 * sqrtA * alpha;
                b0 = a * ((a + 1) + (a - 1) * cos + twoSqrtAAlpha);
                b1 = -2 * a * ((a - 1) + (a + 1) * cos);
                b2 = a * ((a + 1) + (a - 1) * cos - twoSqrtAAlpha);
                a0 = (a + 1) - (a - 1) * cos + twoSqrtAAlpha;
                a1 = 2 * ((a - 1) - (a + 1) * cos);
                a2 = (a + 1) - (a - 1) * cos - twoSqrtAAlpha;
                break;
            }
            case EqBandType.Notch:
                b0 = 1;
                b1 = -2 * cos;
                b2 = 1;
                a0 = 1 + alpha;
                a1 = -2 * cos;
                a2 = 1 - alpha;
                break;
            case EqBandType.LowPass:
                b0 = (1 - cos) / 2;
                b1 = 1 - cos;
                b2 = (1 - cos) / 2;
                a0 = 1 + alpha;
                a1 = -2 * cos;
                a2 = 1 - alpha;
                break;
            case EqBandType.HighPass:
                b0 = (1 + cos) / 2;
                b1 = -(1 + cos);
                b2 = (1 + cos) / 2;
                a0 = 1 + alpha;
                a1 = -2 * cos;
                a2 = 1 - alpha;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(band));
        }
        return new Biquad(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    /// <summary>|H(e^jω)| in dB at one frequency, from the standard closed form.</summary>
    public static double MagnitudeDb(in Biquad c, double freq)
    {
        double w = 2 * Math.PI * freq / SampleRate;
        double cos1 = Math.Cos(w), sin1 = Math.Sin(w);
        double cos2 = Math.Cos(2 * w), sin2 = Math.Sin(2 * w);
        double numRe = c.B0 + c.B1 * cos1 + c.B2 * cos2;
        double numIm = -(c.B1 * sin1 + c.B2 * sin2);
        double denRe = 1 + c.A1 * cos1 + c.A2 * cos2;
        double denIm = -(c.A1 * sin1 + c.A2 * sin2);
        double num = numRe * numRe + numIm * numIm;
        double den = denRe * denRe + denIm * denIm;
        return 10 * Math.Log10(Math.Max(num, 1e-30) / Math.Max(den, 1e-30));
    }

    /// <summary>Summed response (dB) of a band chain over the given frequencies — cascaded
    /// biquads multiply, so their dB responses add.</summary>
    public static double[] ResponseDb(IEnumerable<EqBand> bands, double[] freqs)
    {
        var result = new double[freqs.Length];
        foreach (var band in bands)
        {
            var c = Coefficients(band);
            for (int i = 0; i < freqs.Length; i++)
                result[i] += MagnitudeDb(in c, freqs[i]);
        }
        return result;
    }

    /// <summary>Clipping-prevention preamp suggestion: the negation of the summed response's
    /// maximum where positive (0 when the chain never boosts), rounded down to 0.1 dB so the
    /// suggestion never under-compensates. The search grid includes every band's exact center
    /// frequency — a narrow high-Q boost peaks AT its Fc, which a fixed log grid can miss.</summary>
    public static double SuggestPreampDb(IEnumerable<EqBand> bands)
    {
        var chain = bands as IReadOnlyList<EqBand> ?? bands.ToArray();
        var freqs = LogFrequencies(512)
            .Concat(chain.Select(b => Math.Clamp(b.Fc, MinFrequency, MaxFrequency)))
            .ToArray();
        var response = ResponseDb(chain, freqs);
        double max = response.Length == 0 ? 0 : response.Max();
        return max <= 0 ? 0 : -Math.Ceiling(max * 10) / 10.0;
    }
}
