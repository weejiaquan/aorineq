namespace AorinEQ.Core;

/// <summary>What can stop a skin from saving. <see cref="AirPlayUnloadable"/> and
/// <see cref="NoticeUnloadable"/> are art the skin HAS on disk that the loader could not load:
/// saving would delete it, so the user must remove (or re-add) the strip, or turn the notice off
/// (or give it new art), first.</summary>
public enum SkinIssueKind
{
    MissingArt, ArtUnreadable, FramesDoNotDivide, SizeMismatch, FillRangeInverted, InvalidName,
    AirPlayUnloadable, NoticeUnloadable,
}

/// <summary>One problem with a skin being designed, on the layer it belongs to, with the values
/// its message needs. Every issue blocks Save: each one is a skin, strip or notice that would not
/// load as it is drawn.
///
/// The message is a string-table key composed from the kind; <c>LocKeyUsageTests</c> enumerates
/// the kinds against the table, because a composed key is invisible to its literal scan.</summary>
public sealed record SkinIssue(SkinLayer Layer, SkinIssueKind Kind, IReadOnlyList<string> Args)
{
    public string MessageKey => MessageKeyFor(Kind);

    public string Message => Loc.T(MessageKey, [.. Args]);

    public static string MessageKeyFor(SkinIssueKind kind) => kind switch
    {
        SkinIssueKind.MissingArt => "designer.issue.missing-art",
        SkinIssueKind.ArtUnreadable => "designer.issue.art-unreadable",
        SkinIssueKind.FramesDoNotDivide => "designer.issue.frames-do-not-divide",
        SkinIssueKind.SizeMismatch => "designer.issue.size-mismatch",
        SkinIssueKind.FillRangeInverted => "designer.issue.fill-range-inverted",
        SkinIssueKind.InvalidName => "designer.issue.invalid-name",
        SkinIssueKind.AirPlayUnloadable => "designer.issue.airplay-unloadable",
        SkinIssueKind.NoticeUnloadable => "designer.issue.notice-unloadable",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    // A record compares a list member by REFERENCE, so two identical issues would never be equal
    // and every "is this issue present" check would be a lie - the trap SkinMeta's tags avoid.
    public bool Equals(SkinIssue? other) =>
        other is not null && Layer == other.Layer && Kind == other.Kind
        && Args.SequenceEqual(other.Args, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Layer, Kind, Args.Count);

    public override string ToString() => $"{Layer} {Kind} [{string.Join(", ", Args)}]";
}
