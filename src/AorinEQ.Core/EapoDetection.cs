using Microsoft.Win32;

namespace AorinEQ.Core;

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

    /// <summary>Whether Equalizer APO will actually process this endpoint. BOTH halves have to
    /// hold, and they fail independently:
    ///
    /// <list type="bullet">
    /// <item>Equalizer APO's own record for the device —
    /// <c>HKLM\SOFTWARE\EqualizerAPO\Child APOs\{guid}</c>, written by its Configurator when a
    /// device is ticked;</item>
    /// <item>the device's own property store naming Equalizer APO's APOs
    /// (see <see cref="EapoEndpoint.IsApoAttached"/>).</item>
    /// </list>
    ///
    /// Checking only the first — which this returned until v3.4.0 — misses the failure this whole
    /// release exists for: a Windows update that replaces the audio driver resets the ENDPOINT's
    /// property store and leaves Equalizer APO's record untouched, so the device looks registered
    /// while nothing is processing. The second half is the one the audio engine actually reads.</summary>
    public static bool IsActiveOnEndpoint(string? endpointGuid)
    {
        if (string.IsNullOrEmpty(endpointGuid))
            return false;
        if (!HasChildApoRecord(endpointGuid))
            return false;
        if (GetInstallPath() is not { } install)
            return false;
        return EapoEndpoint.ResolveClsids(install) is { } clsids
            && EapoEndpoint.IsApoAttached(endpointGuid, clsids);
    }

    /// <summary>Equalizer APO's own bookkeeping for a device, on its own. Separate from
    /// <see cref="IsActiveOnEndpoint"/> because a repair has to be able to tell the two halves
    /// apart: a record with no attachment is the driver-reset case, and an attachment with no
    /// record is a half-finished repair.</summary>
    public static bool HasChildApoRecord(string? endpointGuid)
    {
        if (string.IsNullOrEmpty(endpointGuid))
            return false;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(EapoEndpoint.ChildApoRoot + "\\" + endpointGuid);
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
