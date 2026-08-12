using AorinEQ.Core;
using Microsoft.Win32;

namespace AorinEQ.UI;

/// <summary>Reads the Windows personalization values the app draws with — the apps theme, the
/// shell theme, and the DWM accent colour — and hands them to <see cref="AppThemePolicy"/>, which
/// owns what they MEAN.
///
/// This half is deliberately dumb: open the key, fetch the value, return <c>null</c> if anything at
/// all goes wrong (missing key, missing value, wrong value type, access denied). Every fallback
/// decision lives in the policy, where it is tested. Both reads are cheap enough to redo on every
/// <see cref="OsdWindow.ShowVolume"/> call.
///
/// This is the app's ONLY reader of the Windows theme: the tray glyph, the Fluent OSD style and
/// <see cref="AppTheme"/> (the Fluent window chrome) all come through here, so the process never
/// disagrees with itself about whether Windows is dark.</summary>
public static class SystemTheme
{
    private const string PersonalizeKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string DwmKeyPath = @"Software\Microsoft\Windows\DWM";

    /// <summary>True when Windows apps use the light theme.</summary>
    public static bool AppsUseLightTheme() =>
        AppThemePolicy.IsLight(ReadDword(PersonalizeKeyPath, "AppsUseLightTheme"));

    /// <summary>True when the Windows shell — taskbar, notification area, Start — uses the light
    /// theme. Windows lets it differ from the apps theme, and the tray glyph is drawn onto the
    /// taskbar, so this is the one that decides whether it is white or near-black.</summary>
    public static bool SystemUsesLightTheme() =>
        AppThemePolicy.IsShellLight(
            ReadDword(PersonalizeKeyPath, "SystemUsesLightTheme"),
            ReadDword(PersonalizeKeyPath, "AppsUseLightTheme"));

    /// <summary>The current Windows accent colour, as a WPF colour for the brushes the OSD and the
    /// window chrome build from it.</summary>
    public static System.Windows.Media.Color Accent()
    {
        var color = AppThemePolicy.AccentFromAbgr(ReadDword(DwmKeyPath, "AccentColor"));
        return System.Windows.Media.Color.FromArgb(color.A, color.R, color.G, color.B);
    }

    /// <summary>The raw DWORD, or <c>null</c> when it is missing, is not an int, or the registry
    /// read fails for any reason — the theme must never be able to throw out of a paint path.</summary>
    private static int? ReadDword(string keyPath, string valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(valueName) as int?;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
