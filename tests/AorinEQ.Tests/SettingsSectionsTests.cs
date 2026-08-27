using AorinEQ.Core;

namespace AorinEQ.Tests;

/// <summary>The Settings window's sidebar sections. The names are a contract in three places that
/// must agree — the NavigationView items, the content pages they select, and the
/// <c>aorineq://open?page=</c> links that deep-link into one — so the list and the routing live
/// here rather than as literals scattered across the XAML and the app.</summary>
public class SettingsSectionsTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public SettingsSectionsTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    [Fact]
    public void TheSidebarCarriesExactlyTheDesignedSectionsInOrder()
    {
        _out.WriteLine("sections: " + string.Join(", ", SettingsSections.All));

        Assert.Equal(
            new[]
            {
                SettingsSections.Discover,
                SettingsSections.Volume, SettingsSections.Osd, SettingsSections.Skins,
                SettingsSections.Equalizer, SettingsSections.AirPlay, SettingsSections.Hud,
                SettingsSections.Updates, SettingsSections.About,
            },
            SettingsSections.All);
    }

    [Fact]
    public void EverySectionNameIsDistinct()
    {
        Assert.Equal(SettingsSections.All.Count, SettingsSections.All.Distinct().Count());
    }

    /// <summary>Only <c>page=skins</c> and <c>page=settings</c> reach this window — <c>eq</c> and
    /// <c>designer</c> open windows of their own and never route here.</summary>
    [Theory]
    [InlineData(ProtocolPages.Skins, SettingsSections.Skins)]
    [InlineData(ProtocolPages.Settings, SettingsSections.Discover)] // bare "settings" lands on the first section
    public void ProtocolPagesRouteToTheirSection(string page, string expected)
    {
        _out.WriteLine($"page={page} -> section={expected}");
        Assert.Equal(expected, SettingsSections.ForProtocolPage(page));
    }

    /// <summary>An unrecognised page must still open Settings somewhere sane rather than throwing
    /// or leaving the window blank — the app's routing already treats unknown pages as "just open
    /// Settings", and the section picker has to agree.
    ///
    /// That landing place became Discover in 3.7.0, when Discover took first position in the
    /// sidebar. It is a deliberate consequence rather than a side effect: someone who followed a
    /// link that named a page this build does not have is exactly the person best served by the
    /// page that says what the app can do.</summary>
    [Theory]
    [InlineData("widgets")]
    [InlineData("")]
    [InlineData("SKINS")] // the link parser lower-cases, so anything else is genuinely unknown
    public void UnknownProtocolPagesFallBackToTheFirstSection(string page)
    {
        _out.WriteLine($"page='{page}' -> section={SettingsSections.ForProtocolPage(page)}");
        Assert.Equal(SettingsSections.Discover, SettingsSections.ForProtocolPage(page));
    }

    /// <summary>Every section the sidebar shows must be a valid navigation target, or a deep link
    /// (or a restored selection) can select a page that does not exist.</summary>
    [Fact]
    public void EverySectionIsRecognised()
    {
        foreach (var section in SettingsSections.All)
        {
            _out.WriteLine($"IsSection({section}) = {SettingsSections.IsSection(section)}");
            Assert.True(SettingsSections.IsSection(section));
        }
        Assert.False(SettingsSections.IsSection("widgets"));
        Assert.False(SettingsSections.IsSection(""));
    }

    /// <summary>Discover is the landing page exactly once.
    ///
    /// Both directions matter. Never landing there means a new user has to notice a sidebar item
    /// to learn what the app does, which is the problem this release exists to fix; always landing
    /// there means a returning user is shown an explainer every time they open Settings to change
    /// one thing.</summary>
    [Fact]
    public void DiscoverIsTheLandingSectionOnlyUntilItHasBeenSeen()
    {
        Assert.Equal(SettingsSections.Discover, SettingsSections.LandingSection(hasSeenDiscover: false));
        Assert.Equal(SettingsSections.Volume, SettingsSections.LandingSection(hasSeenDiscover: true));
    }

    /// <summary>Discover being FIRST is what makes it the fallback for an unknown deep link, so
    /// the two facts are pinned together rather than one silently drifting.</summary>
    [Fact]
    public void DiscoverIsFirstInTheSidebar() =>
        Assert.Equal(SettingsSections.Discover, SettingsSections.All[0]);
}
