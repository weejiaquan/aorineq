using System.Text.Json;

namespace ApoVolume.Core;

/// <summary>Everything a skin save carries besides the two images. Defaults mirror
/// <see cref="SkinLoader"/>'s: text hidden, scale 1, fps 10, single-frame layers.</summary>
public sealed record SkinConfig(SkinText? Text, double Scale,
    double Fps = 10.0, int EmptyFrames = 1, int FullFrames = 1);

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
        // Windows reserves device names both bare and with any extension (NUL, NUL.txt, COM1.png),
        // so the check runs against the stem before the first dot.
        var stem = trimmed.Split('.')[0];
        if (Array.Exists(ReservedNames, r => r.Equals(stem, StringComparison.OrdinalIgnoreCase)))
            return $"'{trimmed}' is a reserved Windows device name.";
        return null;
    }

    /// <summary>Creates or overwrites <c>skinsRoot\name</c>: copies each source image to
    /// empty/full keeping the SOURCE's extension (.png or .gif) and deletes the stale
    /// other-extension variant — the loader prefers .png over .gif, so a leftover file must
    /// never resurrect an old skin. A source that already IS the destination is left in place
    /// (editing an existing skin without replacing its images). skin.json is written only when
    /// any config field is non-default; a stale one is deleted otherwise. Returns the folder.</summary>
    public static string Save(string skinsRoot, string name, string emptySourcePath, string fullSourcePath,
        SkinConfig config)
    {
        var nameError = ValidateName(name);
        if (nameError is not null)
            throw new ArgumentException(nameError, nameof(name));

        var folder = Path.Combine(skinsRoot, name.Trim());
        try
        {
            Directory.CreateDirectory(folder);
            CopyLayer(emptySourcePath, folder, "empty");
            CopyLayer(fullSourcePath, folder, "full");

            var jsonPath = Path.Combine(folder, "skin.json");
            bool showText = config.Text is { Show: true };
            if (showText || config.Scale != 1.0 || config.Fps != 10.0
                || config.EmptyFrames != 1 || config.FullFrames != 1)
            {
                // Anonymous shape matches SkinLoader's SkinJson (case-insensitive on read).
                var json = JsonSerializer.Serialize(new
                {
                    percentText = showText ? new { show = true, x = config.Text!.X, y = config.Text.Y } : null,
                    scale = config.Scale,
                    fps = config.Fps,
                    emptyFrames = config.EmptyFrames,
                    fullFrames = config.FullFrames,
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

    private static void CopyLayer(string source, string folder, string layer)
    {
        bool isGif = Path.GetExtension(source).Equals(".gif", StringComparison.OrdinalIgnoreCase);
        var destination = Path.Combine(folder, layer + (isGif ? ".gif" : ".png"));
        var staleVariant = Path.Combine(folder, layer + (isGif ? ".png" : ".gif"));

        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            File.Copy(source, destination, overwrite: true);
        if (File.Exists(staleVariant))
            File.Delete(staleVariant);
    }
}
