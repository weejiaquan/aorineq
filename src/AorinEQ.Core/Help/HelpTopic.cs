namespace AorinEQ.Core;

/// <summary>One thing the user can be told about.
///
/// Structure only - the words live in the string tables, and the topic just says which keys.
///
/// Title and Summary deliberately point at the control's OWN keys, the ones the card already
/// renders, rather than at copies under help.*. Two reasons, and the second is the important one:
/// it saves duplicating ~240 English strings (and ~960 translations of them), and it makes a card
/// and its help physically incapable of disagreeing. A reworded card title is a reworded help
/// title, because they are the same string.
///
/// Only <see cref="Body"/> is new writing.</summary>
/// <param name="Key">Stable identity, used from XAML as help:Help.Topic="settings.system-mode".
/// Never derived from the English text: rewording must not change the key, because translators
/// work against keys.</param>
/// <param name="Surface">Which window or settings page the control lives on.</param>
/// <param name="TitleKey">The string key of the control's visible label.</param>
/// <param name="SummaryKey">The key of the one-line description already beside it, where there is
/// one. Null for a control whose label stands alone, such as a toolbar button.</param>
/// <param name="DocsAnchor">A "#section" in docs/reference.md, or null. HelpDocsAnchorTests fails
/// the build if a non-null anchor does not resolve to a real heading.</param>
public sealed record HelpTopic(
    string Key,
    string Surface,
    string TitleKey,
    string? SummaryKey = null,
    string? DocsAnchor = null)
{
    public string Title => Loc.T(TitleKey);

    /// <summary>Empty when the control has no one-liner of its own - a toolbar button, say.</summary>
    public string Summary => SummaryKey is null ? "" : Loc.T(SummaryKey);

    /// <summary>The paragraph behind the (?): what it does, when you would want it, and what
    /// happens if you get it wrong.
    ///
    /// Where a <see cref="Summary"/> exists this must say more than it does - HelpCatalogueTests
    /// fails, in every language, on a body that merely restates its summary. That is the failure
    /// mode this whole feature has: a hundred paragraphs that each cost a screenful and tell the
    /// reader nothing they had not already read on the card.</summary>
    public string Body => Loc.T($"help.{Key}.body");
}
