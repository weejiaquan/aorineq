using Microsoft.Win32;

namespace ApoVolume.Core;

public static class ApoPaths
{
    /// <summary>Resolves the Equalizer APO config directory. Throws if APO is not installed (fail fast).</summary>
    public static string GetConfigDir()
    {
        string? install;
        using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        using (var key = baseKey.OpenSubKey(@"SOFTWARE\EqualizerAPO"))
        {
            install = key?.GetValue("InstallPath") as string;
        }

        install ??= @"C:\Program Files\EqualizerAPO";
        var configDir = Path.Combine(install, "config");
        if (!Directory.Exists(configDir))
            throw new DirectoryNotFoundException(
                $"Equalizer APO config directory not found at '{configDir}'. " +
                "Install Equalizer APO (https://equalizerapo.com) and install it on your DAC's playback device.");
        return configDir;
    }

    /// <summary>Resolves (and creates, if missing) the per-user skins root: %APPDATA%\apo-volume\skins.</summary>
    public static string GetSkinsRoot()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "apo-volume", "skins");
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>Resolves (and creates, if missing) the per-user EQ presets root:
    /// %APPDATA%\apo-volume\presets.</summary>
    public static string GetPresetsRoot()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "apo-volume", "presets");
        Directory.CreateDirectory(root);
        return root;
    }
}
