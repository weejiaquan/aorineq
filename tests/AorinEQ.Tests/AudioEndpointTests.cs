using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>Real-COM tests against this machine's audio endpoints (no mocks, per repo policy).
/// They require at least one active render device, which every dev/E2E machine here has.</summary>
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
}
