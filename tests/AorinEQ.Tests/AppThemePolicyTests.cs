using AorinEQ.Core;

namespace AorinEQ.Tests;

/// <summary>The rules that turn raw registry values into "is Windows light or dark" and "what is
/// the accent colour". They used to live inline in the app's registry reader, untested, and are now
/// the single policy behind all three consumers — the tray glyph, the Fluent OSD style, and the
/// app's own window theme — so a mistake here mis-colours the whole app at once.</summary>
public class AppThemePolicyTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public AppThemePolicyTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(1, true)]   // documented: 1 = light
    [InlineData(0, false)]  // documented: 0 = dark
    [InlineData(2, true)]   // anything non-zero is light, per the DWORD's boolean sense
    [InlineData(null, true)] // missing/unreadable => light, the Windows default
    public void AppsThemeReadsTheDwordAsABooleanAndDefaultsToLight(int? raw, bool expected)
    {
        _out.WriteLine($"AppsUseLightTheme={raw?.ToString() ?? "<missing>"} -> light={expected}");
        Assert.Equal(expected, AppThemePolicy.IsLight(raw));
    }

    /// <summary>Windows lets the SHELL theme (taskbar, notification area) differ from the apps
    /// theme, and the tray glyph is drawn onto the taskbar — so it asks for the shell's value and
    /// only falls back to the apps theme when the shell's is absent. Guessing a fixed side instead
    /// would paint a white glyph onto a white taskbar on any machine missing the value.</summary>
    [Theory]
    [InlineData(0, 1, false)] // shell dark while apps light — the shell wins
    [InlineData(1, 0, true)]  // shell light while apps dark — the shell still wins
    [InlineData(null, 0, false)] // shell value absent — fall back to the apps theme
    [InlineData(null, 1, true)]
    [InlineData(null, null, true)] // neither present — light, same default as above
    public void ShellThemePrefersItsOwnValueAndFallsBackToTheAppsTheme(int? shell, int? apps, bool expected)
    {
        _out.WriteLine($"SystemUsesLightTheme={shell?.ToString() ?? "<missing>"} "
            + $"AppsUseLightTheme={apps?.ToString() ?? "<missing>"} -> shellLight={expected}");
        Assert.Equal(expected, AppThemePolicy.IsShellLight(shell, apps));
    }

    /// <summary>DWM stores the accent as ABGR, not ARGB — read it the obvious way and every accent
    /// comes out with red and blue swapped, which is invisible on a grey accent and glaring on the
    /// default blue.</summary>
    [Fact]
    public void AccentDecodesTheDwmDwordAsAbgrNotArgb()
    {
        // 0xFF C06700 as stored = A=FF, B=C0, G=67, R=00 => the Windows default blue #FF0067C0.
        var color = AppThemePolicy.AccentFromAbgr(unchecked((int)0xFFC06700));
        _out.WriteLine($"0xFFC06700 -> A={color.A} R={color.R} G={color.G} B={color.B}");

        Assert.Equal(255, color.A);
        Assert.Equal(0x00, color.R);
        Assert.Equal(0x67, color.G);
        Assert.Equal(0xC0, color.B);
    }

    [Fact]
    public void AccentFallsBackToTheWindowsDefaultBlueWhenTheValueIsMissing()
    {
        var color = AppThemePolicy.AccentFromAbgr(null);
        _out.WriteLine($"<missing> -> A={color.A} R={color.R} G={color.G} B={color.B}");

        Assert.Equal(AppThemePolicy.DefaultAccent, color);
        Assert.Equal(0x00, color.R);
        Assert.Equal(0x67, color.G);
        Assert.Equal(0xC0, color.B);
    }
}
