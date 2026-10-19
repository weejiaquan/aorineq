using System.Text.Json;

namespace AorinEQ.Core;

/// <summary>Everything a skin save carries besides the volume images. Defaults mirror
/// <see cref="SkinLoader"/>'s: text hidden, scale 1, fps 10, single-frame layers, a null
/// fill range meaning "the bar spans the full image width", the 0.6 mute dim, no
/// authorship metadata, no notice look and no AirPlay strip. <c>Notice.ArtPath</c> and the
/// strip's <c>EmptyPath</c>/<c>FullPath</c> are SOURCE images (any folder).</summary>
public sealed record SkinConfig(SkinText? Text, double Scale,
    double Fps = 10.0, int EmptyFrames = 1, int FullFrames = 1,
    int? FillStartX = null, int? FillEndX = null,
    int MutedFrames = 1, double MutedDim = 0.6,
    SkinMeta? Meta = null, SkinNotice? Notice = null, SkinAirPlay? AirPlay = null);

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
    /// empty/full/muted, airplay-empty/airplay-full and notice keeping the SOURCE's extension (.png
    /// or .gif) and deletes the stale other-extension variant — the loader prefers .png over .gif,
    /// so a leftover file must never resurrect an old skin. A null <paramref name="mutedSourcePath"/>,
    /// a null <see cref="SkinConfig.AirPlay"/> or notice art left unset clears that layer (both
    /// extensions deleted). A source that already IS the destination is left in place (editing an
    /// existing skin without replacing its images). skin.json is written only when any config
    /// field is non-default (or a notice is being turned off where that must be recorded as
    /// "notice": false); a stale one is deleted otherwise. Returns the folder.</summary>
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
            if (config.AirPlay is { } strip)
            {
                CopyLayer(strip.EmptyPath, folder, "airplay-empty");
                CopyLayer(strip.FullPath, folder, "airplay-full");
            }
            else
            {
                DeleteLayer(folder, "airplay-empty");
                DeleteLayer(folder, "airplay-full");
            }
            var jsonPath = Path.Combine(folder, "skin.json");

            // A notice turned off is written down as "notice": false where leaving it out would
            // not stick: a shipped style folder, which DefaultSkins tops up with the shipped
            // notice on the next launch unless a "notice" key (any value) is there, and any folder
            // that had a notice, so turning it off is an explicit decision in the file rather
            // than an absence. The loader reads false as no notice. A skin that never had one
            // resaves exactly as before. Checked BEFORE the art is deleted below.
            bool noticeOff = config.Notice is null
                && (Directory.GetFiles(folder, "notice.*").Length > 0
                    || ReadTopLevel(jsonPath, "notice") is not null
                    || SkinArt.All.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase));

            if (config.Notice?.ArtPath is { } noticeArt)
                CopyLayer(noticeArt, folder, "notice");
            else
                DeleteLayer(folder, "notice");

            bool hasText = config.Text is { } text && text != SkinText.Hidden;
            var meta = config.Meta ?? SkinMeta.None;
            var airPlay = config.AirPlay is { } a ? BuildAirPlay(a) : null;

            if (airPlay is not null || config.Notice is not null || noticeOff || hasText
                || config.Scale != 1.0 || config.Fps != 10.0
                || config.EmptyFrames != 1 || config.FullFrames != 1
                || config.MutedFrames != 1 || config.MutedDim != 0.6
                || config.FillStartX is not null || config.FillEndX is not null
                || !meta.IsEmpty)
            {
                // Anonymous shape matches SkinLoader's SkinJson (case-insensitive on read);
                // null fields are omitted rather than written (WhenWritingNull). Text styling
                // fields are only emitted when they differ from the loader's defaults, so a
                // plain positioned number stays {show,x,y}. The metadata block is written the
                // same way — every field absent when unset, and an empty tag list written as
                // NOTHING rather than as [] — so a skin with no credits (every skin authored
                // before 3.2) and a skin without an AirPlay strip resave byte-identically.
                var json = JsonSerializer.Serialize(new
                {
                    title = meta.Title,
                    author = meta.Author,
                    description = meta.Description,
                    version = meta.Version,
                    tags = meta.Tags.Count == 0 ? null : meta.Tags,
                    sourceUrl = meta.SourceUrl,
                    percentText = hasText ? BuildText(config.Text!) : null,
                    scale = config.Scale,
                    fps = config.Fps,
                    emptyFrames = config.EmptyFrames,
                    fullFrames = config.FullFrames,
                    // Both omitted at their defaults so a pre-1.8 skin resaves byte-identically.
                    mutedFrames = config.MutedFrames == 1 ? (int?)null : config.MutedFrames,
                    mutedDim = config.MutedDim == 0.6 ? (double?)null : config.MutedDim,
                    fillStartX = config.FillStartX,
                    fillEndX = config.FillEndX,
                    airplay = airPlay,
                    notice = config.Notice is not null ? BuildNotice(config.Notice) : noticeOff ? false : (object?)null,
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

    /// <summary>A top-level property of an existing skin.json, matched case-insensitively as the
    /// loader reads it - here, whether a "notice" key is there at all. Absent file, unreadable file
    /// or malformed JSON all read as "nothing there" - this runs on the save path, and refusing to
    /// save a skin because the file it is replacing was already broken would be the wrong trade.</summary>
    private static JsonElement? ReadTopLevel(string jsonPath, string name)
    {
        if (!File.Exists(jsonPath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                // Cloned: a JsonElement is only valid while its document lives, and this one is
                // disposed on the way out of this method.
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    return property.Value.Clone();
            }
            return null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Serializes one text overlay (the percent number, either AirPlay label) with only
    /// the styling fields that differ from the loader's defaults, so a plainly positioned number
    /// round-trips as {show,x,y}. "show" is written as it is: a hidden text that reaches here has
    /// something to keep. Null-valued members are dropped by <see cref="JsonWriteOptions"/>.</summary>
    private static object BuildText(SkinText t) => new
    {
        show = t.Show,
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

    /// <summary>Serializes the AirPlay strip with every field at the loader's default omitted:
    /// fill range at the strip's edges, single frames, the dropdown covering the whole strip, no
    /// labels, no power button. A strip at all its defaults is null - no block at all - because
    /// its art alone declares it to the loader, so an art-only skin resaves byte-identically.</summary>
    private static Dictionary<string, object?>? BuildAirPlay(SkinAirPlay a) => NonNullMembers(new Dictionary<string, object?>
    {
        ["fillStartX"] = a.FillStartX == 0 ? null : a.FillStartX,
        ["fillEndX"] = a.FillEndX == a.Width ? null : a.FillEndX,
        ["emptyFrames"] = a.EmptyFrames == 1 ? null : a.EmptyFrames,
        ["fullFrames"] = a.FullFrames == 1 ? null : a.FullFrames,
        ["nameText"] = a.Name is { } name && name != SkinText.Hidden ? BuildText(name) : null,
        ["percentText"] = a.Percent is { } level && level != SkinText.Hidden ? BuildText(level) : null,
        ["dropdownHit"] = a.DropdownHit == new SkinHitRect(0, 0, a.Width, a.Height) ? null : BuildHit(a.DropdownHit),
        ["connectHit"] = a.ConnectHit is { } connect ? BuildHit(connect) : null,
        ["connectColor"] = a.ConnectColor,
    });

    private static object BuildHit(SkinHitRect r) => new { x = r.X, y = r.Y, w = r.Width, h = r.Height };

    /// <summary>Serializes the notice with only the fields that differ from
    /// <see cref="SkinLoader.DefaultNotice"/>. Art notices write every position; a plate writes
    /// the positions the loader reads for a plate (text.x, text.y, glyph.x) only where they differ
    /// from its defaults (0, 0, 16), so a loaded plate saves back to the same notice and a plain
    /// plate still writes none. A plate's glyph Y is always 0 on load and is never written.
    /// Empty text/glyph objects are dropped.</summary>
    private static object BuildNotice(SkinNotice n)
    {
        var d = SkinLoader.DefaultNotice;
        return new
        {
            background = n.Background == d.Background ? null : n.Background,
            border = n.Border,
            cornerRadius = n.CornerRadius == d.CornerRadius ? (double?)null : n.CornerRadius,
            padding = n.PadX == d.PadX && n.PadY == d.PadY ? null : new { x = n.PadX, y = n.PadY },
            text = NonNullMembers(new Dictionary<string, object?>
            {
                ["x"] = n.HasArt || n.Text.X != d.Text.X ? n.Text.X : null,
                ["y"] = n.HasArt || n.Text.Y != d.Text.Y ? n.Text.Y : null,
                ["color"] = n.Text.Color == d.Text.Color ? null : n.Text.Color,
                ["fontFamily"] = n.Text.FontFamily == d.Text.FontFamily ? null : n.Text.FontFamily,
                ["fontSize"] = n.Text.FontSize == d.Text.FontSize ? null : n.Text.FontSize,
                ["bold"] = n.Text.Bold == d.Text.Bold ? null : n.Text.Bold,
                ["align"] = n.Text.Align == "left" ? null : n.Text.Align,
            }),
            textWidth = n.HasArt ? n.TextWidth : (int?)null,
            glyph = NonNullMembers(new Dictionary<string, object?>
            {
                ["show"] = n.Glyph.Show ? null : false,
                ["x"] = n.HasArt || n.Glyph.X != d.Glyph.X ? n.Glyph.X : null,
                ["y"] = n.HasArt ? n.Glyph.Y : null,
                ["size"] = n.Glyph.Size == d.Glyph.Size ? null : n.Glyph.Size,
                ["color"] = n.Glyph.Color,
            }),
            frames = n.HasArt && n.ArtFrames != 1 ? n.ArtFrames : (int?)null,
        };
    }

    /// <summary>The members that are set, or null when none are (so the whole object is omitted).</summary>
    private static Dictionary<string, object?>? NonNullMembers(Dictionary<string, object?> members)
    {
        var set = members.Where(m => m.Value is not null).ToDictionary(m => m.Key, m => m.Value);
        return set.Count == 0 ? null : set;
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
