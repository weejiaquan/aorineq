using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>Finding VB-Cable on the real endpoint list, by the hardware id VB-Audio's INF gives its
/// device (VBAudioVACWDM) — never by name, because users rename endpoints.</summary>
[Collection(RealAudioDeviceCollection.Name)]
public class VirtualCableTests
{
    private readonly ITestOutputHelper _out;
    public VirtualCableTests(ITestOutputHelper output) => _out = output;

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void Every_active_endpoint_reports_its_devices_hardware_ids_and_only_VB_Cable_matches()
    {
        var endpoints = AudioEndpoint.GetRenderEndpoints();
        int withIds = 0;
        foreach (var e in endpoints)
        {
            var ids = VirtualCable.HardwareIdsOf(e.Id);
            _out.WriteLine($"{e.FriendlyName} | {e.Id} | instance={AudioEndpoint.GetDeviceInstanceId(e.Id)} "
                + $"| hwids=[{string.Join(", ", ids)}] | cable={VirtualCable.IsCable(e)}");
            if (ids.Count > 0) withIds++;
            Assert.Equal(ids.Contains(VirtualCable.HardwareId, StringComparer.OrdinalIgnoreCase),
                VirtualCable.IsCable(e));
        }
        Assert.True(withIds > 0,
            "no active endpoint's device reported a hardware id - the devnode lookup is broken");

        var found = VirtualCable.Find();
        _out.WriteLine($"Find() -> {found?.FriendlyName ?? "(none)"} {found?.Id}");
        var all = VirtualCable.FindAll();
        _out.WriteLine($"FindAll() -> [{string.Join(", ", all.Select(c => c.FriendlyName))}]");
        Assert.Equal(endpoints.Where(VirtualCable.IsCable).Select(e => e.Id), all.Select(c => c.Id));
        Assert.Equal(found?.Id, all.FirstOrDefault()?.Id);
        if (found is null)
            Assert.DoesNotContain(endpoints, VirtualCable.IsCable);
        else
        {
            Assert.Contains(endpoints, e => e.Id == found.Id);
            Assert.True(VirtualCable.IsCable(found));
        }
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void Identity_comes_from_the_hardware_id_never_the_name()
    {
        // Review focus: a user renames CABLE Input mid-stream, or names their speakers "CABLE
        // Input". The verdict for each real endpoint must not move with the name.
        foreach (var e in AudioEndpoint.GetRenderEndpoints())
        {
            bool real = VirtualCable.IsCable(e);
            bool posing = VirtualCable.IsCable(e with { FriendlyName = "CABLE Input (VB-Audio Virtual Cable)" });
            bool renamed = VirtualCable.IsCable(e with { FriendlyName = "Living room" });
            _out.WriteLine($"{e.FriendlyName}: real={real} posing-as-cable={posing} renamed={renamed}");
            Assert.Equal(real, posing);
            Assert.Equal(real, renamed);
        }
    }

    [Theory]
    [InlineData(@"ROOT\AORINEQ_NO_SUCH_DEVICE\0000")]
    [InlineData("")]
    [InlineData(null)]
    public void A_device_instance_that_does_not_exist_has_no_hardware_ids(string? instance)
    {
        var ids = VirtualCable.DevNodeHardwareIds(instance);
        _out.WriteLine($"'{instance ?? "(null)"}' -> [{string.Join(", ", ids)}]");
        Assert.Empty(ids);
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void Installed_but_inactive_is_answered_from_the_inactive_endpoints_alone()
    {
        var inactive = AudioEndpoint.GetInactiveRenderEndpoints();
        foreach (var e in inactive)
            _out.WriteLine($"inactive {e.FriendlyName} | hwids=[{string.Join(", ", VirtualCable.HardwareIdsOf(e.Id))}]");
        bool expected = inactive.Any(VirtualCable.IsCable);
        _out.WriteLine($"IsInstalledButInactive -> {VirtualCable.IsInstalledButInactive()} (expected {expected})");
        Assert.Equal(expected, VirtualCable.IsInstalledButInactive());
    }

    /// <summary>The device-node sweep behind <see cref="VirtualCable.IsDriverPresent"/>, proved on a
    /// node every machine with sound has: the hardware id of the default endpoint's own device is
    /// found among the present nodes, and an id nothing carries is not. Without the positive half,
    /// a sweep that listed nothing would pass the "no VB-Cable here" test for the wrong reason.</summary>
    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void The_present_device_sweep_finds_a_real_hardware_id_and_not_a_made_up_one()
    {
        var defaultId = AudioEndpoint.GetDefaultRenderEndpointId()!;
        var real = VirtualCable.HardwareIdsOf(defaultId);
        _out.WriteLine($"default endpoint {defaultId} | instance={AudioEndpoint.GetDeviceInstanceId(defaultId)} | hwids=[{string.Join(", ", real)}]");
        Assert.NotEmpty(real);

        bool foundReal = VirtualCable.AnyPresentDevNodeWithHardwareId(real[0]);
        bool foundLower = VirtualCable.AnyPresentDevNodeWithHardwareId(real[0].ToLowerInvariant());
        bool foundMadeUp = VirtualCable.AnyPresentDevNodeWithHardwareId("AORINEQ_NO_SUCH_HARDWARE_ID");
        _out.WriteLine($"'{real[0]}' -> {foundReal}; lower-cased -> {foundLower}; made-up -> {foundMadeUp}");
        Assert.True(foundReal);
        Assert.True(foundLower);
        Assert.False(foundMadeUp);
    }

    /// <summary>The driver check the installer asks after VB-Audio's setup exits. A cable with an
    /// endpoint (active, or turned off in Sound settings or Device Manager) has a present node; with
    /// neither, as on a machine without VB-Cable, there is none — the one state this cannot tell from
    /// the endpoints is a driver installed moments ago and waiting for a restart, which is the point
    /// of asking.</summary>
    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void The_driver_is_present_exactly_when_a_cable_endpoint_exists_on_a_machine_not_awaiting_a_restart()
    {
        bool found = VirtualCable.Find() is not null;
        bool inactive = VirtualCable.IsInstalledButInactive();
        bool driver = VirtualCable.IsDriverPresent();
        _out.WriteLine($"Find()={found} IsInstalledButInactive()={inactive} -> IsDriverPresent()={driver}");
        Assert.Equal(found || inactive, driver);
    }

    [Theory]
    [InlineData(true, false, false, VirtualCableState.Present)]
    [InlineData(true, true, true, VirtualCableState.Present)]
    [InlineData(false, false, true, VirtualCableState.PendingReboot)]
    [InlineData(false, true, true, VirtualCableState.PendingReboot)]
    [InlineData(false, true, false, VirtualCableState.Inactive)]
    [InlineData(false, false, false, VirtualCableState.Missing)]
    public void The_page_state_follows_presence_then_pending_then_inactive(
        bool found, bool inactive, bool pending, VirtualCableState expected)
    {
        var state = VirtualCable.StateFor(found, inactive, pending);
        _out.WriteLine($"found={found} inactive={inactive} pending={pending} -> {state}");
        Assert.Equal(expected, state);
    }

    [VirtualCableFact]
    public void The_installed_cable_is_found_and_its_playback_endpoint_captures()
    {
        var cable = VirtualCable.Find()!;
        _out.WriteLine($"cable: {cable.FriendlyName} | {cable.Id} | hwids=[{string.Join(", ", VirtualCable.HardwareIdsOf(cable.Id))}]");
        Assert.Contains(VirtualCable.HardwareId, VirtualCable.HardwareIdsOf(cable.Id), StringComparer.OrdinalIgnoreCase);
        // The active cable is never also reported as a turned-off one.
        Assert.DoesNotContain(AudioEndpoint.GetInactiveRenderEndpoints(), e => e.Id == cable.Id);

        using var capture = new LoopbackCapture();
        bool started = capture.Start(cable.Id);
        _out.WriteLine($"loopback on the cable -> {started}, rate={capture.SampleRate}");
        Assert.True(started);
        capture.Stop();
    }
}
