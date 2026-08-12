namespace AorinEQ.Core;

/// <summary>EQ presets on disk: one ParametricEQ .txt per preset under the presets root
/// (<see cref="ApoPaths.GetPresetsRoot"/>). Files ARE the interchange format — import/export
/// and the AutoEq download all read/write the same text EAPO itself understands.</summary>
public static class PresetStore
{
    public static string? ValidateName(string name) => FileNames.Validate(name, "Preset name");

    /// <summary>Preset names (file stems) sorted case-insensitively; empty when the root is
    /// missing or unreadable.</summary>
    public static IReadOnlyList<string> List(string root)
    {
        try
        {
            if (!Directory.Exists(root))
                return Array.Empty<string>();
            return Directory.GetFiles(root, "*.txt")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Parses the named preset file, or null when it's missing/unreadable (the
    /// tolerant <see cref="EqPreset.Parse"/> never fails on content).</summary>
    public static EqPreset? Load(string root, string name)
    {
        // Path safety, not the full naming policy: a preset file already on disk must stay
        // loadable even if its name is longer than we would accept today.
        if (!FileNames.IsPathSafe(name))
            return null;
        var path = Path.Combine(root, name.Trim() + ".txt");
        try
        {
            if (!File.Exists(path))
                return null;
            return EqPreset.Parse(name.Trim(), File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes (or overwrites) the named preset with the given ParametricEQ text.
    /// Throws <see cref="ArgumentException"/> on an invalid name; IO failures bubble to the
    /// caller (the editor surfaces them).</summary>
    public static void Save(string root, string name, string parametricEqText)
    {
        if (ValidateName(name) is { } error)
            throw new ArgumentException(error, nameof(name));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, name.Trim() + ".txt"), parametricEqText);
    }

    /// <summary>Deletes the named preset; false when it didn't exist or couldn't be removed.</summary>
    public static bool Delete(string root, string name)
    {
        if (!FileNames.IsPathSafe(name)) // as Load: an existing file stays removable
            return false;
        var path = Path.Combine(root, name.Trim() + ".txt");
        try
        {
            if (!File.Exists(path))
                return false;
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Makes an arbitrary display name (an AutoEq model, a pasted title) safe as a
    /// preset file name: runs of invalid characters collapse to a single '-', trailing dots
    /// go, reserved device names get a defusing '-', and an empty result becomes "preset".
    /// The output always passes <see cref="ValidateName"/>.</summary>
    public static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        bool lastWasReplacement = false;
        foreach (var c in name)
        {
            if (Array.IndexOf(invalid, c) >= 0)
            {
                if (!lastWasReplacement)
                    sb.Append('-');
                lastWasReplacement = true;
            }
            else
            {
                sb.Append(c);
                lastWasReplacement = false;
            }
        }
        var result = sb.ToString().Trim().TrimEnd('.').Trim();
        if (result.Length == 0)
            return "preset";
        var stem = result.Split('.')[0];
        if (Array.Exists(FileNames.ReservedNames, r => r.Equals(stem, StringComparison.OrdinalIgnoreCase)))
            result += "-";
        return result;
    }
}
