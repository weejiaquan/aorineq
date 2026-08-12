using Microsoft.Win32;

namespace AorinEQ.Core;

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

    /// <summary>The per-user state folder under %APPDATA%.</summary>
    public const string StateFolderName = "AorinEQ";

    /// <summary>The per-user state root, %APPDATA%\AorinEQ — settings.json, skins, presets, the
    /// AutoEq index cache and the protocol spool all live under it. Every one of those paths
    /// resolves through here rather than re-spelling the folder name, so the folder is named in
    /// exactly one place (see <see cref="AppDataMigration"/>, which renamed it in v3.0.0).
    /// Created if missing.</summary>
    public static string GetStateRoot()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), StateFolderName);
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>Resolves (and creates, if missing) the per-user skins root: %APPDATA%\AorinEQ\skins.</summary>
    public static string GetSkinsRoot() => CreateUnderStateRoot("skins");

    /// <summary>Resolves (and creates, if missing) the per-user EQ presets root:
    /// %APPDATA%\AorinEQ\presets.</summary>
    public static string GetPresetsRoot() => CreateUnderStateRoot("presets");

    private static string CreateUnderStateRoot(string name)
    {
        var root = Path.Combine(GetStateRoot(), name);
        Directory.CreateDirectory(root);
        return root;
    }
}
