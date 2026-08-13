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
                SettingsSections.Volume, SettingsSections.Osd, SettingsSections.Skins,
                SettingsSections.Equalizer, SettingsSections.Hud, SettingsSections.Updates,
                SettingsSections.About,
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
    [InlineData(ProtocolPages.Settings, SettingsSections.Volume)] // bare "settings" lands on the first section
    public void ProtocolPagesRouteToTheirSection(string page, string expected)
    {
        _out.WriteLine($"page={page} -> section={expected}");
        Assert.Equal(expected, SettingsSections.ForProtocolPage(page));
    }

    /// <summary>An unrecognised page must still open Settings somewhere sane rather than throwing
    /// or leaving the window blank — the app's routing already treats unknown pages as "just open
    /// Settings", and the section picker has to agree.</summary>
    [Theory]
    [InlineData("widgets")]
    [InlineData("")]
    [InlineData("SKINS")] // the link parser lower-cases, so anything else is genuinely unknown
    public void UnknownProtocolPagesFallBackToTheFirstSection(string page)
    {
        _out.WriteLine($"page='{page}' -> section={SettingsSections.ForProtocolPage(page)}");
        Assert.Equal(SettingsSections.Volume, SettingsSections.ForProtocolPage(page));
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
}
