using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>The elevated repair helper's two inputs that are not the registry: which endpoint the
/// command line names, and whether that endpoint still opens for playback afterwards. Both real —
/// the argument check asks the machine whether the device exists and whether it is one AorinEQ may
/// change (the default, or VB-Cable), and the usability check opens a real loopback capture on it.</summary>
[Collection(RealAudioDeviceCollection.Name)]
public class RepairHelperTests
{
    private readonly ITestOutputHelper _out;
    public RepairHelperTests(ITestOutputHelper output) => _out = output;

    private const string NoSuchDevice = "{deadbeef-0000-0000-0000-000000000000}";

    [Fact]
    public void No_endpoint_argument_means_the_default_device()
    {
        var verdict = App.RepairEndpointFrom(["--repair-eapo", "--repair-token", new string('a', 32)], out var guid);
        _out.WriteLine($"no --endpoint -> {verdict} guid={guid ?? "(default)"}");
        Assert.Equal(App.RepairEndpointVerdict.Accepted, verdict);
        Assert.Null(guid);
    }

    [Theory]
    [InlineData(null)]            // "--endpoint" with nothing after it
    [InlineData("not-a-guid")]
    [InlineData("..\\..\\x")]
    [InlineData(NoSuchDevice)]
    public void An_endpoint_argument_that_is_not_a_present_device_is_not_found(string? value)
    {
        string[] args = value is null ? ["--repair-eapo", "--endpoint"] : ["--repair-eapo", "--endpoint", value];
        var verdict = App.RepairEndpointFrom(args, out var guid);
        _out.WriteLine($"[{string.Join(' ', args)}] -> {verdict} guid={guid ?? "(none)"}");
        Assert.Equal(App.RepairEndpointVerdict.NotFound, verdict);
        Assert.Null(guid);
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void The_default_endpoint_is_accepted_and_normalised_whatever_its_format()
    {
        var real = AudioEndpoint.EndpointGuid(AudioEndpoint.GetDefaultRenderEndpointId())!;
        var bare = Guid.Parse(real).ToString("D").ToUpperInvariant();
        var verdict = App.RepairEndpointFrom(["--undo-eapo-repair", "--endpoint", bare], out var guid);
        _out.WriteLine($"--endpoint {bare} -> {verdict} guid={guid}");
        Assert.Equal(App.RepairEndpointVerdict.Accepted, verdict);
        Assert.Equal(Guid.Parse(real).ToString("B"), guid);
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    [Trait(Requires.Key, Requires.MultipleNonCableRenderEndpoints)]
    public void A_present_endpoint_that_is_neither_the_default_nor_VB_Cable_is_refused()
    {
        // The elevated helper never takes an arbitrary target from its command line: a real device
        // that is not the one the user is listening through, and is not the cable, is refused even
        // though it exists. Needs a second ACTIVE endpoint that is not VB-Cable.
        var defaultId = AudioEndpoint.GetDefaultRenderEndpointId();
        var bystander = AudioEndpoint.GetRenderEndpoints()
            .FirstOrDefault(e => e.Id != defaultId && !VirtualCable.IsCable(e));
        _out.WriteLine($"default {defaultId}; bystander {bystander?.FriendlyName ?? "(none)"} {bystander?.Guid}");
        Assert.NotNull(bystander);

        var verdict = App.RepairEndpointFrom(["--repair-eapo", "--endpoint", bystander!.Guid], out var guid);
        _out.WriteLine($"--endpoint {bystander.Guid} -> {verdict} guid={guid ?? "(none)"}");
        Assert.True(AudioEndpoint.IsEndpointPresent(bystander.Guid));
        Assert.Equal(App.RepairEndpointVerdict.NotAllowed, verdict);
        Assert.Null(guid);
    }

    [VirtualCableFact]
    public void VB_Cable_is_accepted_whether_or_not_it_is_the_default()
    {
        var cable = VirtualCable.Find()!;
        var bare = Guid.Parse(cable.Guid).ToString("D");
        var verdict = App.RepairEndpointFrom(["--repair-eapo", "--endpoint", bare], out var guid);
        _out.WriteLine($"cable {cable.FriendlyName} (default: {AudioEndpoint.GetDefaultRenderEndpointId() == cable.Id}) "
            + $"--endpoint {bare} -> {verdict} guid={guid}");
        Assert.Equal(App.RepairEndpointVerdict.Accepted, verdict);
        Assert.Equal(Guid.Parse(cable.Guid).ToString("B"), guid);
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void The_default_endpoint_is_usable_whether_or_not_it_is_named()
    {
        var real = AudioEndpoint.EndpointGuid(AudioEndpoint.GetDefaultRenderEndpointId())!;
        bool asDefault = App.IsEndpointUsable(real, repairingDefault: true);
        bool asNamed = App.IsEndpointUsable(real, repairingDefault: false);
        _out.WriteLine($"{real}: as default={asDefault}, as named endpoint={asNamed}");
        Assert.True(asDefault);
        Assert.True(asNamed);
    }

    [Fact]
    public void An_endpoint_that_does_not_exist_is_never_usable()
    {
        Assert.False(App.IsEndpointUsable(NoSuchDevice, repairingDefault: true));
        Assert.False(App.IsEndpointUsable(NoSuchDevice, repairingDefault: false));
        _out.WriteLine("made-up endpoint -> unusable either way");
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    [Trait(Requires.Key, Requires.MultipleRenderEndpoints)]
    public void A_named_endpoint_is_probed_itself_and_the_default_guard_applies_only_to_the_default()
    {
        var other = DefaultRenderDevice.Other(AudioEndpoint.GetDefaultRenderEndpointId())!;
        bool named = App.IsEndpointUsable(other.Guid, repairingDefault: false);
        bool claimedDefault = App.IsEndpointUsable(other.Guid, repairingDefault: true);
        _out.WriteLine($"{other.FriendlyName}: probed as named={named}; claimed as the default={claimedDefault}");
        Assert.True(named);            // its own capture opened
        Assert.False(claimedDefault);  // it is NOT the default: evidence about another device does not count
    }
}
