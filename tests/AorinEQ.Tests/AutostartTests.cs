using AorinEQ.Core;
using Microsoft.Win32;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

public class AutostartTests : IDisposable
{
    private const string TestKeyRoot = @"Software\AorinEQTests";
    private const string TestRunKey = TestKeyRoot + @"\Run";
    private readonly ITestOutputHelper _out;

    public AutostartTests(ITestOutputHelper output) => _out = output;

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(TestKeyRoot, throwOnMissingSubKey: false);

    [Fact]
    public void Enable_writes_quoted_path_and_IsEnabled_reflects_it()
    {
        var a = new Autostart(TestRunKey);
        Assert.False(a.IsEnabled());

        a.Enable(@"C:\Tools\AorinEQ.exe");
        Assert.True(a.IsEnabled());

        using var key = Registry.CurrentUser.OpenSubKey(TestRunKey);
        var value = key!.GetValue(Autostart.ValueName) as string;
        _out.WriteLine("registry value: " + value);
        Assert.Equal("\"C:\\Tools\\AorinEQ.exe\"", value);
    }

    [Fact]
    public void Disable_removes_value_and_is_safe_when_absent()
    {
        var a = new Autostart(TestRunKey);
        a.Enable(@"C:\Tools\AorinEQ.exe");
        a.Disable();
        Assert.False(a.IsEnabled());
        a.Disable(); // second call must not throw
        Assert.False(a.IsEnabled());
    }
}
