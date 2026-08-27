namespace AorinEQ.Core;

/// <summary>One card on the Discover page.</summary>
/// <param name="Key">Stable identity, and the prefix of its title and pitch in the string table.</param>
/// <param name="Glyph">A WPF-UI SymbolRegular name.</param>
/// <param name="PageTag">The settings section its button navigates to.</param>
/// <param name="DocsAnchor">A "#section" in docs/reference.md for the "Learn more" link.</param>
public sealed record Feature(string Key, string Glyph, string PageTag, string? DocsAnchor = null)
{
    public string Title => Loc.T($"feature.{Key}.title");

    /// <summary>The sentence that stops someone buying a second application to do a thing AorinEQ
    /// already does. Not a description of the settings page - a reason to care.</summary>
    public string Pitch => Loc.T($"feature.{Key}.pitch");
}

/// <summary>Whether a feature is currently doing anything, and a word about how.
///
/// Lives here rather than on <see cref="Feature"/> because it reads live app state - the connected
/// receiver's name, the active skin - which Core has no access to. The window supplies it.</summary>
public sealed record FeatureState(bool IsOn, string Detail);

/// <summary>The six things a new user should be told AorinEQ can do.
///
/// Not nine. Discover is the page itself, and Updates and About are not features anyone needs
/// talking into. The point of this list is the person who owns a HomePod, has no idea the app has
/// an AirPlay sender, and buys a second application to do it.</summary>
public static class FeatureCatalogue
{
    public static readonly IReadOnlyList<Feature> Features =
    [
        new("volume", "Speaker224", SettingsSections.Volume, "#volume-modes"),
        new("osd", "Video24", SettingsSections.Osd, "#the-osd"),
        new("skins", "PaintBrush24", SettingsSections.Skins, "#skins"),
        new("equalizer", "Options24", SettingsSections.Equalizer, "#equalizer"),
        new("airplay", "Wifi124", SettingsSections.AirPlay, "#airplay"),
        new("hud", "Board24", SettingsSections.Hud, "#hud-widgets"),
    ];
}
