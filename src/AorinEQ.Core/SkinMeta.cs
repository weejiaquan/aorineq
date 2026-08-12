using System.Text;

namespace AorinEQ.Core;

/// <summary>The optional authorship/gallery metadata a skin can carry in skin.json: who made it,
/// what it's called (as opposed to what its folder is called), what it is, which version, how it's
/// tagged, and where it came from.
///
/// Every field is OPTIONAL and every one of them absent is exactly the pre-3.2 skin format — the
/// writer omits what is empty, so a skin without credits resaves byte-identically.
///
/// There is ONE door into a SkinMeta, <see cref="Create"/>, and it normalizes: values are trimmed,
/// capped by TEXT ELEMENT (so a cap can never slice a surrogate pair or a combining sequence in
/// half), stripped of control and bidi-override characters — these strings are displayed in the
/// skin picker and, by design, on a public gallery page, where a right-to-left override makes a
/// credit render as something other than what it says — and a <see cref="SourceUrl"/> that is not
/// credential-free https is dropped entirely rather than kept for something else to link.</summary>
public sealed record SkinMeta
{
    /// <summary>Caps, counted in text elements. Sized for a credit line, not for prose: the
    /// description is the only field with room for a sentence or two.</summary>
    public const int MaxTitleLength = 80;
    public const int MaxAuthorLength = 80;
    public const int MaxDescriptionLength = 500;
    public const int MaxVersionLength = 32;
    public const int MaxTagLength = 32;
    public const int MaxTags = 12;

    /// <summary>Cap on <see cref="SourceUrl"/>. A URL past it is DROPPED, never truncated — half
    /// a URL is a different destination, not a shorter one.</summary>
    public const int MaxSourceUrlLength = 512;

    /// <summary>Cap on <see cref="DisplayLabel"/>, so a long title plus a long author can't
    /// stretch a combo box row past the column it lives in.</summary>
    public const int MaxDisplayLabelLength = 60;

    /// <summary>Separator between the skin's name and its author in a credit line.</summary>
    private const string ByJoiner = " — by ";

    private SkinMeta() { }

    /// <summary>Display name, distinct from the folder name. Null = the folder name is the name.</summary>
    public string? Title { get; private init; }

    /// <summary>Who made it. Null = anonymous, exactly like every pre-3.2 skin.</summary>
    public string? Author { get; private init; }

    /// <summary>A sentence or two about the skin. May contain newlines (only \n; line endings are
    /// normalized on the way in).</summary>
    public string? Description { get; private init; }

    /// <summary>The AUTHOR's version string for their skin — free-form ("1.2", "2024-03"), not
    /// AorinEQ's version and never parsed as one.</summary>
    public string? Version { get; private init; }

    /// <summary>Gallery tags, de-duplicated case-insensitively, first spelling kept. Never null.</summary>
    public IReadOnlyList<string> Tags { get; private init; } = Array.Empty<string>();

    /// <summary>Where the skin came from, guaranteed absolute https without credentials — it is
    /// rendered as a link on the gallery, so anything else is not kept at all.</summary>
    public string? SourceUrl { get; private init; }

    /// <summary>No metadata at all: what every skin written before 3.2 has, and what a skin.json
    /// with no metadata keys parses to.</summary>
    public static readonly SkinMeta None = new();

    /// <summary>True when nothing is set — the signal the writer uses to omit every key.</summary>
    public bool IsEmpty =>
        Title is null && Author is null && Description is null
        && Version is null && SourceUrl is null && Tags.Count == 0;

    /// <summary>The only way to build a SkinMeta: normalizes every field (see the type remarks).
    /// Returns <see cref="None"/> when nothing survives, so "no metadata" has one representation.</summary>
    public static SkinMeta Create(string? title, string? author, string? description,
        string? version, IEnumerable<string>? tags, string? sourceUrl)
    {
        var meta = new SkinMeta
        {
            Title = Clean(title, MaxTitleLength, keepNewlines: false),
            Author = Clean(author, MaxAuthorLength, keepNewlines: false),
            Description = Clean(description, MaxDescriptionLength, keepNewlines: true),
            Version = Clean(version, MaxVersionLength, keepNewlines: false),
            Tags = CleanTags(tags),
            SourceUrl = CleanSourceUrl(sourceUrl),
        };
        return meta.IsEmpty ? None : meta;
    }

    /// <summary>The credit shown in a skin picker: the skin's own name (title, else the folder it
    /// lives in) and its author when it has one. Capped so it stays one readable row.</summary>
    public string DisplayLabel(string folderName)
    {
        var name = Title ?? folderName;
        var label = Author is null ? name : name + ByJoiner + Author;
        return FileNames.ForDisplay(label, MaxDisplayLabelLength);
    }

    /// <summary>Splits the designer's comma-separated tags box into normalized tags.</summary>
    public static IReadOnlyList<string> ParseTags(string? commaSeparated) =>
        CleanTags(commaSeparated?.Split(','));

    /// <summary>The inverse of <see cref="ParseTags"/> for populating that box from a loaded skin.</summary>
    public static string FormatTags(IReadOnlyList<string> tags) => string.Join(", ", tags);

    // Records compare members with EqualityComparer<T>.Default, which for a list is REFERENCE
    // equality — so a written-then-loaded SkinMeta would never equal the one that produced it,
    // and every roundtrip assertion (in tests and in the designer's dirty checks) would be a lie.
    // Tags therefore compare element-wise, and GetHashCode has to agree.
    public bool Equals(SkinMeta? other) =>
        other is not null
        && Title == other.Title && Author == other.Author && Description == other.Description
        && Version == other.Version && SourceUrl == other.SourceUrl
        && Tags.SequenceEqual(other.Tags, StringComparer.Ordinal);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Title);
        hash.Add(Author);
        hash.Add(Description);
        hash.Add(Version);
        hash.Add(SourceUrl);
        foreach (var tag in Tags)
            hash.Add(tag, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    /// <summary>Trims, flattens whitespace that would break a single-line credit, drops characters
    /// that disguise how the text renders, and caps the length by text element. Null when nothing
    /// meaningful is left.</summary>
    private static string? Clean(string? value, int cap, bool keepNewlines)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var builder = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            // CRLF/CR/LF all normalize to a single \n first, so the fields that keep newlines
            // carry exactly one line-ending spelling into skin.json and onto a gallery page.
            if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < value.Length && value[i + 1] == '\n')
                    i++;
                builder.Append(keepNewlines ? '\n' : ' ');
                continue;
            }
            if (c == '\t')
            {
                builder.Append(' '); // a tab is separation, not a control code to delete
                continue;
            }
            if (FileNames.IsDeceptive(c))
                continue; // C0/C1 controls and the bidi overrides: dropped outright
            builder.Append(c);
        }

        var cleaned = FileNames.ForDisplay(builder.ToString().Trim(), cap).Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static IReadOnlyList<string> CleanTags(IEnumerable<string>? tags)
    {
        if (tags is null) return Array.Empty<string>();
        var kept = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in tags)
        {
            if (Clean(raw, MaxTagLength, keepNewlines: false) is not { } tag) continue;
            if (!seen.Add(tag)) continue; // first spelling wins
            kept.Add(tag);
            if (kept.Count == MaxTags) break;
        }
        return kept.Count == 0 ? Array.Empty<string>() : kept;
    }

    /// <summary>Keeps a source URL only when it is an absolute https URL with a host and no
    /// credentials — the same bar <see cref="ProtocolLink"/> holds a download URL to, because this
    /// one is published as a link for other people to click.</summary>
    private static string? CleanSourceUrl(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxSourceUrlLength)
            return null;
        if (trimmed.Any(FileNames.IsDeceptive))
            return null;
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && uri.UserInfo.Length == 0
            && uri.Host.Length != 0
                ? trimmed // verbatim: re-serializing a Uri can rewrite escaping the author chose
                : null;
    }
}
