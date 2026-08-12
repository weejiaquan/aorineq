using ApoVolume.Core;
using Xunit;
using Xunit.Abstractions;

namespace ApoVolume.Tests;

/// <summary>Real-WASAPI smoke tests on this machine's default render endpoint (no mocks).
/// Data flow isn't asserted — event-driven loopback only signals while audio renders — but
/// init/teardown must work cleanly and repeatedly, because the EQ window opens and closes
/// the capture on every show/hide.</summary>
public class LoopbackCaptureTests
{
    private readonly ITestOutputHelper _out;
    public LoopbackCaptureTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Start_attaches_to_the_default_endpoint_and_stop_tears_down()
    {
        using var capture = new LoopbackCapture();
        bool started = capture.Start();
        _out.WriteLine($"started={started} sampleRate={capture.SampleRate}");
        Assert.True(started, "loopback init failed on the default render endpoint");
        Assert.InRange(capture.SampleRate, 8000, 384000);
        capture.Stop();
        Assert.Equal(0, capture.SampleRate); // torn down: no idle CPU, no lingering state
    }

    [Fact]
    public void Restart_cycles_cleanly_for_device_reattach()
    {
        using var capture = new LoopbackCapture();
        Assert.True(capture.Start());
        int rate1 = capture.SampleRate;
        Assert.True(capture.Restart()); // the device-switch path: full stop + fresh attach
        _out.WriteLine($"rate before={rate1} after={capture.SampleRate}");
        Assert.InRange(capture.SampleRate, 8000, 384000);
        capture.Stop();
    }

    [Fact]
    public void Stop_and_dispose_are_idempotent_and_start_after_stop_works()
    {
        var capture = new LoopbackCapture();
        capture.Stop(); // never started: must be a no-op
        Assert.True(capture.Start());
        capture.Stop();
        capture.Stop();
        Assert.True(capture.Start()); // restartable after a full stop
        capture.Dispose();
        capture.Dispose(); // double-dispose safe
        Assert.False(capture.Start()); // disposed: refuses to start
        _out.WriteLine("teardown clean");
    }
}
