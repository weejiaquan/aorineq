using System;
using Microsoft.Win32;

namespace AorinEQ.UI;

/// <summary>Reads the Windows 11 apps theme (light/dark) and DWM accent color from the registry
/// for the Fluent OSD style. Both reads are cheap enough to redo on every <see
/// cref="OsdWindow.ShowVolume"/> call (no change watcher needed) and are fully exception-guarded:
/// a missing key/value, wrong value type, or any registry failure resolves to the documented
/// default instead of throwing.</summary>
public static class SystemTheme
{
    private const string PersonalizeKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string DwmKeyPath = @"Software\Microsoft\Windows\DWM";
    private static readonly System.Windows.Media.Color DefaultAccent =
        System.Windows.Media.Color.FromArgb(0xFF, 0x00, 0x67, 0xC0);

    /// <summary>True when Windows apps use the light theme. Reads HKCU\...\Personalize's
    /// <c>AppsUseLightTheme</c> DWORD (1 = light, 0 = dark); defaults to <c>true</c> if the
    /// key/value is missing, isn't an int, or the registry read fails for any reason.</summary>
    public static bool AppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
            return key?.GetValue("AppsUseLightTheme") is int value ? value != 0 : true;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>True when the Windows shell — taskbar, notification area, Start — uses the light
    /// theme. Reads the same Personalize key's <c>SystemUsesLightTheme</c> DWORD (1 = light, 0 =
    /// dark). Windows lets the shell and the apps themes differ, and the tray glyph is drawn onto
    /// the taskbar, so this is the one that decides whether it is white or near-black. Falls back
    /// to <see cref="AppsUseLightTheme"/> when the value is missing or unreadable — the two match
    /// on every default Windows configuration, which beats guessing a fixed side.</summary>
    public static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
            if (key?.GetValue("SystemUsesLightTheme") is int value) return value != 0;
        }
        catch (Exception)
        {
            // fall through to the apps theme
        }
        return AppsUseLightTheme();
    }

    /// <summary>The current Windows accent color. Reads HKCU\Software\Microsoft\Windows\DWM's
    /// <c>AccentColor</c> DWORD, stored in ABGR byte order (highest byte alpha, then blue, green,
    /// red); defaults to <c>#FF0067C0</c> if the key/value is missing, isn't an int, or the
    /// registry read fails for any reason.</summary>
    public static System.Windows.Media.Color Accent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(DwmKeyPath);
            if (key?.GetValue("AccentColor") is not int raw) return DefaultAccent;

            uint abgr = unchecked((uint)raw);
            byte a = (byte)(abgr >> 24);
            byte b = (byte)(abgr >> 16);
            byte g = (byte)(abgr >> 8);
            byte r = (byte)abgr;
            return System.Windows.Media.Color.FromArgb(a, r, g, b);
        }
        catch (Exception)
        {
            return DefaultAccent;
        }
    }
}
