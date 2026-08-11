using System.Text.Json;

namespace ApoVolume.Core;

/// <summary>Writes a skin folder (empty.png + full.png + optional skin.json) for the skin
/// designer. Image content validation stays with the caller (PngHeader before save,
/// SkinLoader as the source of truth on read); this class owns name validation and file layout.</summary>
public static class SkinWriter
{
    private static readonly string[] ReservedNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Returns a user-readable error for an invalid skin (folder) name, or null when valid.
    /// The name is used verbatim (trimmed) as a directory name under the skins root.</summary>
    public static string? ValidateName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            return "Skin name cannot be empty.";
        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "Skin name contains characters not allowed in folder names.";
        if (trimmed.EndsWith('.'))
            return "Skin name cannot end with a dot.";
        if (Array.Exists(ReservedNames, r => r.Equals(trimmed, StringComparison.OrdinalIgnoreCase)))
            return $"'{trimmed}' is a reserved Windows device name.";
        return null;
    }

    /// <summary>Creates or overwrites <c>skinsRoot\name</c>: copies the two source images to
    /// empty.png/full.png (a source that already IS the destination is left in place, which is
    /// how editing an existing skin without replacing its images works) and writes skin.json
    /// only when non-default — a stale skin.json from an earlier save is deleted otherwise.
    /// Returns the folder written.</summary>
    public static string Save(string skinsRoot, string name, string emptySourcePath, string fullSourcePath,
        SkinText? text, double scale)
    {
        var nameError = ValidateName(name);
        if (nameError is not null)
            throw new ArgumentException(nameError, nameof(name));

        var folder = Path.Combine(skinsRoot, name.Trim());
        try
        {
            Directory.CreateDirectory(folder);
            CopyUnlessSame(emptySourcePath, Path.Combine(folder, "empty.png"));
            CopyUnlessSame(fullSourcePath, Path.Combine(folder, "full.png"));

            var jsonPath = Path.Combine(folder, "skin.json");
            bool showText = text is { Show: true };
            if (showText || scale != 1.0)
            {
                // Anonymous shape matches SkinLoader's SkinJson (case-insensitive on read).
                var json = JsonSerializer.Serialize(new
                {
                    percentText = showText ? new { show = true, x = text!.X, y = text.Y } : null,
                    scale,
                });
                File.WriteAllText(jsonPath, json);
            }
            else if (File.Exists(jsonPath))
            {
                File.Delete(jsonPath);
            }
            return folder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Failed to save skin '{name.Trim()}': {ex.Message}", ex);
        }
    }

    private static void CopyUnlessSame(string source, string destination)
    {
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            return;
        File.Copy(source, destination, overwrite: true);
    }
}
