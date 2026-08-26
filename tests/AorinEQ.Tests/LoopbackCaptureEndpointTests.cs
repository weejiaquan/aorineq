using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>Endpoint selection on the shared loopback capture.
///
/// Real COM against the real audio stack, per the repo's no-mocking rule. The machine running
/// these needs a render endpoint; the default-endpoint cases skip themselves when there is
/// none rather than failing a build on a headless runner.</summary>
public class LoopbackCaptureEndpointTests
{
    private readonly ITestOutputHelper _out;
    public LoopbackCaptureEndpointTests(ITestOutputHelper output) => _out = output;

    private static bool HasRenderEndpoint => AudioEndpoint.GetDefaultRenderEndpointId() is not null;

    [Fact]
    public void Default_behaviour_is_unchanged_when_no_endpoint_is_named()
    {
        if (!HasRenderEndpoint) { _out.WriteLine("no render endpoint; skipping"); return; }

        using var capture = new LoopbackCapture();
        bool started = capture.Start();
        _out.WriteLine($"Start() -> {started}, rate={capture.SampleRate}, endpointId={capture.EndpointId ?? "(default)"}");

        Assert.True(started);
        Assert.True(capture.SampleRate > 0, "a running capture must publish its rate");
        Assert.Null(capture.EndpointId);
        capture.Stop();
        Assert.Equal(0, capture.SampleRate);
    }

    [Fact]
    public void Attaches_to_a_named_endpoint()
    {
        if (!HasRenderEndpoint) { _out.WriteLine("no render endpoint; skipping"); return; }
        string id = AudioEndpoint.GetDefaultRenderEndpointId()!;

        using var capture = new LoopbackCapture();
        bool started = capture.Start(id);
        _out.WriteLine($"Start(\"{id}\") -> {started}, rate={capture.SampleRate}");

        Assert.True(started);
        Assert.Equal(id, capture.EndpointId);
        capture.Stop();
    }

    [Fact]
    public void An_unknown_endpoint_fails_cleanly_and_leaves_the_object_reusable()
    {
        using var capture = new LoopbackCapture();
        bool started = capture.Start("{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}");
        _out.WriteLine($"bogus endpoint -> {started}, rate={capture.SampleRate}");

        Assert.False(started);
        Assert.Equal(0, capture.SampleRate);

        // The important half: a failed attach must not poison the instance.
        if (!HasRenderEndpoint) { _out.WriteLine("no render endpoint; skipping reuse check"); return; }
        capture.Stop();
        Assert.True(capture.Start(), "a failed Start left the capture unusable");
        capture.Stop();
    }

    [Fact]
    public void Requesting_44100_reports_whatever_rate_was_actually_obtained()
    {
        if (!HasRenderEndpoint) { _out.WriteLine("no render endpoint; skipping"); return; }

        using var capture = new LoopbackCapture();
        bool started = capture.Start(endpointId: null, requestedSampleRate: 44100);
        _out.WriteLine($"requested 44100 -> started={started}, actual rate={capture.SampleRate}");
        _out.WriteLine(capture.SampleRate == 44100
            ? "engine honoured AUTOCONVERTPCM: no resampler needed"
            : "engine declined; caller must convert from this rate");

        Assert.True(started, "requesting a rate must never make the capture fail outright");
        Assert.True(capture.SampleRate > 0);
        capture.Stop();
    }

    [Fact]
    public void Restart_reattaches_to_the_same_endpoint_it_was_given()
    {
        if (!HasRenderEndpoint) { _out.WriteLine("no render endpoint; skipping"); return; }
        string id = AudioEndpoint.GetDefaultRenderEndpointId()!;

        using var capture = new LoopbackCapture();
        Assert.True(capture.Start(id));
        Assert.True(capture.Restart());
        _out.WriteLine($"after Restart: endpointId={capture.EndpointId}, rate={capture.SampleRate}");

        // A pinned stream must not migrate to a new default device the way visualizers do.
        Assert.Equal(id, capture.EndpointId);
        capture.Stop();
    }
}
