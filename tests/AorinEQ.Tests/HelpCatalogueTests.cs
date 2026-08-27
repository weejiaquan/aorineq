using AorinEQ.Core;

namespace AorinEQ.Tests;

/// <summary>The catalogue's shape, and its words in every language.
///
/// The body-versus-summary test is the one that matters. A body that restates its summary is how
/// this feature rots: a hundred paragraphs that each cost the reader a screenful and tell them
/// nothing they had not already read on the card. It is easy to write those by accident when
/// working through a long list, and impossible to notice by reading the diff.
///
/// It runs per language, because a translation can collapse a distinction the English keeps.</summary>
[Collection("Loc")]
public class HelpCatalogueTests
{
    public static TheoryData<string> AllLanguages()
    {
        var data = new TheoryData<string>();
        foreach (var language in Languages.All) data.Add(language);
        return data;
    }

    [Fact]
    public void Keys_are_unique()
    {
        var duplicates = HelpCatalogue.Topics
            .GroupBy(t => t.Key, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(duplicates.Count == 0, $"Duplicate topic keys: {string.Join(", ", duplicates)}");
    }

    [Fact]
    public void Every_topic_claims_a_real_surface()
    {
        foreach (var topic in HelpCatalogue.Topics)
            Assert.True(HelpSurfaces.IsSurface(topic.Surface),
                $"Topic '{topic.Key}' claims surface '{topic.Surface}', which is not a HelpSurfaces value.");
    }

    [Fact]
    public void Find_returns_null_for_an_unknown_key() =>
        Assert.Null(HelpCatalogue.Find("no.such.topic"));

    [Fact]
    public void Find_returns_the_topic_for_every_key_in_the_catalogue()
    {
        foreach (var topic in HelpCatalogue.Topics)
            Assert.Same(topic, HelpCatalogue.Find(topic.Key));
    }

    [Fact]
    public void ForSurface_returns_only_that_surface_and_loses_nothing()
    {
        var regrouped = HelpSurfaces.All.SelectMany(HelpCatalogue.ForSurface).ToList();

        foreach (var surface in HelpSurfaces.All)
            Assert.All(HelpCatalogue.ForSurface(surface), t => Assert.Equal(surface, t.Surface));

        Assert.Equal(HelpCatalogue.Topics.Count, regrouped.Count);
    }

    /// <summary>A topic's title and summary point at the CONTROL's own string keys, not at copies.
    /// If one of those keys is wrong the help shows a raw key where the label should be, which is
    /// the most visible possible failure and the easiest to introduce by hand-editing the list.</summary>
    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void Every_topic_resolves_a_real_title_and_summary(string language)
    {
        using var _ = new LocScope(language);

        foreach (var topic in HelpCatalogue.Topics)
        {
            Assert.False(string.IsNullOrWhiteSpace(topic.Title), $"{topic.Key} has no title in {language}");
            Assert.False(topic.Title.StartsWith("settings.", StringComparison.Ordinal),
                $"{topic.Key}: TitleKey '{topic.TitleKey}' is not in the table - it rendered as the raw key.");

            if (topic.SummaryKey is not null)
            {
                Assert.False(string.IsNullOrWhiteSpace(topic.Summary), $"{topic.Key} has no summary in {language}");
                Assert.False(topic.Summary.StartsWith("settings.", StringComparison.Ordinal),
                    $"{topic.Key}: SummaryKey '{topic.SummaryKey}' is not in the table.");
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void Every_topic_has_a_body(string language)
    {
        using var _ = new LocScope(language);

        foreach (var topic in HelpCatalogue.Topics)
        {
            Assert.False(string.IsNullOrWhiteSpace(topic.Body), $"{topic.Key} has no body in {language}");
            Assert.False(topic.Body.StartsWith("help.", StringComparison.Ordinal),
                $"{topic.Key}: no help.{topic.Key}.body in the {language} table - it rendered as the raw key.");
        }
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void Every_body_says_more_than_its_summary(string language)
    {
        using var _ = new LocScope(language);

        foreach (var topic in HelpCatalogue.Topics)
        {
            if (topic.SummaryKey is null) continue;

            var summary = Flatten(topic.Summary);
            var body = Flatten(topic.Body);

            Assert.False(summary == body,
                $"{topic.Key} ({language}): the body is a restatement of the summary. The body is " +
                "for what the summary has no room for - when you would want this, and what happens " +
                "if you get it wrong.");

            Assert.True(body.Length > summary.Length,
                $"{topic.Key} ({language}): body ({body.Length} chars) is not longer than its " +
                $"summary ({summary.Length}).");
        }

        static string Flatten(string s) =>
            string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    }

    /// <summary>Every surface named in HelpSurfaces should eventually carry topics. The ones that
    /// do not yet are listed here BY NAME rather than the assertion being dropped, so finishing
    /// one is a visible one-line deletion instead of a thing nobody remembers is outstanding.</summary>
    [Fact]
    public void Every_surface_has_topics_except_the_ones_still_to_be_written()
    {
        string[] notYetCovered =
        [
            SettingsSections.Discover, SettingsSections.About,
            HelpSurfaces.EqEditor, HelpSurfaces.SkinDesigner,
            HelpSurfaces.Onboarding, HelpSurfaces.Dialogs, HelpSurfaces.Tray,
        ];

        foreach (var surface in HelpSurfaces.All.Except(notYetCovered))
            Assert.True(HelpCatalogue.ForSurface(surface).Count > 0, $"Surface '{surface}' has no topics.");
    }
}
