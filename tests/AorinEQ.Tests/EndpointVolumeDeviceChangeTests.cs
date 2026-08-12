using System.Collections.Concurrent;
using System.Diagnostics;
using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>The default-device notification path, driven for real: this machine's default playback
/// device is switched back and forth and every switch must be observed.
///
/// WHY REPETITION IS THE WHOLE TEST. Until v3.4.1 the notification arrived exactly ONCE per session
/// and then stopped, because <c>EndpointVolume</c> did its MMDevAPI work (unregister the volume
/// callback, release the endpoint, re-activate on the new one) INSIDE the IMMNotificationClient
/// callback. MMDevAPI does not permit a blocking call back into it from a notification callback: the
/// call never returns, its dispatch thread is stuck there for the life of the process, and no
/// further notification is ever delivered. A test that switches the device ONCE passes on that bug.
///
/// Every test here restores the default device it found.</summary>
[Trait(Requires.Key, Requires.AudioEndpoint)]
[Trait(Requires.Key, Requires.MultipleRenderEndpoints)]
public class EndpointVolumeDeviceChangeTests
{
    /// <summary>Enough switches that "delivered once then stopped" cannot pass, with room for the
    /// pattern to be visible in the output when it does not.</summary>
    private const int Switches = 6;

    private static readonly TimeSpan PerSwitchTimeout = TimeSpan.FromSeconds(6);

    private readonly ITestOutputHelper _out;
    public EndpointVolumeDeviceChangeTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Every_default_device_change_is_delivered_not_just_the_first()
    {
        var original = DefaultRenderDevice.Current;
        Assert.NotNull(original);
        var other = DefaultRenderDevice.Other(original);
        Assert.NotNull(other);
        _out.WriteLine($"original default: {original}");
        _out.WriteLine($"switching against: {other!.FriendlyName} ({other.Id})");

        using var ev = new EndpointVolume();
        var delivered = new SemaphoreSlim(0);
        var seen = new ConcurrentQueue<string>();
        ev.DefaultDeviceChanged += () =>
        {
            seen.Enqueue($"DefaultDeviceChanged @ {DateTime.UtcNow:HH:mm:ss.fff}");
            delivered.Release();
        };

        var observed = new List<(int Index, string Target, bool Seen)>();
        try
        {
            string current = original!;
            for (int i = 1; i <= Switches; i++)
            {
                string target = current == original ? other.Id : original;
                // Drain anything left over from the previous switch so each wait below is
                // answered by THIS switch and not by a straggler from the last one.
                while (delivered.Wait(0)) { }
                DefaultRenderDevice.SetDefault(target);
                bool got = delivered.Wait(PerSwitchTimeout);
                observed.Add((i, target == original ? "original" : "other", got));
                _out.WriteLine($"switch {i} -> {(target == original ? "original" : "other")}: "
                    + (got ? "OBSERVED" : "NOT DELIVERED"));
                current = target;
            }
        }
        finally
        {
            DefaultRenderDevice.SetDefault(original!);
            _out.WriteLine($"restored default: {DefaultRenderDevice.Current}");
        }

        _out.WriteLine($"deliveries: [{string.Join(" | ", seen)}]");
        Assert.All(observed, o =>
            Assert.True(o.Seen, $"switch {o.Index} (to {o.Target}) was never delivered — "
                + $"only {observed.Count(x => x.Seen)}/{Switches} switches were observed"));
    }

    /// <summary>Following the device is the POINT of the notification: after each switch the
    /// backend must be re-activated on the new endpoint, so what it reads and what it stamps onto
    /// its events name the device that is now default. Stuck on the old endpoint, per-device volume
    /// silently stops following the user's output.</summary>
    [Fact]
    public void The_backend_re_activates_on_the_new_endpoint_every_time()
    {
        var original = DefaultRenderDevice.Current;
        Assert.NotNull(original);
        var other = DefaultRenderDevice.Other(original);
        Assert.NotNull(other);

        using var ev = new EndpointVolume();
        var stamps = new BlockingCollection<string?>();
        ev.Changed += (id, _, _) => stamps.Add(id);

        var results = new List<(int Index, string Expected, string? Stamped, bool Readable)>();
        try
        {
            string current = original!;
            for (int i = 1; i <= Switches; i++)
            {
                string target = current == original ? other!.Id : original!;
                while (stamps.TryTake(out _)) { }
                DefaultRenderDevice.SetDefault(target);

                string? stamped = stamps.TryTake(out var s, PerSwitchTimeout) ? s : null;
                bool readable = ev.TryRead() is not null;
                results.Add((i, target, stamped, readable));
                _out.WriteLine($"switch {i}: expected {target}");
                _out.WriteLine($"          stamped  {stamped ?? "<nothing delivered>"} readable={readable}");
                current = target;
            }
        }
        finally
        {
            DefaultRenderDevice.SetDefault(original!);
        }

        Assert.All(results, r =>
        {
            Assert.True(r.Readable, $"switch {r.Index}: the endpoint was unreadable after the switch");
            Assert.Equal(r.Expected, r.Stamped);
        });
    }

    /// <summary>A BURST of switches faster than the notification queue drains must still leave the
    /// backend on the device the machine actually ended on. The handler re-reads the current
    /// default rather than activating the endpoint each notification names, precisely so that
    /// intermediate hops cannot strand it on a device the user has already left — this is the test
    /// that keeps that property honest.</summary>
    [Fact]
    public void A_burst_of_switches_converges_on_the_device_the_machine_ended_on()
    {
        var original = DefaultRenderDevice.Current;
        Assert.NotNull(original);
        var other = DefaultRenderDevice.Other(original);
        Assert.NotNull(other);

        using var ev = new EndpointVolume();
        // The stamp on Changed names the endpoint the backend re-activated on, so the LAST one
        // reports where it settled — no test-only API needed to see it.
        var stamps = new ConcurrentQueue<string?>();
        ev.Changed += (id, _, _) => stamps.Enqueue(id);
        try
        {
            // No settle between them: several switches are in flight at once.
            for (int i = 0; i < 5; i++)
            {
                DefaultRenderDevice.SetDefault(other!.Id);
                DefaultRenderDevice.SetDefault(original!);
            }
            DefaultRenderDevice.SetDefault(other!.Id); // ends here, deliberately not the original
            _out.WriteLine($"burst finished, machine default is {DefaultRenderDevice.Current}");

            string? settled = null;
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                settled = stamps.LastOrDefault();
                if (settled == other.Id) break;
                Thread.Sleep(100);
            }
            _out.WriteLine($"stamps seen: {stamps.Count}, settled on {settled}");
            Assert.Equal(other.Id, settled);
        }
        finally
        {
            DefaultRenderDevice.SetDefault(original!);
        }
    }

    /// <summary>Shutdown after a device change must not hang. On the bug this deadlocked outright:
    /// the callback thread was stuck inside MMDevAPI still holding the instance lock, so Dispose
    /// waited on it forever and the process never exited.</summary>
    [Fact]
    public void Dispose_completes_after_a_default_device_change()
    {
        var original = DefaultRenderDevice.Current;
        Assert.NotNull(original);
        var other = DefaultRenderDevice.Other(original);
        Assert.NotNull(other);

        var ev = new EndpointVolume();
        var changed = new SemaphoreSlim(0);
        ev.DefaultDeviceChanged += () => changed.Release();
        try
        {
            DefaultRenderDevice.SetDefault(other!.Id);
            Assert.True(changed.Wait(PerSwitchTimeout), "the device change was never delivered");
        }
        finally
        {
            DefaultRenderDevice.SetDefault(original!);
        }

        var sw = Stopwatch.StartNew();
        var disposed = new ManualResetEventSlim();
        var thread = new Thread(() => { ev.Dispose(); disposed.Set(); }) { IsBackground = true };
        thread.Start();
        bool finished = disposed.Wait(TimeSpan.FromSeconds(15));
        _out.WriteLine($"Dispose returned after {sw.ElapsedMilliseconds} ms: {finished}");
        Assert.True(finished, "Dispose did not return — the notification callback deadlocked it");
    }
}
