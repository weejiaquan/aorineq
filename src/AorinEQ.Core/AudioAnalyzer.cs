namespace AorinEQ.Core;

/// <summary>One reading of the post-EQ stream, shared by every surface that draws it: the
/// meters, the clip indicator and the spectrum all come out of the SAME analysis.
///
/// It is an immutable snapshot rather than a set of properties on the analyzer so that a consumer
/// reading it cannot be torn by the capture thread writing the next one, and so that
/// <see cref="AudioAnalyzer.Analyze"/> can hand the very same instance to every consumer in a
/// frame — which is what makes "one transform, however many widgets" observable rather than
/// hoped for.</summary>
/// <param name="SpectrumDb">Hann-windowed magnitude in dBFS, DC..Nyquist-1, from
/// <see cref="Fft.SpectrumDb"/>.</param>
/// <param name="ClipEvents">Distinct clipping events since the analyzer was created — MONOTONIC
/// and never reset. "Reset" belongs to each surface's own <see cref="HudClipLatch"/>, so the EQ
/// editor clearing its indicator cannot silently answer the levels widget's question.</param>
public sealed record AudioAnalysis(
    double PeakDbL, double PeakDbR, double RmsDbL, double RmsDbR,
    double[] SpectrumDb, int SampleRate, int ClipEvents)
{
    /// <summary>Anything above the floor on either channel — what "audio is playing" means to the
    /// show-only-while-playing behaviour.</summary>
    public bool HasSignal => PeakDbL > MeterMath.FloorDb || PeakDbR > MeterMath.FloorDb;

    public double PeakDb => Math.Max(PeakDbL, PeakDbR);
}

/// <summary>The ONE analysis behind every audio-reading surface in the app.
///
/// The capture thread <see cref="Feed"/>s decoded sample blocks in; UI timers ask for
/// <see cref="Analyze"/>. The transform runs ONCE PER ARRIVAL OF NEW SAMPLES, not once per asker:
/// the result is cached against a sample generation, so four widgets reading in the same frame
/// cost one transform between them. (Two INDEPENDENTLY TIMED surfaces — the HUD and the EQ editor
/// — can still cost one each, because samples arrive between their ticks. The guarantee is per
/// generation, not per wall-clock frame.)
///
/// THE READING IS NON-DESTRUCTIVE. Peak and RMS are measured over the analysis WINDOW that is
/// still in the ring, not accumulated-and-cleared since the last read. That distinction is the
/// whole reason two surfaces can share this: a "since you last looked" accumulator belongs to one
/// reader, and whichever of two timers got there first would silently shorten the other's
/// measurement interval and under-report its levels. A window belongs to nobody, and every reader
/// sees the same true answer. At 4096 samples it also comfortably outlasts a 30 fps frame, so no
/// transient can slip between two ticks unseen.
///
/// THREE LOCKS' WORTH OF CARE, for two threads that must not wait on each other:
///   * <c>_lock</c> guards the rings and the cache. Feed takes it; Analyze takes it only to copy
///     in and to publish out — never across the transform, which would park the WASAPI capture
///     thread behind an FFT.
///   * <c>_transformLock</c> serializes the transform itself, so two callers arriving on the same
///     generation produce ONE transform and ONE instance rather than two of each. The capture
///     thread never touches it, so serializing readers cannot delay capture.
///   * Publication is strictly monotonic in the generation, which is what stops a transform that
///     was in flight across a <see cref="Reset"/> from putting the previous device's audio back.</summary>
public sealed class AudioAnalyzer
{
    /// <summary>Analysis window. A power of two for the radix-2 transform; 4096 at 48 kHz is
    /// ~85 ms, the same trade the EQ editor has drawn its spectrum from since v2.0.0 — long enough
    /// to resolve bass, short enough to still look live.</summary>
    public const int FftSize = 4096;

    private static readonly AudioAnalysis Silent = new(
        MeterMath.FloorDb, MeterMath.FloorDb, MeterMath.FloorDb, MeterMath.FloorDb,
        SilentSpectrum(), 0, 0);

    private readonly object _lock = new();
    private readonly object _transformLock = new();
    private readonly float[] _ringL = new float[FftSize];
    private readonly float[] _ringR = new float[FftSize];
    private int _ringPos;
    /// <summary>How much of the ring holds REAL samples — everything before that is the zero fill
    /// it was created with. Without it, RMS over the whole window under-reports by however much
    /// silence has not been overwritten yet, for the first ~85 ms after every attach and every
    /// device switch, which is exactly when somebody is looking at the meters.</summary>
    private int _filled;
    private readonly ClipDetector _clip = new();
    private int _clipEvents;

    private long _generation;          // bumped by every Feed and by Reset
    private long _analyzedGeneration = -1;
    private AudioAnalysis? _cached;

    /// <summary>The capture's mix-format rate, 0 while nothing is capturing. Set by the pipeline
    /// when a capture starts, and carried into every analysis so the spectrum can be scaled.</summary>
    public int SampleRate { get; set; }

    /// <summary>How many transforms have actually been computed. Diagnostic, and the thing the
    /// one-transform-per-generation test asserts on.</summary>
    public long AnalysisCount { get; private set; }

    private static double[] SilentSpectrum()
    {
        var a = new double[FftSize / 2];
        Array.Fill(a, Fft.FloorDb);
        return a;
    }

    /// <summary>Capture-thread entry point: fold a decoded block into the analysis window.
    /// Deliberately tiny — no transform happens here, because this runs on the WASAPI capture
    /// thread and anything slow here is a dropped packet.</summary>
    public void Feed(float[] left, float[] right)
    {
        // Clipping is detected OUTSIDE the lock and on the capture side, so an event that starts
        // and ends between two frames is still counted. A UI timer that only inspected the peak it
        // happens to read would miss a clip shorter than its own interval.
        double peak = Math.Max(MeterMath.PeakDb(left), MeterMath.PeakDb(right));
        lock (_lock)
        {
            _clip.Observe(peak);
            _clipEvents = _clip.Count;

            int n = Math.Min(left.Length, right.Length);
            for (int i = 0; i < n; i++)
            {
                _ringL[_ringPos] = left[i];
                _ringR[_ringPos] = right[i];
                _ringPos = (_ringPos + 1) % FftSize;
            }
            _filled = (int)Math.Min(FftSize, (long)_filled + n);
            _generation++;
        }
    }

    /// <summary>The current reading. Returns the SAME instance to every caller until new samples
    /// arrive; only the first ask after a Feed pays for a transform.</summary>
    public AudioAnalysis Analyze()
    {
        if (TryCached() is { } quick) return quick;

        // Serialized so that two callers arriving on the same generation produce ONE transform and
        // hand back ONE instance. The capture thread never waits on this lock.
        lock (_transformLock)
        {
            if (TryCached() is { } published) return published;

            float[] left, right;
            long generation;
            int clipEvents, rate, filled;
            lock (_lock)
            {
                left = Unroll(_ringL);
                right = Unroll(_ringR);
                generation = _generation;
                clipEvents = _clipEvents;
                rate = SampleRate;
                filled = _filled;
            }

            // The expensive part, outside every lock.
            var mono = new float[FftSize];
            for (int i = 0; i < FftSize; i++)
                mono[i] = (left[i] + right[i]) * 0.5f;
            // Levels are measured over the REAL samples only. The window is unrolled oldest-first,
            // so those are the last `filled` of it.
            var validL = Tail(left, filled);
            var validR = Tail(right, filled);
            var analysis = new AudioAnalysis(
                MeterMath.PeakDb(validL), MeterMath.PeakDb(validR),
                MeterMath.RmsDb(validL), MeterMath.RmsDb(validR),
                Fft.SpectrumDb(mono), rate, clipEvents);

            lock (_lock)
            {
                AnalysisCount++;
                // STRICTLY newer only. A Reset that landed while this transform was running has
                // already published silence at a higher generation; republishing here would put
                // the previous device's audio back on screen.
                if (generation > _analyzedGeneration)
                {
                    _cached = analysis;
                    _analyzedGeneration = generation;
                    return _cached;
                }
                // Superseded: hand this caller its own (correct, if slightly stale) reading rather
                // than a newer one it did not ask for, and leave the cache alone.
                return analysis;
            }
        }
    }

    private AudioAnalysis? TryCached()
    {
        lock (_lock)
            return _cached is not null && _analyzedGeneration == _generation ? _cached : null;
    }

    /// <summary>The last <paramref name="count"/> samples of an oldest-first window.</summary>
    private static float[] Tail(float[] window, int count)
    {
        if (count >= window.Length) return window;
        if (count <= 0) return [];
        var tail = new float[count];
        Array.Copy(window, window.Length - count, tail, 0, count);
        return tail;
    }

    /// <summary>Copies a ring into a flat array with index 0 as the OLDEST sample.</summary>
    private float[] Unroll(float[] ring)
    {
        var window = new float[FftSize];
        int head = _ringPos;
        Array.Copy(ring, head, window, 0, FftSize - head);
        Array.Copy(ring, 0, window, FftSize - head, head);
        return window;
    }

    /// <summary>Clears the analysis window. Called when the capture attaches to a different
    /// endpoint: the tail of the device the user just left is not this device's signal, and
    /// leaving it in would draw a spectrum for audio that is no longer playing. The clip COUNT
    /// survives — it is monotonic by contract, and every latch is relative to it.
    ///
    /// It PUBLISHES silence rather than merely clearing the cache. That is what makes the reset
    /// win against a transform that is already in flight: the in-flight one carries an older
    /// generation, and publication is strictly monotonic, so it can no longer put the old
    /// device's audio back.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            Array.Clear(_ringL);
            Array.Clear(_ringR);
            _ringPos = 0;
            _filled = 0;
            _generation++;
            _cached = Silent with { SampleRate = SampleRate, ClipEvents = _clipEvents };
            _analyzedGeneration = _generation;
        }
    }
}

/// <summary>One surface's clip indicator, taken as a difference from the analyzer's monotonic
/// event count.
///
/// The EQ editor and the levels widget watch the same signal but answer different questions:
/// "has it clipped since I last looked". A shared, resettable detector would let one surface's
/// reset silently clear the other's, so each keeps its own baseline instead.</summary>
public sealed class HudClipLatch
{
    private int _baseline;
    private int _latest;

    public bool Latched => _latest > _baseline;

    public int Count => Math.Max(0, _latest - _baseline);

    /// <summary>Feeds the analyzer's current event count in.</summary>
    public void Observe(int clipEvents) => _latest = clipEvents;

    /// <summary>Clears this surface's indicator: everything counted so far stops being news.</summary>
    public void Reset(int clipEvents)
    {
        _baseline = clipEvents;
        _latest = clipEvents;
    }

    /// <summary>Starts from an already-running analyzer without inheriting its history — a widget
    /// opened after an hour of clipping must not come up already lit.</summary>
    public void Rebase(int clipEvents) => Reset(clipEvents);
}
