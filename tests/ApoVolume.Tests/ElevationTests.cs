using System.Security.Principal;
using ApoVolume.Core;
using Xunit;
using Xunit.Abstractions;

namespace ApoVolume.Tests;

public class ElevationTests
{
    private readonly ITestOutputHelper _out;
    public ElevationTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void IsElevated_matches_windows_principal()
    {
        using var id = WindowsIdentity.GetCurrent();
        var expected = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        _out.WriteLine($"expected elevation: {expected}, Elevation.IsElevated: {Elevation.IsElevated}");
        Assert.Equal(expected, Elevation.IsElevated);
    }
}
