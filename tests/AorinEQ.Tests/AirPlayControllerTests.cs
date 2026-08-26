using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>Where the volume keys go while a stream is running.
///
/// This is the rule the whole feature turns on, and it is counter-intuitive, so it lives in a
/// pure static function rather than being buried in event wiring.
///
/// The AirPlay stream is fed from a loopback tap that sits AFTER Equalizer APO. So in APO mode
/// the preamp has already attenuated the samples before they are packetised — the volume keys
/// control the receiver for free, and retargeting them would attenuate twice. In system mode
/// the endpoint volume may or may not reach the tap depending on whether the device has
/// hardware volume, which Microsoft documents as not contractual, so relying on it is unsafe
/// in both directions and the sender drives the receiver explicitly instead.</summary>
public class AirPlayControllerTests
{
    private readonly ITestOutputHelper _out;
    public AirPlayControllerTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Apo_mode_never_retargets_even_when_the_setting_is_on()
    {
        bool retarget = AirPlayController.ShouldRetargetVolume(
            VolumeModes.Eapo, autoRetarget: true, streaming: true);
        _out.WriteLine($"eapo + autoRetarget + streaming -> retarget={retarget}");
        _out.WriteLine("(the preamp is already upstream of the tap; retargeting would double-attenuate)");

        Assert.False(retarget);
    }

    [Fact]
    public void System_mode_retargets_while_streaming()
    {
        bool retarget = AirPlayController.ShouldRetargetVolume(
            VolumeModes.System, autoRetarget: true, streaming: true);
        _out.WriteLine($"system + autoRetarget + streaming -> retarget={retarget}");

        Assert.True(retarget);
    }

    [Fact]
    public void Nothing_retargets_when_no_stream_is_running()
    {
        foreach (var mode in new[] { VolumeModes.Eapo, VolumeModes.System })
        {
            bool retarget = AirPlayController.ShouldRetargetVolume(mode, true, streaming: false);
            _out.WriteLine($"{mode} + not streaming -> retarget={retarget}");
            Assert.False(retarget);
        }
    }

    [Fact]
    public void System_mode_respects_the_setting_being_off()
    {
        bool retarget = AirPlayController.ShouldRetargetVolume(
            VolumeModes.System, autoRetarget: false, streaming: true);
        _out.WriteLine($"system + autoRetarget OFF + streaming -> retarget={retarget}");

        Assert.False(retarget);
    }

    [Fact]
    public void An_unknown_volume_mode_does_not_retarget()
    {
        // Defensive: a settings file naming a mode this build does not know must not silently
        // start driving the receiver from the volume keys.
        bool retarget = AirPlayController.ShouldRetargetVolume("something-else", true, true);
        _out.WriteLine($"unknown mode -> retarget={retarget}");
        Assert.False(retarget);
    }

    [Fact]
    public void The_setting_is_only_meaningful_in_system_mode()
    {
        _out.WriteLine($"eapo -> applies={AirPlayController.RetargetSettingApplies(VolumeModes.Eapo)}");
        _out.WriteLine($"system -> applies={AirPlayController.RetargetSettingApplies(VolumeModes.System)}");

        // Drives whether the Settings checkbox is enabled or shown greyed with an explanation.
        Assert.False(AirPlayController.RetargetSettingApplies(VolumeModes.Eapo));
        Assert.True(AirPlayController.RetargetSettingApplies(VolumeModes.System));
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
