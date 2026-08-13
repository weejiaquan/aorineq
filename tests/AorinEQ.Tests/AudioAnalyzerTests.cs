using AorinEQ.Core;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>The ONE analysis every audio-reading surface shares. Fed with real sample buffers —
/// the same float arrays a loopback packet decodes into — so the whole path is exercised without
/// needing an audio device for the maths.
///
/// The property that matters most here is the CACHE: however many widgets ask, the FFT runs once
/// per arrival of new samples. If that ever regresses, the CPU measurement in the PR is where it
/// would show, and this is the test that would catch it first.</summary>
public class AudioAnalyzerTests
{
    private readonly ITestOutputHelper _out;
    public AudioAnalyzerTests(ITestOutputHelper output) => _out = output;

    private static (float[] L, float[] R) Sine(double hz, double rate, int frames, double amplitude = 0.5)
    {
        var l = new float[frames];
        var r = new float[frames];
        for (int i = 0; i < frames; i++)
            l[i] = r[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / rate));
        return (l, r);
    }

    private static (float[] L, float[] R) Silence(int frames) => (new float[frames], new float[frames]);

    [Fact]
    public void With_nothing_fed_the_analysis_is_silent_and_says_so()
    {
        var a = new AudioAnalyzer();

        var snap = a.Analyze();

        Assert.Equal(MeterMath.FloorDb, snap.PeakDbL);
        Assert.Equal(MeterMath.FloorDb, snap.PeakDbR);
        Assert.Equal(MeterMath.FloorDb, snap.RmsDbL);
        Assert.Equal(0, snap.ClipEvents);
        Assert.False(snap.HasSignal);
        Assert.Equal(AudioAnalyzer.FftSize / 2, snap.SpectrumDb.Length);
    }

    [Fact]
    public void A_full_scale_tone_reads_at_its_own_level_on_both_meters()
    {
        var a = new AudioAnalyzer { SampleRate = 48000 };
        var (l, r) = Sine(1000, 48000, 4096, amplitude: 0.5); // -6.02 dBFS

        a.Feed(l, r);
        var snap = a.Analyze();

        _out.WriteLine($"peak {snap.PeakDbL:0.00} rms {snap.RmsDbL:0.00}");
        Assert.Equal(-6.02, snap.PeakDbL, 1);
        Assert.Equal(-6.02, snap.PeakDbR, 1);
        Assert.Equal(-9.03, snap.RmsDbL, 1); // a sine's RMS is peak/sqrt(2)
        Assert.True(snap.HasSignal);
    }

    [Fact]
    public void The_spectrum_puts_a_tone_at_its_own_frequency()
    {
        var a = new AudioAnalyzer { SampleRate = 48000 };
        var (l, r) = Sine(1000, 48000, AudioAnalyzer.FftSize, amplitude: 1.0);

        a.Feed(l, r);
        var snap = a.Analyze();

        double binWidth = 48000.0 / AudioAnalyzer.FftSize;
        int loudest = 0;
        for (int i = 1; i < snap.SpectrumDb.Length; i++)
            if (snap.SpectrumDb[i] > snap.SpectrumDb[loudest]) loudest = i;

        _out.WriteLine($"loudest bin {loudest} = {loudest * binWidth:0} Hz at {snap.SpectrumDb[loudest]:0.0} dB");
        Assert.Equal(1000, loudest * binWidth, 1.5 * binWidth);
        Assert.True(snap.SpectrumDb[loudest] > -3, "a full-scale tone should read near 0 dBFS");
    }

    [Fact]
    public void The_fft_runs_ONCE_no_matter_how_many_consumers_ask_between_arrivals()
    {
        // THE shared-pipeline guarantee. Four widgets at 30 fps must not mean four FFTs a frame.
        var a = new AudioAnalyzer { SampleRate = 48000 };
        var (l, r) = Sine(1000, 48000, 2048);
        a.Feed(l, r);

        long before = a.AnalysisCount;
        var first = a.Analyze();
        for (int i = 0; i < 20; i++) Assert.Same(first, a.Analyze());

        Assert.Equal(before + 1, a.AnalysisCount);
    }

    [Fact]
    public void New_samples_invalidate_the_cache_so_the_next_ask_is_fresh()
    {
        var a = new AudioAnalyzer { SampleRate = 48000 };
        a.Feed(Silence(2048).L, Silence(2048).R);
        var quiet = a.Analyze();

        var (l, r) = Sine(1000, 48000, 2048);
        a.Feed(l, r);
        var loud = a.Analyze();

        Assert.NotSame(quiet, loud);
        Assert.True(loud.PeakDbL > quiet.PeakDbL + 40);
    }

    [Fact]
    public void Peaks_between_two_asks_are_carried_into_the_next_analysis_not_lost()
    {
        // The meters must see the loudest thing that happened since they last looked, not just
        // whatever the final packet held.
        var a = new AudioAnalyzer { SampleRate = 48000 };
        a.Analyze();

        var (loudL, loudR) = Sine(1000, 48000, 512, amplitude: 0.9);
        a.Feed(loudL, loudR);
        a.Feed(Silence(512).L, Silence(512).R);
        a.Feed(Silence(512).L, Silence(512).R);

        Assert.Equal(-0.92, a.Analyze().PeakDbL, 1);
    }

    [Fact]
    public void Reading_an_analysis_does_not_consume_it_so_a_second_surface_sees_the_same_levels()
    {
        // THE property that lets two independently-timed surfaces share one analyzer. A
        // "since you last looked" accumulator belongs to ONE reader: whichever of the HUD's timer
        // and the EQ editor's got there first would clear it, and the other would silently
        // under-report every level it drew. The reading is over the analysis WINDOW instead, so it
        // belongs to nobody and both see the same true answer.
        var a = new AudioAnalyzer { SampleRate = 48000 };
        var (l, r) = Sine(1000, 48000, 2048, amplitude: 0.9);
        a.Feed(l, r);

        var first = a.Analyze();
        a.Feed(Silence(64).L, Silence(64).R);   // a new generation, so the cache cannot answer
        var second = a.Analyze();

        Assert.Equal(-0.92, first.PeakDbL, 1);
        Assert.Equal(first.PeakDbL, second.PeakDbL, 6);
        Assert.Equal(first.RmsDbL, second.RmsDbL, 1);
    }

    [Fact]
    public void A_peak_leaves_the_reading_only_once_it_has_fallen_out_of_the_window()
    {
        // The flip side: the meters are not a latch either. Once the loud samples have been
        // pushed out of the window by silence, the level really does read silent.
        var a = new AudioAnalyzer { SampleRate = 48000 };
        var (l, r) = Sine(1000, 48000, 512, amplitude: 0.9);
        a.Feed(l, r);
        Assert.Equal(-0.92, a.Analyze().PeakDbL, 1);

        a.Feed(Silence(AudioAnalyzer.FftSize).L, Silence(AudioAnalyzer.FftSize).R);

        Assert.Equal(MeterMath.FloorDb, a.Analyze().PeakDbL);
    }

    [Fact]
    public void The_window_outlasts_a_frame_so_nothing_can_slip_between_two_ticks_unseen()
    {
        // 4096 samples is ~85 ms at 48 kHz, comfortably longer than a 30 fps frame (33 ms) — a
        // transient shorter than a frame is still in the window when the next tick reads it.
        Assert.True(AudioAnalyzer.FftSize / 48000.0 > 2 * (1.0 / 30));

        var a = new AudioAnalyzer { SampleRate = 48000 };
        var (l, r) = Sine(1000, 48000, 64, amplitude: 0.9); // ~1.3 ms of signal
        a.Feed(l, r);
        // A whole frame of silence at 48 kHz goes by...
        a.Feed(Silence(1600).L, Silence(1600).R);

        Assert.Equal(-0.92, a.Analyze().PeakDbL, 1); // ...and the transient is still reported
    }

    [Fact]
    public void Clipping_is_counted_on_the_capture_side_so_no_event_can_fall_between_frames()
    {
        var a = new AudioAnalyzer { SampleRate = 48000 };
        var full = new float[256];
        Array.Fill(full, 1.0f);
        var quiet = new float[256];

        // Three separate bursts, none of which is ever the state at an Analyze() call.
        for (int i = 0; i < 3; i++)
        {
            a.Feed(full, full);
            a.Feed(quiet, quiet);
        }

        Assert.Equal(3, a.Analyze().ClipEvents);
    }

    [Fact]
    public void The_clip_count_only_ever_goes_up_so_a_latch_can_be_taken_from_a_baseline()
    {
        var a = new AudioAnalyzer { SampleRate = 48000 };
        var full = new float[256];
        Array.Fill(full, 1.0f);
        var quiet = new float[256];

        a.Feed(full, full);
        a.Feed(quiet, quiet);
        int after1 = a.Analyze().ClipEvents;
        a.Feed(quiet, quiet);
        int after2 = a.Analyze().ClipEvents;

        Assert.Equal(1, after1);
        Assert.Equal(1, after2); // nothing resets it — HudClipLatch owns "reset", per surface
    }

    [Fact]
    public void A_run_of_clipping_blocks_is_one_event_not_one_per_block()
    {
        var a = new AudioAnalyzer { SampleRate = 48000 };
        var full = new float[256];
        Array.Fill(full, 1.0f);

        for (int i = 0; i < 10; i++) a.Feed(full, full);

        Assert.Equal(1, a.Analyze().ClipEvents);
    }

    [Fact]
    public void Feeding_more_than_the_window_keeps_the_newest_samples()
    {
        var a = new AudioAnalyzer { SampleRate = 48000 };
        // Fill the ring with silence, then overwrite it entirely with a tone.
        a.Feed(new float[AudioAnalyzer.FftSize], new float[AudioAnalyzer.FftSize]);
        var (l, r) = Sine(1000, 48000, AudioAnalyzer.FftSize, amplitude: 1.0);
        a.Feed(l, r);

        var snap = a.Analyze();
        int loudest = 0;
        for (int i = 1; i < snap.SpectrumDb.Length; i++)
            if (snap.SpectrumDb[i] > snap.SpectrumDb[loudest]) loudest = i;

        Assert.True(snap.SpectrumDb[loudest] > -3, "the newest window should be the tone, not the silence");
    }

    [Fact]
    public void Without_a_sample_rate_the_analysis_says_it_has_no_spectrum_to_scale()
    {
        var a = new AudioAnalyzer(); // capture not running: rate 0
        a.Feed(Sine(1000, 48000, 2048).L, Sine(1000, 48000, 2048).R);

        Assert.Equal(0, a.Analyze().SampleRate);
    }

    [Fact]
    public void Reset_clears_the_window_so_a_device_switch_does_not_show_the_old_devices_tail()
    {
        var a = new AudioAnalyzer { SampleRate = 48000 };
        var (l, r) = Sine(1000, 48000, AudioAnalyzer.FftSize, amplitude: 1.0);
        a.Feed(l, r);
        Assert.True(a.Analyze().HasSignal);

        a.Reset();

        var snap = a.Analyze();
        Assert.False(snap.HasSignal);
        Assert.Equal(MeterMath.FloorDb, snap.PeakDbL);
        Assert.All(snap.SpectrumDb, v => Assert.Equal(Fft.FloorDb, v));
    }

    [Fact]
    public void Feeding_from_another_thread_while_analyzing_neither_throws_nor_tears_a_snapshot()
    {
        // The real shape: the capture thread feeds while the UI thread analyses.
        var a = new AudioAnalyzer { SampleRate = 48000 };
        var (l, r) = Sine(1000, 48000, 480);
        using var stop = new ManualResetEventSlim();
        var feeder = new Thread(() => { while (!stop.IsSet) a.Feed(l, r); }) { IsBackground = true };
        feeder.Start();

        try
        {
            for (int i = 0; i < 500; i++)
            {
                var snap = a.Analyze();
                Assert.Equal(AudioAnalyzer.FftSize / 2, snap.SpectrumDb.Length);
                Assert.True(snap.PeakDbL >= MeterMath.FloorDb);
            }
        }
        finally
        {
            stop.Set();
            feeder.Join(TimeSpan.FromSeconds(5));
        }
    }

    // ---- the per-surface clip latch ----

    [Fact]
    public void A_fresh_latch_is_unlit_even_when_clipping_happened_before_it_existed()
    {
        var a = new AudioAnalyzer { SampleRate = 48000 };
        var full = new float[256];
        Array.Fill(full, 1.0f);
        a.Feed(full, full);
        a.Feed(new float[256], new float[256]);
        var snap = a.Analyze();

        var latch = new HudClipLatch();
        latch.Rebase(snap.ClipEvents);

        Assert.False(latch.Latched);
        Assert.Equal(0, latch.Count);
    }

    [Fact]
    public void The_latch_lights_and_counts_from_its_own_baseline_and_reset_moves_the_baseline()
    {
        var latch = new HudClipLatch();

        latch.Observe(0);
        Assert.False(latch.Latched);

        latch.Observe(3);
        Assert.True(latch.Latched);
        Assert.Equal(3, latch.Count);

        latch.Observe(5);
        Assert.Equal(5, latch.Count);

        latch.Reset(5);
        Assert.False(latch.Latched);
        Assert.Equal(0, latch.Count);

        latch.Observe(5);
        Assert.False(latch.Latched); // still nothing NEW since the reset
        latch.Observe(6);
        Assert.True(latch.Latched);
        Assert.Equal(1, latch.Count);
    }

    [Fact]
    public void Two_surfaces_latch_independently_so_one_reset_does_not_clear_the_other()
    {
        // The EQ editor and the levels widget both watch the same signal. Resetting one must not
        // silently answer the other's question.
        var editor = new HudClipLatch();
        var widget = new HudClipLatch();

        editor.Observe(2);
        widget.Observe(2);
        editor.Reset(2);

        Assert.False(editor.Latched);
        Assert.True(widget.Latched);
        Assert.Equal(2, widget.Count);
    }
}
