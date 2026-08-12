using System.Text.Json;

namespace AorinEQ.Core;

/// <summary>Everything a skin save carries besides the images. Defaults mirror
/// <see cref="SkinLoader"/>'s: text hidden, scale 1, fps 10, single-frame layers, a null
/// fill range meaning "the bar spans the full image width", and the 0.6 mute dim.</summary>
public sealed record SkinConfig(SkinText? Text, double Scale,
    double Fps = 10.0, int EmptyFrames = 1, int FullFrames = 1,
    int? FillStartX = null, int? FillEndX = null,
    int MutedFrames = 1, double MutedDim = 0.6);

/// <summary>Writes a skin folder (empty.png + full.png + optional skin.json) for the skin
/// designer. Image content validation stays with the caller (PngHeader before save,
/// SkinLoader as the source of truth on read); this class owns name validation and file layout.</summary>
public static class SkinWriter
{
    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Returns a user-readable error for an invalid skin (folder) name, or null when valid.
    /// The name is used verbatim (trimmed) as a directory name under the skins root.</summary>
    public static string? ValidateName(string name) => FileNames.Validate(name, "Skin name");

    /// <summary>Creates or overwrites <c>skinsRoot\name</c>: copies each source image to
    /// empty/full/muted keeping the SOURCE's extension (.png or .gif) and deletes the stale
    /// other-extension variant — the loader prefers .png over .gif, so a leftover file must
    /// never resurrect an old skin. A null <paramref name="mutedSourcePath"/> clears the optional
    /// muted layer (both extensions deleted). A source that already IS the destination is left in
    /// place (editing an existing skin without replacing its images). skin.json is written only
    /// when any config field is non-default; a stale one is deleted otherwise. Returns the folder.</summary>
    public static string Save(string skinsRoot, string name, string emptySourcePath, string fullSourcePath,
        SkinConfig config, string? mutedSourcePath = null)
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
            if (mutedSourcePath is not null)
                CopyLayer(mutedSourcePath, folder, "muted");
            else
                DeleteLayer(folder, "muted");

            var jsonPath = Path.Combine(folder, "skin.json");
            bool showText = config.Text is { Show: true };
            if (showText || config.Scale != 1.0 || config.Fps != 10.0
                || config.EmptyFrames != 1 || config.FullFrames != 1
                || config.MutedFrames != 1 || config.MutedDim != 0.6
                || config.FillStartX is not null || config.FillEndX is not null)
            {
                // Anonymous shape matches SkinLoader's SkinJson (case-insensitive on read);
                // null fields are omitted rather than written (WhenWritingNull). Text styling
                // fields are only emitted when they differ from the loader's defaults, so a
                // plain positioned number stays {show,x,y}.
                var json = JsonSerializer.Serialize(new
                {
                    percentText = showText ? BuildPercentText(config.Text!) : null,
                    scale = config.Scale,
                    fps = config.Fps,
                    emptyFrames = config.EmptyFrames,
                    fullFrames = config.FullFrames,
                    // Both omitted at their defaults so a pre-1.8 skin resaves byte-identically.
                    mutedFrames = config.MutedFrames == 1 ? (int?)null : config.MutedFrames,
                    mutedDim = config.MutedDim == 0.6 ? (double?)null : config.MutedDim,
                    fillStartX = config.FillStartX,
                    fillEndX = config.FillEndX,
                }, JsonWriteOptions);
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

    /// <summary>Serializes percentText with only the styling fields that differ from the
    /// loader's defaults, so a plainly-positioned number round-trips as {show,x,y}. Null-valued
    /// members are dropped by <see cref="JsonWriteOptions"/> (WhenWritingNull).</summary>
    private static object BuildPercentText(SkinText t) => new
    {
        show = true,
        x = t.X,
        y = t.Y,
        color = t.Color == "#FFFFFFFF" ? null : t.Color,
        fontFamily = t.FontFamily == "Segoe UI" ? null : t.FontFamily,
        fontSize = t.FontSize == 14 ? (double?)null : t.FontSize,
        bold = t.Bold ? (bool?)true : null,
        outlineColor = t.OutlineColor,
        outlineWidth = t.OutlineColor is null || t.OutlineWidth == 0 ? (double?)null : t.OutlineWidth,
        shadowColor = t.ShadowColor,
        shadowBlur = t.ShadowColor is null || t.ShadowBlur == 4 ? (double?)null : t.ShadowBlur,
        shadowDepth = t.ShadowColor is null || t.ShadowDepth == 2 ? (double?)null : t.ShadowDepth,
        align = t.Align == "left" ? null : t.Align,
    };

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

    /// <summary>Removes both extension variants of an optional layer (clearing the muted layer).</summary>
    private static void DeleteLayer(string folder, string layer)
    {
        foreach (var ext in new[] { ".png", ".gif" })
        {
            var path = Path.Combine(folder, layer + ext);
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
