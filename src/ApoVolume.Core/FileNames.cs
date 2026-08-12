namespace ApoVolume.Core;

/// <summary>Windows file/folder name validation shared by everything that turns a
/// user-supplied name into a path segment (skin folders, EQ preset files).</summary>
public static class FileNames
{
    internal static readonly string[] ReservedNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Cap on a name's length. Generous for anything a person types, and short enough
    /// that the resulting path stays well inside MAX_PATH — a name from an apo-volume:// link
    /// can otherwise be thousands of characters and only fail at write time.</summary>
    public const int MaxLength = 100;

    /// <summary>Returns a user-readable error for a name being ACCEPTED — typed in, or arriving
    /// from an apo-volume:// link — or null when it's fine. <paramref name="what"/> names the
    /// thing in messages ("Skin name", "Preset name").</summary>
    public static string? Validate(string name, string what) =>
        name.Trim().Length > MaxLength
            ? $"{what} is too long (limit {MaxLength} characters)."
            : ValidatePathSafety(name, what);

    /// <summary>Whether a name is safe to turn into a path segment. This is the check for names
    /// already on disk (loading, deleting): the length cap above is a policy for what we accept,
    /// and applying it to existing files would make anything named before it shipped
    /// unselectable and undeletable.</summary>
    public static bool IsPathSafe(string name) => ValidatePathSafety(name, "Name") is null;

    /// <summary>Characters Windows allows in a file name but which let a name lie about itself.
    /// The bidi overrides, embeddings and isolates reverse how the rest of a string renders, so
    /// a preset named with one can display as something else entirely — in a confirm dialog, in
    /// the editor, in Explorer. C1 controls are refused for the same reason C0 already is
    /// (<see cref="Path.GetInvalidFileNameChars"/> covers 0–31 but not 128–159). Zero-width
    /// joiners and the like are NOT in here: they are how emoji names are spelled.</summary>
    private static bool IsDeceptive(char c) =>
        char.IsControl(c)
        || c is (char)0x200E or (char)0x200F         // LRM, RLM
        || c is >= (char)0x202A and <= (char)0x202E  // LRE, RLE, PDF, LRO, RLO
        || c is >= (char)0x2066 and <= (char)0x2069; // LRI, RLI, FSI, PDI

    /// <summary>Shortens an untrusted name for display without cutting a character in half —
    /// truncation counts text elements (graphemes), not UTF-16 code units, so a combining
    /// sequence or an astral character can't be sliced into a different glyph.</summary>
    public static string ForDisplay(string name, int maxLength)
    {
        if (maxLength <= 0)
            return "";
        if (name.Length <= maxLength) // code units are an upper bound on text elements
            return name;
        var elements = System.Globalization.StringInfo.GetTextElementEnumerator(name);
        var kept = new System.Text.StringBuilder();
        int count = 0;
        while (elements.MoveNext())
        {
            if (count == maxLength - 1)
                return kept.Append('…').ToString(); // more to come: leave room for the ellipsis
            kept.Append(elements.GetTextElement());
            count++;
        }
        return kept.ToString(); // fewer text elements than the cap after all
    }

    private static string? ValidatePathSafety(string name, string what)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            return $"{what} cannot be empty.";
        if (trimmed.Any(IsDeceptive))
            return $"{what} contains characters that can disguise how it is displayed.";
        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return $"{what} contains characters not allowed in file names.";
        if (trimmed.EndsWith('.'))
            return $"{what} cannot end with a dot.";
        // Windows reserves device names both bare and with any extension (NUL, NUL.txt, COM1.png),
        // so the check runs against the stem before the first dot.
        var stem = trimmed.Split('.')[0];
        if (Array.Exists(ReservedNames, r => r.Equals(stem, StringComparison.OrdinalIgnoreCase)))
            return $"'{trimmed}' is a reserved Windows device name.";
        return null;
    }
}
