using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>Real-COM tests against this machine's audio endpoints (no mocks, per repo policy).
/// They require at least one active render device, which every dev/E2E machine here has.</summary>
[Collection(RealAudioDeviceCollection.Name)]
[Trait(Requires.Key, Requires.AudioEndpoint)]
public class AudioEndpointTests
{
    private readonly ITestOutputHelper _out;
    public AudioEndpointTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void GetRenderEndpoints_lists_active_devices_with_names_and_guids()
    {
        var endpoints = AudioEndpoint.GetRenderEndpoints();
        foreach (var e in endpoints)
            _out.WriteLine($"{e.Id} | {e.FriendlyName} | {e.Guid}");
        Assert.NotEmpty(endpoints);
        Assert.All(endpoints, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Id));
            Assert.False(string.IsNullOrWhiteSpace(e.FriendlyName));
            Assert.True(Guid.TryParse(e.Guid, out _), $"guid not parseable: {e.Guid}");
            Assert.StartsWith("{", e.Guid);
        });
    }

    [Fact]
    public void Default_render_endpoint_appears_in_the_enumeration()
    {
        var defaultId = AudioEndpoint.GetDefaultRenderEndpointId();
        _out.WriteLine($"default: {defaultId}");
        Assert.NotNull(defaultId);
        var endpoints = AudioEndpoint.GetRenderEndpoints();
        Assert.Contains(endpoints, e => e.Id == defaultId);
    }

    [Fact]
    public void Every_role_has_a_default_and_multimedia_is_the_one_the_app_follows()
    {
        var active = AudioEndpoint.GetRenderEndpoints().Select(e => e.Id).ToHashSet();
        foreach (var role in new[] { AudioEndpoint.ERole.Console, AudioEndpoint.ERole.Multimedia,
                     AudioEndpoint.ERole.Communications })
        {
            var id = AudioEndpoint.GetDefaultRenderEndpointId(role);
            _out.WriteLine($"{role}: {id ?? "(none)"}");
            Assert.NotNull(id);
            Assert.Contains(id!, active);
        }
        Assert.Equal(AudioEndpoint.GetDefaultRenderEndpointId(),
            AudioEndpoint.GetDefaultRenderEndpointId(AudioEndpoint.ERole.Multimedia));
    }

    [Fact]
    public void The_default_endpoint_names_its_device_instance_without_the_enumerator_prefix()
    {
        var id = AudioEndpoint.GetDefaultRenderEndpointId()!;
        var instance = AudioEndpoint.GetDeviceInstanceId(id);
        _out.WriteLine($"endpoint {id} -> device instance '{instance ?? "(null)"}'");

        Assert.NotNull(instance);
        Assert.DoesNotContain("}.", instance!);   // "{1}." stripped
        Assert.Contains((char)92, instance!);       // the backslash in ENUMERATOR, DEVICE, INSTANCE
    }

    [Fact]
    public void An_endpoint_id_that_names_nothing_has_no_device_instance()
    {
        var instance = AudioEndpoint.GetDeviceInstanceId(
            "{0.0.0.00000000}.{deadbeef-0000-0000-0000-000000000000}");
        _out.WriteLine($"bogus endpoint -> '{instance ?? "(null)"}'");
        Assert.Null(instance);
    }

    [Fact]
    public void The_inactive_list_never_holds_the_active_default_or_any_other_active_endpoint()
    {
        // Measured against an endpoint KNOWN to be active — the default — so this can fail on any
        // machine, including one with no inactive endpoints at all: a state mask that let ACTIVE
        // devices in would list the default here.
        var defaultId = AudioEndpoint.GetDefaultRenderEndpointId();
        Assert.NotNull(defaultId);
        var active = AudioEndpoint.GetRenderEndpoints().Select(e => e.Id).ToHashSet();
        var inactive = AudioEndpoint.GetInactiveRenderEndpoints();
        _out.WriteLine($"default (active): {defaultId}; {active.Count} active, {inactive.Count} inactive");
        foreach (var e in inactive)
            _out.WriteLine($"inactive: {e.FriendlyName} | {e.Id} | instance={AudioEndpoint.GetDeviceInstanceId(e.Id)}");

        Assert.Contains(defaultId!, active);
        Assert.DoesNotContain(inactive, e => e.Id == defaultId);
        Assert.All(inactive, e =>
        {
            Assert.DoesNotContain(e.Id, active);
            Assert.True(Guid.TryParse(e.Guid, out _), $"guid not parseable: {e.Guid}");
        });
    }
}

/// <summary>The endpoint property names its parent device as "{n}.ENUM\DEVICE\INSTANCE"; the
/// configuration manager wants it without the "{n}." prefix.</summary>
public class AudioEndpointInstanceIdTests
{
    private readonly ITestOutputHelper _out;
    public AudioEndpointInstanceIdTests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(@"{1}.ROOT\MEDIA\0000", @"ROOT\MEDIA\0000")]
    [InlineData(@"{1}.USB\VID_32BB&PID_0004&MI_00\9&2B941AD5&0&0000", @"USB\VID_32BB&PID_0004&MI_00\9&2B941AD5&0&0000")]
    [InlineData(@"{12}.SWD\DRIVERENUM\X", @"SWD\DRIVERENUM\X")]
    [InlineData(@"ROOT\MEDIA\0000", @"ROOT\MEDIA\0000")]
    [InlineData(@"{broken", @"{broken")]
    public void The_enumerator_prefix_is_stripped_and_nothing_else(string raw, string expected)
    {
        var stripped = AudioEndpoint.StripEnumeratorPrefix(raw);
        _out.WriteLine($"'{raw}' -> '{stripped}'");
        Assert.Equal(expected, stripped);
    }
}
