using Microsoft.Win32;

namespace ApoVolume.Core;

public enum EapoStatus
{
    /// <summary>Equalizer APO is not installed (no registry key / config dir).</summary>
    NotInstalled,
    /// <summary>Installed, but not enabled on the current default playback device — volume
    /// writes succeed but have no audible effect on that device.</summary>
    InstalledInactive,
    /// <summary>Installed and enabled on the current default playback device.</summary>
    Active,
}

/// <summary>Non-throwing Equalizer APO state detection for onboarding. Conservative on any
/// registry/COM failure: never reports a better state than it can prove.</summary>
public static class EapoDetection
{
    /// <summary>The registered install path when EAPO is installed and its config dir exists,
    /// else null. (Non-throwing variant of <see cref="ApoPaths.GetConfigDir"/>'s logic.)</summary>
    public static string? GetInstallPath()
    {
        try
        {
            string? install;
            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = baseKey.OpenSubKey(@"SOFTWARE\EqualizerAPO"))
            {
                install = key?.GetValue("InstallPath") as string;
            }
            install ??= @"C:\Program Files\EqualizerAPO";
            return Directory.Exists(Path.Combine(install, "config")) ? install : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException
            or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Full path to Configurator.exe when installed, else null.</summary>
    public static string? GetConfiguratorPath()
    {
        var install = GetInstallPath();
        if (install is null)
            return null;
        var path = Path.Combine(install, "Configurator.exe");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Whether EAPO records an install on the given endpoint GUID — the
    /// <c>HKLM\SOFTWARE\EqualizerAPO\Child APOs\{guid}</c> subkey EAPO's own Configurator
    /// writes when a device is ticked.</summary>
    public static bool IsActiveOnEndpoint(string? endpointGuid)
    {
        if (string.IsNullOrEmpty(endpointGuid))
            return false;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\EqualizerAPO\Child APOs\" + endpointGuid);
            return key is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Overall status against the CURRENT default render device. When the default
    /// endpoint can't be determined (no audio device), an installed EAPO reports
    /// <see cref="EapoStatus.InstalledInactive"/> — conservative, never optimistic.</summary>
    public static EapoStatus Detect()
    {
        if (GetInstallPath() is null)
            return EapoStatus.NotInstalled;
        var guid = AudioEndpoint.EndpointGuid(AudioEndpoint.GetDefaultRenderEndpointId());
        return IsActiveOnEndpoint(guid) ? EapoStatus.Active : EapoStatus.InstalledInactive;
    }
}
