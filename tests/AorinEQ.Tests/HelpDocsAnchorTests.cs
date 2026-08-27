using System.Text.RegularExpressions;
using AorinEQ.Core;

namespace AorinEQ.Tests;

/// <summary>Every "Learn more" link lands on a heading that exists.
///
/// The app links into docs/reference.md by anchor. Rename a heading six months from now and a
/// hundred of those links silently become a scroll to the top of a long page - nothing fails,
/// because a Markdown anchor that does not resolve is not an error anywhere in the toolchain. The
/// user just clicks "Learn more" and lands nowhere useful.</summary>
public class HelpDocsAnchorTests
{
    private static string Reference => RepoFiles.ReadText("docs/reference.md");

    /// <summary>GitHub's heading-to-anchor rule: lowercase, drop anything that is not a letter,
    /// digit, space, hyphen or underscore, then spaces become hyphens.</summary>
    private static string Slug(string heading)
    {
        var cleaned = new string(heading.Trim().ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_')
            .ToArray());

        return "#" + cleaned.Trim().Replace(' ', '-');
    }

    private static HashSet<string> Anchors() =>
        Regex.Matches(Reference, @"^#{2,3}\s+(?<text>.+)$", RegexOptions.Multiline)
             .Select(m => Slug(m.Groups["text"].Value))
             .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Every_help_topic_anchor_resolves()
    {
        var anchors = Anchors();

        var broken = HelpCatalogue.Topics
            .Where(t => t.DocsAnchor is not null && !anchors.Contains(t.DocsAnchor))
            .Select(t => $"{t.Key} -> {t.DocsAnchor}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.True(broken.Count == 0,
            $"Help topics whose \"Learn more\" points at a heading reference.md does not have:" +
            $"{Environment.NewLine}  " + string.Join($"{Environment.NewLine}  ", broken));
    }

    [Fact]
    public void Every_discover_card_anchor_resolves()
    {
        var anchors = Anchors();

        var broken = FeatureCatalogue.Features
            .Where(f => f.DocsAnchor is not null && !anchors.Contains(f.DocsAnchor))
            .Select(f => $"{f.Key} -> {f.DocsAnchor}")
            .ToList();

        Assert.True(broken.Count == 0,
            $"Discover cards linking nowhere:{Environment.NewLine}  " +
            string.Join($"{Environment.NewLine}  ", broken));
    }

    /// <summary>The manual opens with a hand-written contents list. A section missing from it is a
    /// section most readers never find.</summary>
    [Fact]
    public void The_table_of_contents_lists_every_section()
    {
        var sections = Regex.Matches(Reference, @"^##\s+(?<text>.+)$", RegexOptions.Multiline)
            .Select(m => Slug(m.Groups["text"].Value))
            .ToList();

        var listed = Regex.Matches(Reference, @"^-\s+\[[^\]]+\]\((?<anchor>#[^)]+)\)$", RegexOptions.Multiline)
            .Select(m => m.Groups["anchor"].Value)
            .ToHashSet(StringComparer.Ordinal);

        var unlisted = sections.Where(s => !listed.Contains(s)).ToList();

        Assert.True(unlisted.Count == 0,
            $"Sections missing from the table of contents:{Environment.NewLine}  " +
            string.Join($"{Environment.NewLine}  ", unlisted));
    }

    /// <summary>AirPlay shipped in this release with no mention anywhere in the manual. This is the
    /// check that it stays documented - a feature the docs do not know about is a feature users
    /// discover by accident, which is the whole complaint this release answers.</summary>
    [Fact]
    public void The_manual_documents_airplay()
    {
        Assert.Contains("## AirPlay", Reference, StringComparison.Ordinal);

        foreach (var mustMention in new[] { "HomePod", "dither", "virtual", "multi-room" })
            Assert.True(Reference.Contains(mustMention, StringComparison.OrdinalIgnoreCase),
                $"The AirPlay section never mentions '{mustMention}'.");
    }
}
