using Microsoft.Win32;

namespace AorinEQ.Core;

/// <summary>Registers the aorineq:// URL scheme under HKCU\Software\Classes (per-user, no
/// elevation). Register is idempotent and re-points the handler at the current exe path — which
/// also keeps the registration valid across the auto-updater's in-place exe swap, since the path
/// itself never changes. The scheme name is parameterized for tests only.</summary>
public sealed class ProtocolRegistration
{
    private readonly string _scheme;

    public ProtocolRegistration(string scheme = ProtocolLink.Scheme) => _scheme = scheme;

    private string KeyPath => @"Software\Classes\" + _scheme;

    private static string CommandFor(string exePath) => $"\"{exePath}\" \"%1\"";

    /// <summary>Writes the protocol class: empty "URL Protocol" marker value, DefaultIcon, and
    /// shell\open\command pointing at <paramref name="exePath"/>. Throws
    /// <see cref="InvalidOperationException"/> with a readable message on registry failure.</summary>
    public void Register(string exePath)
    {
        if (exePath.Contains('"'))
            throw new ArgumentException("Executable path cannot contain double-quote characters.",
                nameof(exePath));
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue(null, "URL:" + _scheme);
            key.SetValue("URL Protocol", "");
            using (var icon = key.CreateSubKey("DefaultIcon"))
                icon.SetValue(null, $"\"{exePath}\",0");
            using (var cmd = key.CreateSubKey(@"shell\open\command"))
                cmd.SetValue(null, CommandFor(exePath));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException
            or IOException)
        {
            throw new InvalidOperationException(
                $"Couldn't register the {_scheme}:// link handler: {ex.Message}", ex);
        }
    }

    /// <summary>Deletes the scheme registration. Safe when absent.</summary>
    public void Unregister()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException
            or IOException)
        {
            throw new InvalidOperationException(
                $"Couldn't remove the {_scheme}:// link handler: {ex.Message}", ex);
        }
    }

    /// <summary>Whether the scheme is registered AND its open command points at
    /// <paramref name="exePath"/> — a registration for a moved/stale exe counts as not
    /// registered, so startup re-registration re-points it.</summary>
    public bool IsRegisteredFor(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key?.GetValue("URL Protocol") is null) return false;
            using var cmd = key.OpenSubKey(@"shell\open\command");
            return (cmd?.GetValue(null) as string) == CommandFor(exePath);
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
    }
}
