namespace AorinEQ.Core;

/// <summary>Where a help topic lives.
///
/// The settings sections plus the windows that are not settings pages. Spelled once, the way
/// <see cref="SettingsSections"/> and <see cref="VolumeModes"/> already are, so a topic's claimed
/// home can be validated rather than trusted - and so the per-window help panel can ask for
/// "everything on this surface" without anybody maintaining a second list by hand.</summary>
public static class HelpSurfaces
{
    public const string EqEditor = "eq-editor";
    public const string SkinDesigner = "skin-designer";
    public const string Onboarding = "onboarding";
    public const string Dialogs = "dialogs";
    public const string Tray = "tray";

    /// <summary>Every surface. The settings sections come first and in sidebar order, so a help
    /// panel or a search result list reads in the same order as the window.</summary>
    public static readonly IReadOnlyList<string> All =
        [.. SettingsSections.All, EqEditor, SkinDesigner, Onboarding, Dialogs, Tray];

    public static bool IsSurface(string surface) => All.Contains(surface);

    /// <summary>The name shown on a search result and at the top of a help panel - "Equalizer
    /// editor", not "eq-editor".</summary>
    public static string DisplayName(string surface) =>
        IsSurface(surface) ? Loc.T($"surface.{surface}.name") : surface;
}
