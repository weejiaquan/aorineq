using AorinEQ.Core;

namespace AorinEQ.Tests;

/// <summary>When the OSD's AirPlay bar appears, and what it says when it does.
///
/// Pure decision, kept in Core and away from the two OSD windows, because BOTH of them have to
/// reach the same answer: a skinned OSD and a Fluent one must not disagree about whether AirPlay
/// is showing.</summary>
public class AirPlayBarTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public AirPlayBarTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private static AirPlaySetting Setting(bool enabled = true, string visibility = AirPlayBarVisibility.Connected,
        string deviceName = "Bedroom", int volume = 70) =>
        AirPlaySetting.Default with
        {
            Enabled = enabled,
            DeviceName = deviceName,
            VolumePercent = volume,
            BarVisibility = visibility,
        };

    [Fact]
    public void The_visibility_vocabulary_is_exactly_the_three_designed_values()
    {
        _out.WriteLine("visibility: " + string.Join(", ", AirPlayBarVisibility.All));
        Assert.Equal(
            new[] { AirPlayBarVisibility.Never, AirPlayBarVisibility.Connected, AirPlayBarVisibility.Enabled },
            AirPlayBarVisibility.All);
    }

    /// <summary>Unknown and absent both land on the default rather than hiding the bar or
    /// throwing, the way every other persisted vocabulary in this app normalises.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("always")]
    [InlineData("CONNECTED")]
    public void An_unrecognised_visibility_normalises_to_the_default(string? value)
    {
        _out.WriteLine($"'{value}' -> {AirPlayBarVisibility.Normalize(value)}");
        Assert.Equal(AirPlayBarVisibility.Connected, AirPlayBarVisibility.Normalize(value));
    }

    [Fact]
    public void Never_hides_the_bar_even_while_streaming()
    {
        var state = AirPlayBarState.From(
            Setting(visibility: AirPlayBarVisibility.Never), connected: true, streaming: true);

        _out.WriteLine($"visible={state.Visible} connected={state.IsConnected}");
        Assert.False(state.Visible);
    }

    /// <summary>The default. A bar for a receiver nobody is using is clutter, and the OSD is liked
    /// precisely because it says one thing quickly.</summary>
    [Fact]
    public void Connected_shows_the_bar_only_once_there_is_a_session()
    {
        var idle = AirPlayBarState.From(Setting(), connected: false, streaming: false);
        var live = AirPlayBarState.From(Setting(), connected: true, streaming: false);

        _out.WriteLine($"not connected -> visible={idle.Visible}");
        _out.WriteLine($"connected     -> visible={live.Visible}");

        Assert.False(idle.Visible);
        Assert.True(live.Visible);
    }

    /// <summary>"Enabled" is for someone who wants the receiver one click away at all times - the
    /// bar is the thing they CONNECT with, so it has to be there before they are connected.</summary>
    [Fact]
    public void Enabled_shows_the_bar_before_anything_is_connected()
    {
        var state = AirPlayBarState.From(
            Setting(visibility: AirPlayBarVisibility.Enabled), connected: false, streaming: false);

        _out.WriteLine($"visible={state.Visible} name='{state.DeviceName}'");
        Assert.True(state.Visible);
    }

    /// <summary>AirPlay switched off in settings beats every visibility choice. Otherwise turning
    /// the feature off would still leave its bar on screen.</summary>
    [Theory]
    [InlineData(AirPlayBarVisibility.Never)]
    [InlineData(AirPlayBarVisibility.Connected)]
    [InlineData(AirPlayBarVisibility.Enabled)]
    public void Disabling_airplay_hides_the_bar_whatever_the_visibility_says(string visibility)
    {
        var state = AirPlayBarState.From(
            Setting(enabled: false, visibility: visibility), connected: true, streaming: true);

        _out.WriteLine($"{visibility} with AirPlay off -> visible={state.Visible}");
        Assert.False(state.Visible);
    }

    [Fact]
    public void The_bar_carries_the_device_name_and_the_receivers_own_level()
    {
        var state = AirPlayBarState.From(
            Setting(deviceName: "Living room", volume: 42), connected: true, streaming: true);

        _out.WriteLine($"name='{state.DeviceName}' volume={state.VolumePercent} streaming={state.IsStreaming}");

        Assert.Equal("Living room", state.DeviceName);
        Assert.Equal(42, state.VolumePercent);
        Assert.True(state.IsStreaming);
    }

    /// <summary>A receiver has been chosen but nothing is connected yet: the bar still has to say
    /// something, and the device name is the useful thing to say.</summary>
    [Fact]
    public void A_chosen_but_unconnected_receiver_still_names_itself()
    {
        var state = AirPlayBarState.From(
            Setting(visibility: AirPlayBarVisibility.Enabled), connected: false, streaming: false);

        _out.WriteLine($"name='{state.DeviceName}' connected={state.IsConnected}");
        Assert.Equal("Bedroom", state.DeviceName);
        Assert.False(state.IsConnected);
    }

    /// <summary>Nothing chosen at all. The bar shows, because the user asked to see it, and it is
    /// how they pick one - so it must not render a blank strip with no affordance.</summary>
    [Fact]
    public void With_no_receiver_chosen_the_bar_reports_no_name_rather_than_a_stale_one()
    {
        var state = AirPlayBarState.From(
            Setting(visibility: AirPlayBarVisibility.Enabled, deviceName: ""),
            connected: false, streaming: false);

        _out.WriteLine($"name='{state.DeviceName}' hasDevice={state.HasDevice}");
        Assert.Equal("", state.DeviceName);
        Assert.False(state.HasDevice);
        Assert.True(state.Visible);
    }

    /// <summary>The level is clamped on the way out. It is persisted, and a settings file edited
    /// by hand must not make the fill overrun or invert the bar it draws.</summary>
    [Theory]
    [InlineData(-40, 0)]
    [InlineData(0, 0)]
    [InlineData(55, 55)]
    [InlineData(100, 100)]
    [InlineData(180, 100)]
    public void The_level_is_clamped_to_a_drawable_range(int stored, int expected)
    {
        var state = AirPlayBarState.From(Setting(volume: stored), connected: true, streaming: true);

        _out.WriteLine($"stored {stored} -> {state.VolumePercent}");
        Assert.Equal(expected, state.VolumePercent);
    }
}
