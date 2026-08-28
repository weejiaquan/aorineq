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
                SettingsSections.General,
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

    // ---------------------------------------------------------------------------------------
    // The three places the section names must agree. The tests above pin the Core list; these
    // pin the two lists in the WINDOW against it, by reading the repository the way RepoFiles
    // already does elsewhere.
    //
    // This is not belt-and-braces. Every fact above held while NavDiscover was absent from
    // NavItems() - the one list that wires each sidebar item's Click - so the Discover item was
    // dead to the mouse AND to the keyboard, and nothing failed. A list of controls typed out by
    // hand is exactly the thing that goes stale when a section is added, and the symptom is
    // silent.
    // ---------------------------------------------------------------------------------------

    private const string WindowXaml = "src/AorinEQ/UI/SettingsWindow.xaml";
    private const string WindowCode = "src/AorinEQ/UI/SettingsWindow.xaml.cs";

    /// <summary>Sidebar item field name to the section it targets, read from the XAML in
    /// declaration order.</summary>
    private static IReadOnlyList<(string Field, string Tag)> SidebarItems()
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(
            RepoFiles.ReadText(WindowXaml),
            @"<ui:NavigationViewItem\s+x:Name=""(\w+)""[^>]*?TargetPageTag=""([^""]+)""");

        return matches.Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();
    }

    /// <summary>The sidebar shows every section, in the designed order, and nothing else. A
    /// section in <see cref="SettingsSections.All"/> with no item is unreachable; an item naming a
    /// tag that is not a section navigates nowhere.</summary>
    [Fact]
    public void TheXamlSidebarDeclaresEverySectionInOrder()
    {
        var items = SidebarItems();
        foreach (var (field, tag) in items) _out.WriteLine($"{field} -> {tag}");

        Assert.Equal(SettingsSections.All, items.Select(i => i.Tag).ToList());
    }

    /// <summary>Every sidebar item is in <c>NavItems()</c>.
    ///
    /// That list is the ONLY place an item's Click is wired (the NavigationView's own
    /// SelectionChanged never fires for a TargetPageTag-only item, which is why this window drives
    /// itself), and the only place <c>NavItemFor</c> looks when deciding which item to light up.
    /// An item missing from it is a sidebar entry that does nothing when clicked and never shows
    /// as selected - which is what shipped for Discover.</summary>
    [Fact]
    public void NavItemsCoversEverySidebarItem()
    {
        var body = System.Text.RegularExpressions.Regex.Match(
            RepoFiles.ReadText(WindowCode),
            @"NavItems\(\) =>\s*new\[\]\s*\{([^}]*)\}");

        Assert.True(body.Success, $"Could not find the NavItems() array in {WindowCode}.");

        var wired = body.Groups[1].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        _out.WriteLine("NavItems(): " + string.Join(", ", wired));

        Assert.Equal(SidebarItems().Select(i => i.Field).ToList(), wired);
    }

    /// <summary>Every section has a body to show. The bodies are detached from their holder and
    /// handed to the frame one at a time, keyed by these names; a section with no entry is dropped
    /// by Navigate's <c>ContainsKey</c> guard and simply does nothing.</summary>
    [Fact]
    public void EverySectionHasAContentBody()
    {
        var mapped = System.Text.RegularExpressions.Regex
            .Matches(RepoFiles.ReadText(WindowCode), @"\(SettingsSections\.(\w+), Section(\w+)\)")
            .Select(m => (Section: m.Groups[1].Value, Body: m.Groups[2].Value))
            .ToList();

        foreach (var (section, bodyName) in mapped) _out.WriteLine($"{section} -> Section{bodyName}");

        // Named after their section, so the pairing is checkable rather than merely present.
        Assert.All(mapped, m => Assert.Equal(m.Section, m.Body));
        Assert.Equal(SettingsSections.All.Count, mapped.Count);
    }
}

