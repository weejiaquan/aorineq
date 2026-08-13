namespace AorinEQ.Core;

/// <summary>One reading of the post-EQ stream, shared by every surface that draws it: the
/// meters, the clip indicator and the spectrum all come out of the SAME analysis.
///
/// It is an immutable snapshot rather than a set of properties on the analyzer so that a consumer
/// reading it cannot be torn by the capture thread writing the next one, and so that
/// <see cref="AudioAnalyzer.Analyze"/> can hand the very same instance to every consumer in a
/// frame — which is what makes "one FFT, however many widgets" observable rather than hoped for.</summary>
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
/// <see cref="Analyze"/>. THE FFT RUNS ONCE PER ARRIVAL OF NEW SAMPLES, not once per asker: the
/// result is cached against a sample generation, so four widgets and the EQ editor all reading at
/// 30 fps cost one transform a frame between them. That is the difference between the shared
/// pipeline the design calls for and four accidental private ones, and it is the property the
/// CPU measurement in the release notes is really measuring.
///
/// Feed is called from the capture thread and Analyze from a UI thread, so everything mutable is
/// under one lock. The work under it is bounded (a copy plus, at most, one transform).</summary>
public sealed class AudioAnalyzer
{
    /// <summary>Analysis window. A power of two for the radix-2 transform; 4096 at 48 kHz is
    /// ~85 ms, the same trade the EQ editor has drawn its spectrum from since v2.0.0 — long enough
    /// to resolve bass, short enough to still look live.</summary>
    public const int FftSize = 4096;

    private readonly object _lock = new();
    private readonly float[] _ring = new float[FftSize];
    private int _ringPos;
    private readonly ClipDetector _clip = new();
    private int _clipEvents;

    private double _blockPeakL = MeterMath.FloorDb, _blockPeakR = MeterMath.FloorDb;
    private double _blockRmsL = MeterMath.FloorDb, _blockRmsR = MeterMath.FloorDb;

    private long _generation;          // bumped by every Feed
    private long _analyzedGeneration = -1;
    private AudioAnalysis? _cached;

    /// <summary>The capture's mix-format rate, 0 while nothing is capturing. Set by the pipeline
    /// when a capture starts, and carried into every analysis so the spectrum can be scaled.</summary>
    public int SampleRate { get; set; }

    /// <summary>How many transforms have actually been computed. Diagnostic, and the thing the
    /// one-FFT-per-frame test asserts on.</summary>
    public long AnalysisCount { get; private set; }

    /// <summary>Capture-thread entry point: fold a decoded block into the analysis window and the
    /// block meters. Deliberately tiny — no transform happens here, because this runs on the
    /// WASAPI capture thread and anything slow here is a dropped packet.</summary>
    public void Feed(float[] left, float[] right)
    {
        double peakL = MeterMath.PeakDb(left), rmsL = MeterMath.RmsDb(left);
        double peakR = MeterMath.PeakDb(right), rmsR = MeterMath.RmsDb(right);
        lock (_lock)
        {
            _blockPeakL = Math.Max(_blockPeakL, peakL);
            _blockPeakR = Math.Max(_blockPeakR, peakR);
            _blockRmsL = Math.Max(_blockRmsL, rmsL);
            _blockRmsR = Math.Max(_blockRmsR, rmsR);

            // Clipping is counted HERE, on the capture side, so an event between two frames is
            // still counted. A UI timer that only inspected the peak it happens to read would
            // miss a clip that started and ended inside one frame.
            _clip.Observe(Math.Max(peakL, peakR));
            _clipEvents = _clip.Count;

            int n = Math.Min(left.Length, right.Length);
            for (int i = 0; i < n; i++)
            {
                _ring[_ringPos] = (left[i] + right[i]) * 0.5f;
                _ringPos = (_ringPos + 1) % _ring.Length;
            }
            _generation++;
        }
    }

    /// <summary>The current reading. Returns the SAME instance to every caller until new samples
    /// arrive; only the first ask after a Feed pays for a transform.</summary>
    public AudioAnalysis Analyze()
    {
        lock (_lock)
        {
            if (_cached is not null && _analyzedGeneration == _generation)
                return _cached;

            var window = new float[_ring.Length];
            int head = _ringPos;
            Array.Copy(_ring, head, window, 0, _ring.Length - head);
            Array.Copy(_ring, 0, window, _ring.Length - head, head);

            var spectrum = Fft.SpectrumDb(window);
            AnalysisCount++;

            _cached = new AudioAnalysis(
                _blockPeakL, _blockPeakR, _blockRmsL, _blockRmsR, spectrum, SampleRate, _clipEvents);
            _analyzedGeneration = _generation;

            // The block meters describe the interval since the last reading, so they reset here
            // and not in Feed — a peak that arrived between two readings is carried into the next
            // one rather than lost.
            _blockPeakL = _blockPeakR = _blockRmsL = _blockRmsR = MeterMath.FloorDb;
            return _cached;
        }
    }

    /// <summary>Clears the analysis window and the block meters. Called when the capture attaches
    /// to a different endpoint: the tail of the device the user just left is not this device's
    /// signal, and leaving it in would draw a spectrum for audio that is no longer playing.
    /// The clip COUNT survives — it is monotonic by contract, and every latch is relative to it.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            Array.Clear(_ring);
            _ringPos = 0;
            _blockPeakL = _blockPeakR = _blockRmsL = _blockRmsR = MeterMath.FloorDb;
            _cached = null;
            _analyzedGeneration = -1;
            _generation++;
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
