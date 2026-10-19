using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>Where the volume keys go while a stream is running.
///
/// This is the rule the whole feature turns on, and it is counter-intuitive, so it lives in a
/// pure static function rather than being buried in event wiring.
///
/// The AirPlay stream is fed from a loopback tap that sits AFTER Equalizer APO. In APO mode WITH
/// Equalizer APO on the source, the preamp has already attenuated the samples before they are
/// packetised — retargeting would attenuate twice. In APO mode WITHOUT it on the source
/// (VB-Cable with no EQ, say) the preamp touches nothing, so the keys must drive the receiver. In
/// system mode the endpoint volume may or may not reach the tap, so the sender drives the
/// receiver explicitly.</summary>
public class AirPlayControllerTests
{
    private readonly ITestOutputHelper _out;
    public AirPlayControllerTests(ITestOutputHelper output) => _out = output;

    [Theory]
    // mode, autoRetarget, streaming, eapoActiveOnSource -> retarget
    [InlineData(VolumeModes.Eapo, true, true, true, false)]   // preamp already upstream of the tap
    [InlineData(VolumeModes.Eapo, true, true, false, true)]   // preamp reaches nothing: drive the receiver
    [InlineData(VolumeModes.Eapo, false, true, false, false)] // the user said no
    [InlineData(VolumeModes.Eapo, true, false, false, false)] // nothing streaming
    [InlineData(VolumeModes.Eapo, false, false, true, false)]
    [InlineData(VolumeModes.System, true, true, true, true)]
    [InlineData(VolumeModes.System, true, true, false, true)]
    [InlineData(VolumeModes.System, false, true, false, false)]
    [InlineData(VolumeModes.System, true, false, true, false)]
    [InlineData(VolumeModes.System, false, false, false, false)]
    [InlineData("something-else", true, true, false, false)]  // an unknown mode never starts driving the receiver
    [InlineData("something-else", true, true, true, false)]
    public void Volume_keys_drive_the_receiver_exactly_when_nothing_upstream_of_the_tap_would(
        string mode, bool autoRetarget, bool streaming, bool eapoOnSource, bool expected)
    {
        bool retarget = AirPlayController.ShouldRetargetVolume(mode, autoRetarget, streaming, eapoOnSource);
        _out.WriteLine($"mode={mode} autoRetarget={autoRetarget} streaming={streaming} eapoOnSource={eapoOnSource} -> retarget={retarget}");
        Assert.Equal(expected, retarget);
    }

    [Theory]
    [InlineData(VolumeModes.Eapo, true, false)]
    [InlineData(VolumeModes.Eapo, false, true)]
    [InlineData(VolumeModes.System, true, true)]
    [InlineData(VolumeModes.System, false, true)]
    [InlineData("something-else", false, false)]
    public void The_setting_is_meaningful_wherever_the_keys_could_be_retargeted(
        string mode, bool eapoOnSource, bool expected)
    {
        bool applies = AirPlayController.RetargetSettingApplies(mode, eapoOnSource);
        _out.WriteLine($"mode={mode} eapoOnSource={eapoOnSource} -> applies={applies}");
        Assert.Equal(expected, applies);
    }

    [Fact]
    public void A_fresh_controller_captures_nothing()
    {
        using var controller = new AirPlayController();
        _out.WriteLine($"source endpoint while idle: {controller.SourceEndpointId ?? "(none)"}");
        Assert.Null(controller.SourceEndpointId);
    }

    [Fact]
    public void A_fresh_controller_is_idle_and_reports_idle_diagnostics()
    {
        using var controller = new AirPlayController();
        var snapshot = controller.Snapshot();
        _out.WriteLine($"state={snapshot.State} streaming={controller.IsStreaming} summary='{snapshot.Summary}'");

        Assert.False(controller.IsStreaming);
        Assert.Null(controller.Current);
        Assert.Equal("idle", snapshot.State);
    }

    [Fact]
    public void Stopping_an_idle_controller_is_harmless()
    {
        using var controller = new AirPlayController();
        controller.Stop();
        controller.Stop();
        _out.WriteLine($"after two Stops: streaming={controller.IsStreaming}");
        Assert.False(controller.IsStreaming);
    }

    /// <summary>App asks for a re-attach after every repair-helper run while a stream is up; with
    /// nothing streaming there is no capture to re-attach, and asking must not start one (that
    /// would be a WASAPI client nobody stops). The streaming half needs a real receiver and is a
    /// live check.</summary>
    [Fact]
    public void Re_attaching_the_capture_of_an_idle_controller_does_nothing()
    {
        using var controller = new AirPlayController();
        bool reattached = controller.RestartCapture();
        _out.WriteLine($"idle RestartCapture -> {reattached}; streaming={controller.IsStreaming} source={controller.SourceEndpointId ?? "(none)"}");
        Assert.False(reattached);
        Assert.False(controller.IsStreaming);
        Assert.Null(controller.SourceEndpointId);
    }

    [Fact]
    public void Volume_changes_while_idle_are_remembered_for_the_next_session()
    {
        using var controller = new AirPlayController();
        controller.SetVolumePercent(35);
        _out.WriteLine($"volume percent held while idle: {controller.VolumePercent}");
        Assert.Equal(35, controller.VolumePercent);

        controller.SetVolumePercent(500);
        _out.WriteLine($"out-of-range clamped to: {controller.VolumePercent}");
        Assert.Equal(100, controller.VolumePercent);
    }
}
