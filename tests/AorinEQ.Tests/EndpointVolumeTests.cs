using System.Collections.Concurrent;
using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>Real COM tests against this machine's default render endpoint (repo convention: no
/// mocks) — they audibly move the actual Windows volume while running. Every test that changes
/// state snapshots the endpoint's initial volume/mute first and RESTORES it in finally.</summary>
public class EndpointVolumeTests
{
    private readonly ITestOutputHelper _out;
    public EndpointVolumeTests(ITestOutputHelper output) => _out = output;

    private static void Restore(EndpointVolume ev, (int Percent, bool Muted) initial)
    {
        ev.SetPercent(initial.Percent);
        ev.SetMuted(initial.Muted);
    }

    [Fact]
    public void TryRead_reports_the_real_endpoint_state()
    {
        using var ev = new EndpointVolume();
        var state = ev.TryRead();
        _out.WriteLine($"endpoint state: {state}");
        Assert.NotNull(state);
        Assert.InRange(state!.Value.Percent, 0, 100);
    }

    [Fact]
    public void SetPercent_roundtrips_through_the_device()
    {
        using var ev = new EndpointVolume();
        var initial = ev.TryRead();
        Assert.NotNull(initial);
        _out.WriteLine($"initial: {initial}");
        try
        {
            foreach (var target in new[] { 37, 64, 0, 100 })
            {
                Assert.True(ev.SetPercent(target), $"SetPercent({target}) returned false");
                var read = ev.TryRead();
                _out.WriteLine($"set {target} -> read {read}");
                Assert.NotNull(read);
                Assert.Equal(target, read!.Value.Percent);
            }
        }
        finally
        {
            Restore(ev, initial!.Value);
            _out.WriteLine($"restored: {ev.TryRead()}");
        }
    }

    [Fact]
    public void SetMuted_roundtrips_through_the_device()
    {
        using var ev = new EndpointVolume();
        var initial = ev.TryRead();
        Assert.NotNull(initial);
        _out.WriteLine($"initial: {initial}");
        try
        {
            Assert.True(ev.SetMuted(true));
            var muted = ev.TryRead();
            _out.WriteLine($"after mute: {muted}");
            Assert.True(muted!.Value.Muted);

            Assert.True(ev.SetMuted(false));
            var unmuted = ev.TryRead();
            _out.WriteLine($"after unmute: {unmuted}");
            Assert.False(unmuted!.Value.Muted);
        }
        finally
        {
            Restore(ev, initial!.Value);
            _out.WriteLine($"restored: {ev.TryRead()}");
        }
    }

    [Fact]
    public void External_change_raises_Changed_but_own_sets_do_not_echo()
    {
        using var listener = new EndpointVolume();
        using var external = new EndpointVolume(); // different event-context GUID = "someone else"
        var initial = listener.TryRead();
        Assert.NotNull(initial);
        _out.WriteLine($"initial: {initial}");
        try
        {
            var events = new ConcurrentQueue<(int Percent, bool Muted)>();
            var deviceIds = new ConcurrentQueue<string?>();
            listener.Changed += (id, p, m) => { events.Enqueue((p, m)); deviceIds.Enqueue(id); };

            // Own set: the notification carries our own event context and must be swallowed.
            Assert.True(listener.SetPercent(41));
            Thread.Sleep(750); // notifications are async; give a wrong impl time to echo
            _out.WriteLine($"events after own set: [{string.Join(", ", events)}]");
            Assert.Empty(events);

            // External set (another context): Changed must fire with the new state.
            Assert.True(external.SetPercent(73));
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && !events.Any(e => e.Percent == 73))
                Thread.Sleep(50);
            _out.WriteLine($"events after external set: [{string.Join(", ", events)}]");
            Assert.Contains(events, e => e.Percent == 73);

            // Every notification is stamped with the endpoint it came from — that stamp is
            // what lets the app drop notifications from a no-longer-active device.
            var expectedId = AudioEndpoint.GetDefaultRenderEndpointId();
            _out.WriteLine($"device ids on events: [{string.Join(", ", deviceIds)}] (default {expectedId})");
            Assert.NotEmpty(deviceIds);
            Assert.All(deviceIds, id => Assert.Equal(expectedId, id));
        }
        finally
        {
            Restore(listener, initial!.Value);
            _out.WriteLine($"restored: {listener.TryRead()}");
        }
    }

    [Fact]
    public void Dispose_is_idempotent_and_methods_fail_gracefully_after()
    {
        var ev = new EndpointVolume();
        var before = ev.TryRead();
        _out.WriteLine($"before dispose: {before}");
        Assert.NotNull(before);

        ev.Dispose();
        var setResult = ev.SetPercent(50);
        var muteResult = ev.SetMuted(true);
        var readResult = ev.TryRead();
        _out.WriteLine($"after dispose: SetPercent={setResult} SetMuted={muteResult} TryRead={readResult?.ToString() ?? "<null>"}");
        Assert.False(setResult);
        Assert.False(muteResult);
        Assert.Null(readResult);

        ev.Dispose(); // double-dispose must not throw
    }
}
