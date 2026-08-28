using System.Text.RegularExpressions;
using AorinEQ.Core;

namespace AorinEQ.Tests;

/// <summary>A help topic's Surface must name the page its control is actually on.
///
/// WHY THIS EXISTS. Surface is what search uses to decide which page to open before it scrolls to
/// a row. Nothing else checks it against reality: HelpCatalogueTests proves a Surface is a REAL
/// surface, and HelpXamlCoverageTests proves every card HAS a topic, but a card can be moved from
/// one page to another while its topic still claims the old one, and both of those stay green. The
/// symptom is a search result that opens the wrong page and scrolls to nothing - silent, and only
/// reproducible by searching for the one topic that moved.
///
/// This became a real risk the moment cards started moving between pages, which is what adding the
/// Settings section did to seven of them.</summary>
public class HelpSurfaceMatchesPageTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public HelpSurfaceMatchesPageTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private const string WindowXaml = "src/AorinEQ/UI/SettingsWindow.xaml";

    /// <summary>Every topic attached to a control in the Settings window, paired with the section
    /// body it is declared inside.
    ///
    /// Read positionally: the sections are declared in document order, so the section a topic
    /// belongs to is simply the last one opened above it. That is exactly how a reader of the file
    /// works out the same thing, and it needs no marker in the XAML that could go stale.</summary>
    private static IReadOnlyList<(string Topic, string Section)> TopicsByDeclaredSection()
    {
        var xaml = RepoFiles.ReadText(WindowXaml);

        var pattern = new Regex(
            @"<ScrollViewer x:Name=""Section(?<section>\w+)""|help:Help\.Topic=""(?<topic>[^""]+)""");

        var found = new List<(string, string)>();
        string? section = null;

        foreach (Match match in pattern.Matches(xaml))
        {
            if (match.Groups["section"].Success)
            {
                section = match.Groups["section"].Value.ToLowerInvariant();
                continue;
            }

            if (section is null)
            {
                throw new InvalidOperationException(
                    $"help topic '{match.Groups["topic"].Value}' is declared before any " +
                    "<ScrollViewer x:Name=\"Section...\">, so this test cannot tell which page it " +
                    "is on.");
            }

            found.Add((match.Groups["topic"].Value, section));
        }

        return found;
    }

    [Fact]
    public void Every_settings_topic_claims_the_page_its_control_is_on()
    {
        var wrong = new List<string>();

        foreach (var (key, section) in TopicsByDeclaredSection())
        {
            var topic = HelpCatalogue.Find(key)
                ?? throw new InvalidOperationException(
                    $"'{key}' is attached in the XAML but is not in HelpCatalogue.");

            _out.WriteLine($"{key,-34} xaml={section,-10} catalogue={topic.Surface}");

            if (!string.Equals(topic.Surface, section, StringComparison.Ordinal))
                wrong.Add($"{key}: on the '{section}' page but its topic says '{topic.Surface}'");
        }

        Assert.True(wrong.Count == 0,
            "A search result for these topics would open the wrong page:" + Environment.NewLine +
            "  " + string.Join(Environment.NewLine + "  ", wrong));
    }

    /// <summary>The section names the XAML uses have to BE sections, or the comparison above would
    /// pass by comparing two things that are equally wrong.</summary>
    [Fact]
    public void Every_section_the_xaml_declares_is_a_real_section()
    {
        foreach (var section in TopicsByDeclaredSection().Select(t => t.Section).Distinct())
        {
            _out.WriteLine($"IsSection({section}) = {SettingsSections.IsSection(section)}");
            Assert.True(SettingsSections.IsSection(section),
                $"The XAML declares a section body named '{section}' that SettingsSections does not know.");
        }
    }
}
