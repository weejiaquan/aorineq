using AorinEQ.Core;

namespace AorinEQ.Tests;

/// <summary>Search is the answer to "I know AorinEQ does this - where is it?".
///
/// The English-while-Japanese case is not hypothetical, and it is the reason search reads two
/// tables instead of one. Almost every page written about audio is in English, so someone running
/// the app in Japanese who has just read about a "preamp" types preamp. If search only looked at
/// the active language it would return nothing, and returning nothing does not read as "wrong
/// language" - it reads as "this app cannot do that".</summary>
[Collection("Loc")]
public class HelpSearchTests
{
    [Fact]
    public void Homepod_finds_the_airplay_receiver_row()
    {
        using var _ = new LocScope(Languages.En);

        // "HomePod" appears only in the BODY of the receiver topic, which is the whole point:
        // the word a user thinks in is not always a word on the screen.
        Assert.Contains(HelpCatalogue.Search("homepod"), t => t.Key == "settings.air-play-status");
    }

    [Fact]
    public void A_title_match_outranks_a_body_match()
    {
        using var _ = new LocScope(Languages.En);

        var results = HelpCatalogue.Search("skin");
        Assert.NotEmpty(results);
        Assert.Contains("skin", results[0].Title, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SKIN")]
    [InlineData("  skin  ")]
    [InlineData("Skin")]
    public void Case_and_surrounding_space_do_not_matter(string query)
    {
        using var _ = new LocScope(Languages.En);
        Assert.NotEmpty(HelpCatalogue.Search(query));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void An_empty_query_returns_nothing_rather_than_everything(string? query)
    {
        using var _ = new LocScope(Languages.En);
        Assert.Empty(HelpCatalogue.Search(query!));
    }

    [Fact]
    public void A_query_matching_nothing_returns_nothing()
    {
        using var _ = new LocScope(Languages.En);
        Assert.Empty(HelpCatalogue.Search("xyzzy-no-such-thing"));
    }

    [Fact]
    public void Results_are_ordered_best_first_and_never_repeat_a_topic()
    {
        using var _ = new LocScope(Languages.En);

        var results = HelpCatalogue.Search("volume");
        Assert.NotEmpty(results);
        Assert.Equal(results.Select(t => t.Key).Distinct().Count(), results.Count);
    }

    /// <summary>The case this design exists for. Runs against whatever the Japanese table holds -
    /// before the translations land it falls through to English, which is exactly the behaviour
    /// being asserted.</summary>
    [Fact]
    public void An_english_term_still_matches_while_the_ui_is_japanese()
    {
        using var _ = new LocScope(Languages.Ja);
        Assert.NotEmpty(HelpCatalogue.Search("preamp"));
    }

    [Fact]
    public void Every_result_carries_a_surface_that_resolves_to_a_real_page()
    {
        using var _ = new LocScope(Languages.En);

        foreach (var topic in HelpCatalogue.Search("volume"))
            Assert.True(HelpSurfaces.IsSurface(topic.Surface),
                $"'{topic.Key}' would be labelled with surface '{topic.Surface}', which has no name.");
    }
}
