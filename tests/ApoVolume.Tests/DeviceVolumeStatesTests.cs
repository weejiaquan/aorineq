using ApoVolume.Core;
using Xunit;
using Xunit.Abstractions;

namespace ApoVolume.Tests;

public class DeviceVolumeStatesTests
{
    private const string DevA = "{0.0.0.00000000}.{aaaaaaaa-1111-2222-3333-444444444444}";
    private const string DevB = "{0.0.0.00000000}.{bbbbbbbb-5555-6666-7777-888888888888}";

    private readonly ITestOutputHelper _out;
    public DeviceVolumeStatesTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void First_seen_device_seeds_from_legacy_percent_and_mute()
    {
        var settings = Settings.Default with { Percent = 40, Muted = true, StepPercent = 5 };
        var states = new DeviceVolumeStates(settings);
        var a = states.SwitchTo(DevA);
        _out.WriteLine($"seeded: {a.Percent}% muted={a.Muted} step={a.StepPercent}");
        Assert.Equal(40, a.Percent);
        Assert.True(a.Muted);
        Assert.Equal(5, a.StepPercent);
    }

    [Fact]
    public void Persisted_device_state_wins_over_the_legacy_seed()
    {
        var settings = Settings.Default with
        {
            Percent = 40,
            DeviceVolumes = new Dictionary<string, DeviceVolumeSetting>
            {
                [DevA] = new(80, false),
                [DevB] = new(15, true),
            },
        };
        var states = new DeviceVolumeStates(settings);
        Assert.Equal(80, states.SwitchTo(DevA).Percent);
        var b = states.SwitchTo(DevB);
        Assert.Equal(15, b.Percent);
        Assert.True(b.Muted);
    }

    [Fact]
    public void Switching_devices_keeps_each_state_independent()
    {
        var states = new DeviceVolumeStates(Settings.Default with { Percent = 50 });
        var a = states.SwitchTo(DevA);
        a.SetPercent(70);
        // A first-seen device inherits the volume currently in USE (codex r1: a startup-time
        // seed goes stale the moment the user changes volume mid-session).
        var b = states.SwitchTo(DevB);
        Assert.Equal(70, b.Percent);
        b.SetPercent(20);
        var aAgain = states.SwitchTo(DevA);
        _out.WriteLine($"A={aAgain.Percent} B={b.Percent}");
        Assert.Equal(70, aAgain.Percent); // A's edit survived the away-trip, B's 20 didn't leak
        Assert.Same(a, aAgain);
        Assert.Equal(20, b.Percent);
    }

    [Fact]
    public void Live_seed_survives_a_trip_through_no_default_device()
    {
        // codex r2: SwitchTo(null) used to resurrect the startup seed, which then leaked into
        // the next first-seen device (e.g. the last endpoint unplugged, then a new one added).
        var states = new DeviceVolumeStates(Settings.Default with { Percent = 50 });
        var a = states.SwitchTo(DevA);
        a.SetPercent(85);
        a.ToggleMute();

        var fallback = states.SwitchTo(null);
        _out.WriteLine($"fallback after null switch: {fallback.Percent}% muted={fallback.Muted}");
        Assert.Equal(85, fallback.Percent);
        Assert.True(fallback.Muted); // mute state adopted too, not just the percent

        var b = states.SwitchTo(DevB);
        _out.WriteLine($"new device seeded: {b.Percent}% muted={b.Muted}");
        Assert.Equal(85, b.Percent);
        Assert.True(b.Muted);
    }

    [Fact]
    public void Active_tracks_the_last_switch_and_null_falls_back()
    {
        var states = new DeviceVolumeStates(Settings.Default with { Percent = 33 });
        Assert.Equal(33, states.Active.Percent); // no device yet: fallback state from seed
        var a = states.SwitchTo(DevA);
        Assert.Same(a, states.Active);
        Assert.Equal(DevA, states.ActiveId);
        var fallback = states.SwitchTo(null);
        Assert.Same(fallback, states.Active);
        Assert.Null(states.ActiveId);
        _out.WriteLine($"fallback percent={fallback.Percent}");
    }

    [Fact]
    public void Snapshot_reports_seen_devices_and_keeps_unseen_persisted_ones()
    {
        const string DevC = "{0.0.0.00000000}.{cccccccc-9999-9999-9999-999999999999}";
        var states = new DeviceVolumeStates(Settings.Default with
        {
            Percent = 50,
            DeviceVolumes = new Dictionary<string, DeviceVolumeSetting> { [DevC] = new(25, false) },
        });
        states.SwitchTo(DevA).SetPercent(70);
        states.SwitchTo(DevB).ToggleMute();
        var snap = states.Snapshot();
        foreach (var (k, v) in snap) _out.WriteLine($"{k} -> {v.Percent}% muted={v.Muted}");
        Assert.Equal(70, snap[DevA].Percent);
        Assert.True(snap[DevB].Muted);
        Assert.Equal(25, snap[DevC].Percent); // never switched to, must not be dropped
        Assert.Equal(3, snap.Count);
    }

    [Fact]
    public void StepPercent_applies_to_every_state_current_and_future()
    {
        var states = new DeviceVolumeStates(Settings.Default with { StepPercent = 1 });
        var a = states.SwitchTo(DevA);
        Assert.Equal(1, a.StepPercent);
        states.StepPercent = 5;
        Assert.Equal(5, a.StepPercent);
        Assert.Equal(5, states.SwitchTo(DevB).StepPercent);
    }
}
