using Microsoft.Win32;

namespace ApoVolume.Core;

/// <summary>Start-with-Windows via HKCU Run key. Key path is injectable so tests use a real dedicated subkey.</summary>
public sealed class Autostart
{
    public const string ValueName = "ApoVolume";
    private readonly string _runKeyPath;

    public Autostart(string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run")
        => _runKeyPath = runKeyPath;

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath);
        return key?.GetValue(ValueName) is string;
    }

    public void Enable(string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_runKeyPath);
        key.SetValue(ValueName, $"\"{exePath}\"");
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
