using ApoVolume.Core;
using Microsoft.Win32;
using Xunit;
using Xunit.Abstractions;

namespace ApoVolume.Tests;

/// <summary>Real-registry tests (repo convention: no mocks) against HKCU\Software\Classes with a
/// throwaway scheme name, cleaned up in Dispose the way the schtasks tests clean their task.</summary>
public class ProtocolRegistrationTests : IDisposable
{
    private readonly string _scheme = "apo-volume-test-" + Guid.NewGuid().ToString("N")[..8];
    private readonly ProtocolRegistration _reg;
    private readonly ITestOutputHelper _out;

    public ProtocolRegistrationTests(ITestOutputHelper output)
    {
        _out = output;
        _reg = new ProtocolRegistration(_scheme);
    }

    public void Dispose()
    {
        try { _reg.Unregister(); } catch (InvalidOperationException) { }
    }

    [Fact]
    public void Register_writes_url_protocol_shape_and_IsRegistered_reflects_it()
    {
        Assert.False(_reg.IsRegisteredFor(@"C:\Tools\ApoVolume.exe"));
        _reg.Register(@"C:\Tools\ApoVolume.exe");
        _out.WriteLine($"registered scheme {_scheme}");
        Assert.True(_reg.IsRegisteredFor(@"C:\Tools\ApoVolume.exe"));

        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + _scheme);
        Assert.NotNull(key);
        // The empty "URL Protocol" value is what makes Windows treat this class as a scheme.
        Assert.Equal("", key!.GetValue("URL Protocol"));
        _out.WriteLine("default: " + key.GetValue(null));
        using var cmd = key.OpenSubKey(@"shell\open\command");
        var command = (string?)cmd?.GetValue(null);
        _out.WriteLine("command: " + command);
        Assert.Equal("\"C:\\Tools\\ApoVolume.exe\" \"%1\"", command);
        using var icon = key.OpenSubKey("DefaultIcon");
        Assert.Equal("\"C:\\Tools\\ApoVolume.exe\",0", icon?.GetValue(null));
    }

    [Fact]
    public void Register_is_idempotent_and_repoints_to_a_new_exe_path()
    {
        _reg.Register(@"C:\Old\ApoVolume.exe");
        _reg.Register(@"C:\New Path\ApoVolume.exe"); // moved exe: re-register must re-point
        Assert.False(_reg.IsRegisteredFor(@"C:\Old\ApoVolume.exe"));
        Assert.True(_reg.IsRegisteredFor(@"C:\New Path\ApoVolume.exe"));

        using var cmd = Registry.CurrentUser.OpenSubKey(
            @"Software\Classes\" + _scheme + @"\shell\open\command");
        var command = (string?)cmd?.GetValue(null);
        _out.WriteLine("command after re-register: " + command);
        Assert.Equal("\"C:\\New Path\\ApoVolume.exe\" \"%1\"", command);
    }

    [Fact]
    public void Unregister_removes_the_key_and_is_safe_when_absent()
    {
        _reg.Register(@"C:\Tools\ApoVolume.exe");
        _reg.Unregister();
        Assert.False(_reg.IsRegisteredFor(@"C:\Tools\ApoVolume.exe"));
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + _scheme);
        Assert.Null(key);
        _reg.Unregister(); // absent: must not throw
    }

    [Fact]
    public void Register_rejects_exe_path_with_quote()
    {
        // A quote in the path would break out of the quoted command template.
        var ex = Assert.Throws<ArgumentException>(() => _reg.Register("C:\\bad\"path\\app.exe"));
        _out.WriteLine("rejected: " + ex.Message);
        Assert.Contains("quote", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsRegisteredFor_is_false_for_a_different_exe()
    {
        _reg.Register(@"C:\Tools\ApoVolume.exe");
        Assert.False(_reg.IsRegisteredFor(@"C:\Elsewhere\ApoVolume.exe"));
    }
}
