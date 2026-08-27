using System.Text.RegularExpressions;
using AorinEQ.Core;

namespace AorinEQ.Tests;

/// <summary>Every settings card either explains itself or is on a list saying why not.
///
/// This is the test that makes "the app explains itself" true a year from now rather than only in
/// the release that shipped it. Without it, the thirty-sixth card added next spring gets no help,
/// nothing fails, and the feature quietly becomes "most settings have help".
///
/// It is scoped to CardControl deliberately. Every settings row is a card, and a card is the one
/// shape with room for a paragraph. Extending it to all 226 interactive controls in the app would
/// mostly generate demands for paragraphs about X and Y coordinate boxes - and the honest response
/// to that demand is filler, which costs the reader more than silence does.
///
/// The allowlist is the other half of being honest: a card that genuinely needs no paragraph says
/// so, with a reason, in one place a reviewer can read.</summary>
public class HelpXamlCoverageTests
{
    /// <summary>An opening &lt;ui:CardControl&gt; tag - not &lt;ui:CardControl.Header&gt;, which is
    /// a property element and matches the naive pattern.</summary>
    private static readonly Regex CardTag =
        new(@"<ui:CardControl(?![.\w])(?<attrs>(?:[^<>""]|""[^""]*"")*?)>", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex TopicAttr =
        new(@"help:Help\.Topic\s*=\s*""(?<key>[^""]+)""", RegexOptions.Compiled);

    private static readonly Regex TitleKeyAttr =
        new(@"Text=""\{loc:T (?<key>[\w.\-]+)\}""", RegexOptions.Compiled);

    private static readonly Regex XamlComment = new("<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>Cards that carry no help topic, and why. Keyed by the string key of the card's own
    /// title, which is the only stable name a card has.
    ///
    /// "It is obvious" is not a reason. If it cannot be justified in a sentence, write the topic.</summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        ["settings.version.subtitle"] =
            "The About page's version plate. An identity, not a setting - there is nothing to decide.",
        ["settings.language.title"] =
            "The language picker. Its own card explains it, and a paragraph about it would have to " +
            "be written in a language the reader has just told us they cannot read.",
        ["settings.text.installed"] =
            "The Equalizer APO health card. Its header is a four-row fact TABLE, not the " +
            "title-and-subtitle shape the (?) renders into - the decorator would throw on it. The " +
            "facts are already self-describing (Installed / Switched on for the device you're " +
            "using / Last checked) and the section heading above names what they are about.",
    };

    [Fact]
    public void Every_settings_card_is_helped_or_exempt()
    {
        var xaml = XamlComment.Replace(RepoFiles.ReadText("src/AorinEQ/UI/SettingsWindow.xaml"), "");

        var unhelped = new List<string>();

        foreach (Match card in CardTag.Matches(xaml))
        {
            if (TopicAttr.IsMatch(card.Groups["attrs"].Value)) continue;

            // Identify the card by its title key, which is the first {loc:T} after the open tag.
            var after = xaml[card.Index..Math.Min(xaml.Length, card.Index + 1200)];
            var title = TitleKeyAttr.Match(after);
            var name = title.Success ? title.Groups["key"].Value : $"(card at offset {card.Index})";

            if (Exempt.ContainsKey(name)) continue;

            unhelped.Add(name);
        }

        Assert.True(unhelped.Count == 0,
            $"Settings cards with neither help:Help.Topic nor an exemption:{Environment.NewLine}  " +
            string.Join($"{Environment.NewLine}  ", unhelped) + Environment.NewLine +
            "Add a topic to HelpCatalogue with a body in strings.en.json, or add the card to " +
            "HelpXamlCoverageTests.Exempt with a real reason.");
    }

    [Fact]
    public void Every_topic_referenced_from_xaml_exists_in_the_catalogue()
    {
        foreach (var path in RepoFiles.WindowXaml)
            foreach (Match m in TopicAttr.Matches(RepoFiles.ReadText(path)))
                Assert.True(HelpCatalogue.Find(m.Groups["key"].Value) is not null,
                    $"{path}: help:Help.Topic=\"{m.Groups["key"].Value}\" is not in HelpCatalogue. " +
                    "The decorator throws on this at window-open time.");
    }

    /// <summary>One control per topic. Two controls sharing a topic means one of them is showing
    /// help written about the other, which reads as a bug and is impossible to spot in review.</summary>
    [Fact]
    public void No_topic_is_attached_to_two_controls()
    {
        var referenced = RepoFiles.WindowXaml
            .SelectMany(p => TopicAttr.Matches(RepoFiles.ReadText(p)))
            .Select(m => m.Groups["key"].Value)
            .ToList();

        var twice = referenced.GroupBy(k => k, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(twice.Count == 0, $"Attached to more than one control: {string.Join(", ", twice)}");
    }

    /// <summary>A topic nobody can reach is a paragraph that was written, translated four times,
    /// and shown to no one.</summary>
    [Fact]
    public void Every_catalogue_topic_is_reachable()
    {
        var referenced = RepoFiles.WindowXaml
            .SelectMany(p => TopicAttr.Matches(RepoFiles.ReadText(p)))
            .Select(m => m.Groups["key"].Value)
            .ToHashSet(StringComparer.Ordinal);

        // Attached in code rather than XAML: the tray's "Add widget" submenu is WinForms and has no
        // attached properties, so HelpDecorator.Apply carries these.
        foreach (var type in HudWidgetTypes.All) referenced.Add($"hud.widget.{type}");

        var unreachable = HelpCatalogue.Topics
            .Select(t => t.Key)
            .Where(k => !referenced.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(unreachable.Count == 0,
            $"In the catalogue but attached to nothing:{Environment.NewLine}  " +
            string.Join($"{Environment.NewLine}  ", unreachable));
    }
}
