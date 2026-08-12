using System.Drawing;

namespace AorinEQ.Core;

/// <summary>The rules that turn Windows' raw personalization DWORDs into the two facts the app
/// draws with: whether Windows is in light or dark mode, and what the accent colour is.
///
/// This is the POLICY only — reading the registry is the app's job (see <c>SystemTheme</c>), which
/// hands the values it found (or <c>null</c> when a key/value is missing or unreadable) straight
/// through. Splitting it this way is what lets the fallback rules below be tested at all: they used
/// to be inline <c>?:</c> expressions inside three separate <c>try</c> blocks.
///
/// Three consumers share it — the tray glyph, the Fluent OSD style, and the app's own window
/// theme — so there is exactly one answer to "is Windows dark right now" in the process.</summary>
public static class AppThemePolicy
{
    /// <summary>The Windows default accent (#FF0067C0), used whenever DWM's value is absent or
    /// unreadable. Matching Windows' own default beats inventing a colour.</summary>
    public static readonly Color DefaultAccent = Color.FromArgb(0xFF, 0x00, 0x67, 0xC0);

    /// <summary>True when Windows apps use the light theme, from the Personalize key's
    /// <c>AppsUseLightTheme</c> DWORD (1 = light, 0 = dark). The DWORD is a boolean, so any
    /// non-zero value is light. A missing value resolves to LIGHT: that is the Windows default,
    /// and the value is genuinely absent on installs that have never left it.</summary>
    public static bool IsLight(int? appsUseLightTheme) => appsUseLightTheme is not 0;

    /// <summary>True when the Windows SHELL — taskbar, notification area, Start — uses the light
    /// theme, from the same key's <c>SystemUsesLightTheme</c> DWORD.
    ///
    /// Windows lets the shell and the apps themes differ, and the tray glyph is drawn onto the
    /// taskbar, so the shell's own value wins whenever it is present. When it is absent this falls
    /// back to the APPS theme rather than to a fixed side: the two match on every default Windows
    /// configuration, so following the apps theme is right far more often than guessing, and
    /// guessing wrong paints a white glyph onto a white taskbar.</summary>
    public static bool IsShellLight(int? systemUsesLightTheme, int? appsUseLightTheme) =>
        systemUsesLightTheme is { } shell ? shell != 0 : IsLight(appsUseLightTheme);

    /// <summary>The accent colour from DWM's <c>AccentColor</c> DWORD.
    ///
    /// The value is stored ABGR, NOT ARGB — highest byte alpha, then blue, green, red. Reading it
    /// the obvious way swaps red and blue, which is invisible on a grey accent and glaring on the
    /// default blue. Falls back to <see cref="DefaultAccent"/> when absent.</summary>
    public static Color AccentFromAbgr(int? raw)
    {
        if (raw is not { } value) return DefaultAccent;

        uint abgr = unchecked((uint)value);
        byte a = (byte)(abgr >> 24);
        byte b = (byte)(abgr >> 16);
        byte g = (byte)(abgr >> 8);
        byte r = (byte)abgr;
        return Color.FromArgb(a, r, g, b);
    }
}
