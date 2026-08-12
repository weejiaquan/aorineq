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

    /// <summary>Returns a user-readable error for an invalid name, or null when valid.
    /// <paramref name="what"/> names the thing in messages ("Skin name", "Preset name").</summary>
    public static string? Validate(string name, string what)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            return $"{what} cannot be empty.";
        if (trimmed.Length > MaxLength)
            return $"{what} is too long (limit {MaxLength} characters).";
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
