using Microsoft.Win32;

namespace ApoVolume.Core;

/// <summary>Start-with-Windows via HKCU Run key. Key path is injectable so tests use a real
/// dedicated subkey. All failures (policy-locked registry, denied access) surface as
/// <see cref="InvalidOperationException"/> — the same contract as
/// <see cref="ScheduledTaskAutostart"/>, so callers have one exception type to handle.</summary>
public sealed class Autostart
{
    public const string ValueName = "ApoVolume";
    private readonly string _runKeyPath;

    public Autostart(string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run")
        => _runKeyPath = runKeyPath;

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath);
            return key?.GetValue(ValueName) is string;
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            return false; // unreadable counts as "not enabled" — conservative, never crashing
        }
    }

    public void Enable(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(_runKeyPath);
            key.SetValue(ValueName, $"\"{exePath}\"");
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            throw new InvalidOperationException(
                $"Couldn't register autostart in the Windows registry: {ex.Message}", ex);
        }
    }

    public void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            throw new InvalidOperationException(
                $"Couldn't remove the autostart registry entry: {ex.Message}", ex);
        }
    }

    private static bool IsRegistryFailure(Exception ex) =>
        ex is UnauthorizedAccessException or System.Security.SecurityException or IOException;
}
