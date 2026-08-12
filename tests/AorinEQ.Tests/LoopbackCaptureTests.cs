using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

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
    public void DecodeInterleaved_handles_float32_and_16_24_32_bit_pcm()
    {
        var left = new float[2];
        var right = new float[2];

        // float32 stereo: frame0 = (0.5, -0.25), frame1 = (1.0, 0.0)
        var f32 = new byte[16];
        BitConverter.GetBytes(0.5f).CopyTo(f32, 0);
        BitConverter.GetBytes(-0.25f).CopyTo(f32, 4);
        BitConverter.GetBytes(1.0f).CopyTo(f32, 8);
        BitConverter.GetBytes(0.0f).CopyTo(f32, 12);
        LoopbackCapture.DecodeInterleaved(f32, 2, 2, 32, isFloat: true, left, right);
        Assert.Equal(new[] { 0.5f, 1.0f }, left);
        Assert.Equal(new[] { -0.25f, 0.0f }, right);

        // 16-bit stereo: (16384, -32768), (32767, 0)
        var p16 = new byte[8];
        BitConverter.GetBytes((short)16384).CopyTo(p16, 0);
        BitConverter.GetBytes((short)-32768).CopyTo(p16, 2);
        BitConverter.GetBytes((short)32767).CopyTo(p16, 4);
        BitConverter.GetBytes((short)0).CopyTo(p16, 6);
        LoopbackCapture.DecodeInterleaved(p16, 2, 2, 16, isFloat: false, left, right);
        Assert.Equal(0.5f, left[0], 4);
        Assert.Equal(0.9999f, left[1], 3);
        Assert.Equal(-1.0f, right[0], 4);
        Assert.Equal(0.0f, right[1], 4);

        // 24-bit packed mono: +4194304 (0.5), -8388608 (-1.0) — duplicated to both channels.
        var p24 = new byte[6];
        p24[0] = 0x00; p24[1] = 0x00; p24[2] = 0x40; // 0x400000 = 4194304
        p24[3] = 0x00; p24[4] = 0x00; p24[5] = 0x80; // 0x800000 sign-extends to -8388608
        LoopbackCapture.DecodeInterleaved(p24, 2, 1, 24, isFloat: false, left, right);
        _out.WriteLine($"24-bit: {left[0]}, {left[1]}");
        Assert.Equal(0.5f, left[0], 4);
        Assert.Equal(-1.0f, left[1], 4);
        Assert.Equal(left, right); // mono duplicated

        // 32-bit int mono: int.MinValue -> -1.0, 1073741824 -> 0.5
        var p32 = new byte[8];
        BitConverter.GetBytes(int.MinValue).CopyTo(p32, 0);
        BitConverter.GetBytes(1073741824).CopyTo(p32, 4);
        LoopbackCapture.DecodeInterleaved(p32, 2, 1, 32, isFloat: false, left, right);
        Assert.Equal(-1.0f, left[0], 4);
        Assert.Equal(0.5f, left[1], 4);
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
