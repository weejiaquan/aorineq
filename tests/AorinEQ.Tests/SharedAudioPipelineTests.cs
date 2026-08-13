using AorinEQ.Core;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>THE reuse constraint of v3.5.0: exactly ONE loopback capture and ONE analysis feed
/// every consumer. Each visible audio-reading widget — and the EQ editor — registers a consumer;
/// the capture starts on the FIRST and stops on the LAST, and closing a widget mid-frame must not
/// leak a registration that keeps the capture alive forever.
///
/// The bookkeeping tests need no hardware and run everywhere. The ones that assert the capture
/// really attached carry the AudioEndpoint trait, like every other real-COM test here.</summary>
[Collection(RealAudioDeviceCollection.Name)]
public class SharedAudioPipelineTests
{
    private readonly ITestOutputHelper _out;
    public SharedAudioPipelineTests(ITestOutputHelper output) => _out = output;

    // ---- reference counting (no hardware needed: the count is the app's own bookkeeping) ----

    [Fact]
    public void A_pipeline_with_no_consumers_holds_nothing()
    {
        using var p = new SharedAudioPipeline();

        Assert.Equal(0, p.ConsumerCount);
        Assert.False(p.IsCapturing);
    }

    [Fact]
    public void Consumers_are_counted_up_and_down()
    {
        using var p = new SharedAudioPipeline();

        var a = p.AddConsumer("spectrum");
        Assert.Equal(1, p.ConsumerCount);
        var b = p.AddConsumer("levels");
        var c = p.AddConsumer("eq editor");
        Assert.Equal(3, p.ConsumerCount);

        b.Dispose();
        Assert.Equal(2, p.ConsumerCount);
        a.Dispose();
        c.Dispose();
        Assert.Equal(0, p.ConsumerCount);
    }

    [Fact]
    public void Releasing_the_same_consumer_twice_only_counts_once()
    {
        using var p = new SharedAudioPipeline();
        var a = p.AddConsumer("a");
        var b = p.AddConsumer("b");

        a.Dispose();
        a.Dispose();
        a.Dispose();

        Assert.Equal(1, p.ConsumerCount);
        b.Dispose();
        Assert.Equal(0, p.ConsumerCount);
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void A_double_release_does_not_stop_the_capture_out_from_under_everybody_else()
    {
        // THE mid-frame close. A widget torn down while its own teardown path also runs can reach
        // its release twice; if the second one counted, the capture would stop while other widgets
        // were still drawing from it — and they would silently show silence.
        //
        // This is the assertion with teeth, and the count above is not: releasing a registration
        // that is already gone is a no-op on the BOOKKEEPING under any implementation. What a
        // plain reference count breaks is the CAPTURE, so that is what is checked here — proven
        // by a control run in which both of the pipeline's guards are removed.
        using var p = new SharedAudioPipeline();
        var a = p.AddConsumer("a");
        using var b = p.AddConsumer("b");
        Assert.True(p.IsCapturing);

        a.Dispose();
        a.Dispose();
        a.Dispose();

        Assert.True(p.IsCapturing, "b is still watching — the capture must still be running");
        Assert.Equal(1, p.ConsumerCount);
        _out.WriteLine($"after three releases of one handle: capturing={p.IsCapturing} rate={p.SampleRate}");
        Assert.InRange(p.SampleRate, 8000, 384000);
    }

    [Fact]
    public void Consumers_are_named_so_a_leak_can_be_pointed_at_its_owner()
    {
        using var p = new SharedAudioPipeline();
        using var a = p.AddConsumer("spectrum widget");
        using var b = p.AddConsumer("EQ editor");

        Assert.Equal(new[] { "EQ editor", "spectrum widget" }, p.ConsumerNames.Order());
    }

    [Fact]
    public void A_disposed_pipeline_hands_back_an_inert_registration_rather_than_throwing()
    {
        // Shutdown ordering: a widget can close after the pipeline has gone. Throwing there would
        // take the process down during teardown, which is the failure this app has hit before.
        var p = new SharedAudioPipeline();
        p.Dispose();

        var late = p.AddConsumer("late");
        Assert.Equal(0, p.ConsumerCount);
        Assert.False(p.IsCapturing);
        late.Dispose();
        p.Dispose(); // idempotent
    }

    [Fact]
    public void Disposing_the_pipeline_with_consumers_still_registered_stops_the_capture_anyway()
    {
        var p = new SharedAudioPipeline();
        var a = p.AddConsumer("a");

        p.Dispose();

        Assert.False(p.IsCapturing);
        a.Dispose(); // the orphaned handle must still be safe to release
    }

    [Fact]
    public void Adding_consumers_from_many_threads_at_once_lands_on_the_right_count()
    {
        using var p = new SharedAudioPipeline();
        var handles = new IDisposable[64];

        Parallel.For(0, 64, i => handles[i] = p.AddConsumer("c" + i));
        Assert.Equal(64, p.ConsumerCount);

        Parallel.For(0, 64, i => handles[i].Dispose());
        Assert.Equal(0, p.ConsumerCount);
    }

    [Fact]
    public void The_analysis_is_the_same_object_for_every_consumer_in_one_frame()
    {
        using var p = new SharedAudioPipeline();
        using var a = p.AddConsumer("a");
        using var b = p.AddConsumer("b");

        Assert.Same(p.Analyze(), p.Analyze());
    }

    // ---- the real capture (needs a render endpoint) ----

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void The_capture_starts_on_the_first_consumer_and_stops_on_the_last()
    {
        using var p = new SharedAudioPipeline();
        Assert.False(p.IsCapturing);

        var a = p.AddConsumer("a");
        Assert.True(p.IsCapturing, "the first consumer must start the capture");
        _out.WriteLine($"sample rate {p.SampleRate}");
        Assert.InRange(p.SampleRate, 8000, 384000);

        var b = p.AddConsumer("b");
        var c = p.AddConsumer("c");
        Assert.True(p.IsCapturing);

        a.Dispose();
        b.Dispose();
        Assert.True(p.IsCapturing, "the capture must survive every release but the last");

        c.Dispose();
        Assert.False(p.IsCapturing, "the last release must stop the capture");
        Assert.Equal(0, p.SampleRate);
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void The_capture_restarts_cleanly_after_everyone_has_left_and_come_back()
    {
        // Rapid show/hide of the HUD: this cycle runs every time the last audio widget is hidden
        // and shown again.
        using var p = new SharedAudioPipeline();
        for (int i = 0; i < 8; i++)
        {
            var h = p.AddConsumer("cycle" + i);
            Assert.True(p.IsCapturing, $"cycle {i} failed to start");
            h.Dispose();
            Assert.False(p.IsCapturing, $"cycle {i} failed to stop");
        }
        _out.WriteLine("8 start/stop cycles clean");
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void Rapid_overlapping_add_and_release_from_many_threads_never_leaves_it_running_with_nobody_watching()
    {
        // The race the spec calls out: capture lifecycle under rapid show/hide. Whatever the
        // interleaving, the invariant is the same — capturing if and only if somebody is watching.
        using var p = new SharedAudioPipeline();

        Parallel.For(0, 200, _ =>
        {
            var h = p.AddConsumer("churn");
            Thread.SpinWait(50);
            h.Dispose();
        });

        Assert.Equal(0, p.ConsumerCount);
        Assert.False(p.IsCapturing);

        // And it still works afterwards.
        using var after = p.AddConsumer("after");
        Assert.True(p.IsCapturing);
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void Restart_reattaches_without_disturbing_the_consumer_count()
    {
        using var p = new SharedAudioPipeline();
        using var a = p.AddConsumer("a");
        Assert.True(p.IsCapturing);

        p.Restart(); // the default-device-change path

        Assert.True(p.IsCapturing);
        Assert.Equal(1, p.ConsumerCount);
        Assert.InRange(p.SampleRate, 8000, 384000);
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void Restart_with_nobody_watching_does_not_start_a_capture_nobody_asked_for()
    {
        using var p = new SharedAudioPipeline();

        p.Restart();

        Assert.False(p.IsCapturing);
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void The_analyzer_learns_the_captures_sample_rate_when_it_starts()
    {
        using var p = new SharedAudioPipeline();
        using var a = p.AddConsumer("a");

        Assert.Equal(p.SampleRate, p.Analyze().SampleRate);
    }
}
