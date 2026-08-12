using ApoVolume.Core;
using Xunit;
using Xunit.Abstractions;

namespace ApoVolume.Tests;

public class VolumeStateTests
{
    private readonly ITestOutputHelper _out;
    public VolumeStateTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(1, -50.0)]      // spec: 1% = -50 dB
    [InlineData(100, 0.0)]      // spec: 100% = 0 dB
    [InlineData(50, -25.2525)]  // -50 * 50 / 99
    public void CurrentDb_maps_percent_linearly_in_dB(int percent, double expectedDb)
    {
        var s = new VolumeState(percent);
        _out.WriteLine($"percent={percent} -> {s.CurrentDb} dB (expected {expectedDb})");
        Assert.Equal(expectedDb, s.CurrentDb, 4);
    }

    [Fact]
    public void Zero_percent_and_mute_both_yield_minus_120_dB()
    {
        Assert.Equal(-120.0, new VolumeState(0).CurrentDb);
        Assert.Equal(-120.0, new VolumeState(80, muted: true).CurrentDb);
    }

    [Fact]
    public void Static_ToDb_matches_instance_CurrentDb()
    {
        foreach (var percent in new[] { 0, 1, 40, 50, 100 })
        {
            Assert.Equal(new VolumeState(percent).CurrentDb, VolumeState.ToDb(percent, muted: false));
            Assert.Equal(-120.0, VolumeState.ToDb(percent, muted: true));
        }
        _out.WriteLine($"ToDb(40,false)={VolumeState.ToDb(40, false)}");
    }

    [Fact]
    public void Up_and_Down_step_by_2_and_clamp()
    {
        var s = new VolumeState(99);
        s.Up();
        Assert.Equal(100, s.Percent);
        s.Up();
        Assert.Equal(100, s.Percent);

        var t = new VolumeState(1);
        t.Down();
        Assert.Equal(0, t.Percent);
        t.Down();
        Assert.Equal(0, t.Percent);

        var u = new VolumeState(50);
        u.Up();
        Assert.Equal(52, u.Percent);
        u.Down();
        u.Down();
        Assert.Equal(48, u.Percent);
    }

    [Fact]
    public void Up_unmutes_Down_keeps_mute_like_Windows()
    {
        var s = new VolumeState(50, muted: true);
        s.Down();
        Assert.True(s.Muted);
        s.Up();
        Assert.False(s.Muted);
        Assert.Equal(50, s.Percent); // the Up that unmuted still stepped: 48+2? No — Down took it to 48, Up back to 50.
    }

    [Fact]
    public void ToggleMute_flips_and_preserves_percent()
    {
        var s = new VolumeState(62);
        s.ToggleMute();
        Assert.True(s.Muted);
        Assert.Equal(62, s.Percent);
        s.ToggleMute();
        Assert.False(s.Muted);
    }

    [Fact]
    public void SetPercent_clamps_and_unmutes_when_positive()
    {
        var s = new VolumeState(50, muted: true);
        s.SetPercent(150);
        Assert.Equal(100, s.Percent);
        Assert.False(s.Muted);
        s.SetPercent(-5);
        Assert.Equal(0, s.Percent);
    }

    [Fact]
    public void Constructor_clamps_percent()
    {
        Assert.Equal(100, new VolumeState(300).Percent);
        Assert.Equal(0, new VolumeState(-1).Percent);
    }

    [Fact]
    public void Constructor_gains_stepPercent_with_default()
    {
        var s = new VolumeState(50);
        Assert.Equal(2, s.StepPercent);
    }

    [Fact]
    public void Constructor_stepPercent_clamps_invalid_to_default()
    {
        var s = new VolumeState(50, false, stepPercent: 3);
        Assert.Equal(2, s.StepPercent);
    }

    [Fact]
    public void Up_uses_stepPercent()
    {
        var s = new VolumeState(98, stepPercent: 5);
        s.Up();
        Assert.Equal(100, s.Percent);
        s.Up();
        Assert.Equal(100, s.Percent);

        var t = new VolumeState(50, stepPercent: 1);
        t.Up();
        Assert.Equal(51, t.Percent);
    }

    [Fact]
    public void Down_uses_stepPercent()
    {
        var s = new VolumeState(2, stepPercent: 1);
        s.Down();
        Assert.Equal(1, s.Percent);
        s.Down();
        Assert.Equal(0, s.Percent);
    }

    [Fact]
    public void StepPercent_property_clamps_setter()
    {
        var s = new VolumeState();
        s.StepPercent = 5;
        Assert.Equal(5, s.StepPercent);
        s.StepPercent = 3;
        Assert.Equal(2, s.StepPercent);
        s.StepPercent = 1;
        Assert.Equal(1, s.StepPercent);
    }

    [Fact]
    public void SetMuted_adopts_external_state_without_touching_percent()
    {
        var s = new VolumeState(60);
        s.SetMuted(true);
        _out.WriteLine($"after SetMuted(true): percent={s.Percent} muted={s.Muted}");
        Assert.True(s.Muted);
        Assert.Equal(60, s.Percent);

        s.SetMuted(false);
        _out.WriteLine($"after SetMuted(false): percent={s.Percent} muted={s.Muted}");
        Assert.False(s.Muted);
        Assert.Equal(60, s.Percent);
    }

    [Fact]
    public void StepPercent_live_change_affects_up()
    {
        var s = new VolumeState(50, stepPercent: 1);
        s.Up();
        Assert.Equal(51, s.Percent);
        s.StepPercent = 5;
        s.Up();
        Assert.Equal(56, s.Percent);
    }
}
