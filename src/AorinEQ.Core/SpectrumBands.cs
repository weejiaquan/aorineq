namespace AorinEQ.Core;

/// <summary>The spectrum widget's bar values: log-spaced bands over the shared analysis, with the
/// ballistics a persistent widget needs.
///
/// The BANDING is <see cref="Fft.LogBins"/> — the same mapping the EQ editor's spectrum has drawn
/// since v2.0.0, not a second one. What is new here is what a widget that sits on screen all day
/// needs and a transient plot did not: a smoothed fall so the bars do not flicker, and an optional
/// held peak that decays in real time.
///
/// Decay is per SECOND and takes the elapsed frame time, not per frame. A per-frame decay runs at
/// a different speed on a busy machine than on an idle one, which is exactly the sort of thing
/// that looks like a rendering bug and is really a clock.</summary>
public sealed class SpectrumBands
{
    /// <summary>The display window. Below <see cref="BottomDb"/> a band is empty; at
    /// <see cref="TopDb"/> it is full height.</summary>
    public const double BottomDb = -78;
    public const double TopDb = 0;

    private double[] _levels;
    private double[] _peaks;

    /// <summary>0 follows the input exactly; approaching 1 lengthens the fall. Attack is always
    /// instant — a meter that lags a transient reads as broken.</summary>
    public double Smoothing { get; set; }

    public double PeakDecayDbPerSecond { get; set; }

    public SpectrumBands(int bandCount, double smoothing, double peakDecayDbPerSecond)
    {
        int n = Math.Clamp(bandCount, HudWidget.MinBands, HudWidget.MaxBands);
        _levels = Filled(n);
        _peaks = Filled(n);
        Smoothing = Math.Clamp(smoothing, 0, 1);
        PeakDecayDbPerSecond = peakDecayDbPerSecond > 0 ? peakDecayDbPerSecond : 24;
    }

    public IReadOnlyList<double> Levels => _levels;

    public IReadOnlyList<double> Peaks => _peaks;

    /// <summary>Bands at the silence floor — the starting state, and the target for a frame with
    /// no analysis behind it.</summary>
    private static double[] Filled(int n)
    {
        var a = new double[n];
        Array.Fill(a, Fft.FloorDb);
        return a;
    }

    /// <summary>Changes the band count, starting the new set from silence. Interpolating the old
    /// values across a new count would show a spectrum that no measurement produced, for one
    /// frame, every time the user drags the slider.</summary>
    public void Resize(int bandCount)
    {
        int n = Math.Clamp(bandCount, HudWidget.MinBands, HudWidget.MaxBands);
        if (n == _levels.Length) return;
        _levels = Filled(n);
        _peaks = Filled(n);
    }

    /// <summary>Folds one analysis into the bars.
    ///
    /// With NO analysis to fold — the capture is stopped, or failed to attach — the ballistics
    /// still run, against silence. Returning early instead would freeze the bars at whatever
    /// height they last reached, which reads as a hung widget rather than as an idle one.</summary>
    /// <param name="binDb">A linear-frequency dB spectrum — <see cref="AudioAnalysis.SpectrumDb"/>.
    /// Empty means "no signal at all", not "nothing to do".</param>
    /// <param name="elapsed">Real time since the previous update, for the peak decay.</param>
    public void Update(double[] binDb, double sampleRate, double fMin, double fMax, TimeSpan elapsed)
    {
        var target = sampleRate > 0 && binDb.Length > 0
            ? Fft.LogBins(binDb, sampleRate, fMin, fMax, _levels.Length)
            : Filled(_levels.Length);
        double decay = PeakDecayDbPerSecond * Math.Max(0, elapsed.TotalSeconds);

        for (int i = 0; i < _levels.Length; i++)
        {
            double next = target[i];
            // Instant attack, smoothed release.
            _levels[i] = next >= _levels[i]
                ? next
                : _levels[i] + (next - _levels[i]) * (1 - Smoothing);

            _peaks[i] = Math.Max(_levels[i], Math.Max(_peaks[i] - decay, Fft.FloorDb));
        }
    }

    /// <summary>Maps a dB value onto 0..1 for drawing, clamped at both ends.</summary>
    public static double Normalize(double db, double bottomDb, double topDb)
    {
        if (topDb <= bottomDb) return 0;
        return Math.Clamp((db - bottomDb) / (topDb - bottomDb), 0, 1);
    }

    /// <summary>Bar height fraction for band <paramref name="index"/>.</summary>
    public double LevelFraction(int index) => Normalize(_levels[index], BottomDb, TopDb);

    /// <summary>Held-peak height fraction for band <paramref name="index"/>.</summary>
    public double PeakFraction(int index) => Normalize(_peaks[index], BottomDb, TopDb);
}
