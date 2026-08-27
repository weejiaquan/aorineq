using AorinEQ.Core;
using Wpf.Ui.Controls;

namespace AorinEQ.Tests;

/// <summary>The Discover page's contents.
///
/// This list is the answer to the actual complaint behind this release: someone buys a HomePod,
/// never learns AorinEQ has an AirPlay sender, and pays for a second application. Every assertion
/// here is guarding some way that page could quietly stop doing its job - a card pointing at a
/// page that no longer exists, a glyph that does not render, a pitch that never got written.</summary>
[Collection("Loc")]
public class FeatureCatalogueTests
{
    public static TheoryData<string> AllLanguages()
    {
        var data = new TheoryData<string>();
        foreach (var language in Languages.All) data.Add(language);
        return data;
    }

    [Fact]
    public void The_six_features_are_the_ones_worth_discovering()
    {
        Assert.Equal(
            new[] { "volume", "osd", "skins", "equalizer", "airplay", "hud" },
            FeatureCatalogue.Features.Select(f => f.Key));
    }

    [Fact]
    public void Every_feature_points_at_a_real_page()
    {
        foreach (var feature in FeatureCatalogue.Features)
            Assert.True(SettingsSections.IsSection(feature.PageTag),
                $"Feature '{feature.Key}' navigates to '{feature.PageTag}', which is not a section.");
    }

    /// <summary>Discover, Updates and About get no card on purpose: the first IS this page, and the
    /// other two are not features anyone needs talking into. Pinned so a later edit that adds one
    /// is a deliberate change to this test rather than a quiet drift into a page that advertises
    /// its own About screen.</summary>
    [Fact]
    public void No_feature_advertises_discover_updates_or_about()
    {
        string[] notFeatures = [SettingsSections.Discover, SettingsSections.Updates, SettingsSections.About];

        foreach (var feature in FeatureCatalogue.Features)
            Assert.DoesNotContain(feature.PageTag, notFeatures);
    }

    [Fact]
    public void Feature_keys_are_unique()
    {
        var duplicates = FeatureCatalogue.Features
            .GroupBy(f => f.Key, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(duplicates.Count == 0, $"Duplicate feature keys: {string.Join(", ", duplicates)}");
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void Every_feature_has_a_title_and_a_pitch(string language)
    {
        using var _ = new LocScope(language);

        foreach (var feature in FeatureCatalogue.Features)
        {
            Assert.False(string.IsNullOrWhiteSpace(feature.Title), $"{feature.Key} has no title in {language}");
            Assert.False(feature.Title.StartsWith("feature.", StringComparison.Ordinal),
                $"{feature.Key}: no feature.{feature.Key}.title in the {language} table - it rendered as the raw key.");

            Assert.False(string.IsNullOrWhiteSpace(feature.Pitch), $"{feature.Key} has no pitch in {language}");
            Assert.False(feature.Pitch.StartsWith("feature.", StringComparison.Ordinal),
                $"{feature.Key}: no feature.{feature.Key}.pitch in the {language} table.");
        }
    }

    /// <summary>A pitch has a job the title cannot do: say why someone should care. One that is
    /// shorter than its own title is not doing it.</summary>
    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void Every_pitch_says_more_than_its_title(string language)
    {
        using var _ = new LocScope(language);

        foreach (var feature in FeatureCatalogue.Features)
            Assert.True(feature.Pitch.Length > feature.Title.Length,
                $"{feature.Key} ({language}): the pitch is no longer than the title.");
    }

    /// <summary>Core stores the glyph by NAME so it never references WPF-UI. That is a good trade,
    /// but it moves a compile error to runtime - a typo would render a fallback circle on a card
    /// and nobody would notice. This is where it is caught instead.
    ///
    /// This is the only reason the test project references WPF-UI at all (see the csproj comment).
    /// Checking the name against the real enum is worth one test-only package reference; matching
    /// it against a regex would pass for "Speaker999" and prove nothing.</summary>
    [Fact]
    public void Every_glyph_names_a_real_symbol()
    {
        var symbolType = typeof(SymbolRegular);

        foreach (var feature in FeatureCatalogue.Features)
            Assert.True(Enum.IsDefined(symbolType, Enum.Parse(symbolType, feature.Glyph, ignoreCase: false)),
                $"Feature '{feature.Key}' names glyph '{feature.Glyph}', which is not a SymbolRegular value.");
    }
}
