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

    // ---- v3.0.0 rename migration ----

    private const string LegacyValue = "ApoVolume";

    private static string? ReadValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(TestRunKey);
        return key?.GetValue(name) as string;
    }

    [Fact]
    public void MigrateLegacyValue_repoints_the_entry_at_the_current_exe_under_the_new_name()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(TestRunKey))
            key.SetValue(LegacyValue, "\"C:\\Tools\\ApoVolume.exe\"");
        var a = new Autostart(TestRunKey);

        Assert.True(a.MigrateLegacyValue(LegacyValue, @"C:\Tools\AorinEQ.exe"));

        _out.WriteLine("after: " + ReadValue(Autostart.ValueName));
        // Re-pointed, not copied: the exe may have just been renamed alongside the app.
        Assert.Equal("\"C:\\Tools\\AorinEQ.exe\"", ReadValue(Autostart.ValueName));
        Assert.Null(ReadValue(LegacyValue));
        Assert.True(a.IsEnabled());
    }

    [Fact]
    public void MigrateLegacyValue_is_a_no_op_without_a_legacy_entry()
    {
        var a = new Autostart(TestRunKey);

        Assert.False(a.MigrateLegacyValue(LegacyValue, @"C:\Tools\AorinEQ.exe"));

        // Autostart was off and must stay off — migration never turns it ON.
        Assert.False(a.IsEnabled());
        Assert.Null(ReadValue(Autostart.ValueName));
    }

    [Fact]
    public void MigrateLegacyValue_removes_the_legacy_entry_even_when_the_new_one_exists()
    {
        // Both present (a half-finished earlier attempt): two Run entries would launch the app
        // twice at logon.
        var a = new Autostart(TestRunKey);
        a.Enable(@"C:\Tools\AorinEQ.exe");
        using (var key = Registry.CurrentUser.CreateSubKey(TestRunKey))
            key.SetValue(LegacyValue, "\"C:\\Tools\\ApoVolume.exe\"");

        Assert.True(a.MigrateLegacyValue(LegacyValue, @"C:\Tools\AorinEQ.exe"));

        Assert.Equal("\"C:\\Tools\\AorinEQ.exe\"", ReadValue(Autostart.ValueName));
        Assert.Null(ReadValue(LegacyValue));
    }

    [Fact]
    public void MigrateLegacyValue_refuses_to_migrate_a_name_onto_itself()
    {
        // Guard against deleting the very entry just written.
        var a = new Autostart(TestRunKey);
        a.Enable(@"C:\Tools\AorinEQ.exe");

        Assert.False(a.MigrateLegacyValue(Autostart.ValueName, @"C:\Tools\AorinEQ.exe"));

        Assert.True(a.IsEnabled());
    }

    [Fact]
    public void The_legacy_value_name_is_the_apps_old_name()
    {
        Assert.Equal("ApoVolume", Autostart.LegacyValueName);
        Assert.Equal("AorinEQ", Autostart.ValueName);
    }
}
