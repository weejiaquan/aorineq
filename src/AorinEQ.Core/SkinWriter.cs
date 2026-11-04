using System.Text.Json;

namespace AorinEQ.Core;

/// <summary>Everything a skin save carries besides the volume images. Defaults mirror
/// <see cref="SkinLoader"/>'s: text hidden, scale 1, fps 10, single-frame layers, a null
/// fill range meaning "the bar spans the full image width", the 0.6 mute dim, no
/// authorship metadata, no notice look and no AirPlay strip. <c>Notice.ArtPath</c> and the
/// strip's <c>EmptyPath</c>/<c>FullPath</c> are SOURCE images (any folder).</summary>
public sealed record SkinConfig(SkinText? Text, double Scale,
    double Fps = SkinClamps.DefaultFps, int EmptyFrames = 1, int FullFrames = 1,
    int? FillStartX = null, int? FillEndX = null,
    int MutedFrames = 1, double MutedDim = SkinClamps.DefaultMutedDim,
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
        var staged = new List<string>();
        try
        {
            Directory.CreateDirectory(folder);

            // Layers are written one after another, so a source that is ANOTHER layer's file in
            // this folder (empty and full swapped, notice art pointed at full.png) would be
            // overwritten or deleted before its own turn came and both layers would end up with
            // the same art. Every such source is copied aside first; a source that is its own
            // destination is still left in place.
            string Stage(string source, string layer)
            {
                if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(source)), Path.GetFullPath(folder),
                        StringComparison.OrdinalIgnoreCase)
                    || IsDestination(source, folder, layer))
                    return source;
                var copy = Path.Combine(Path.GetTempPath(), $"aorineq-skin-{Guid.NewGuid():N}{Path.GetExtension(source)}");
                File.Copy(source, copy);
                staged.Add(copy);
                return copy;
            }
            var emptySource = Stage(emptySourcePath, SkinLoader.EmptyLayer);
            var fullSource = Stage(fullSourcePath, SkinLoader.FullLayer);
            var mutedSource = mutedSourcePath is null ? null : Stage(mutedSourcePath, SkinLoader.MutedLayer);
            var stripEmptySource = config.AirPlay is { } s1 ? Stage(s1.EmptyPath, SkinLoader.AirPlayEmptyLayer) : null;
            var stripFullSource = config.AirPlay is { } s2 ? Stage(s2.FullPath, SkinLoader.AirPlayFullLayer) : null;
            var noticeSource = config.Notice?.ArtPath is { } art ? Stage(art, SkinLoader.NoticeLayer) : null;

            CopyLayer(emptySource, folder, SkinLoader.EmptyLayer);
            CopyLayer(fullSource, folder, SkinLoader.FullLayer);
            if (mutedSource is not null)
                CopyLayer(mutedSource, folder, SkinLoader.MutedLayer);
            else
                DeleteLayer(folder, SkinLoader.MutedLayer);
            if (stripEmptySource is not null && stripFullSource is not null)
            {
                CopyLayer(stripEmptySource, folder, SkinLoader.AirPlayEmptyLayer);
                CopyLayer(stripFullSource, folder, SkinLoader.AirPlayFullLayer);
            }
            else
            {
                DeleteLayer(folder, SkinLoader.AirPlayEmptyLayer);
                DeleteLayer(folder, SkinLoader.AirPlayFullLayer);
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

            if (noticeSource is not null)
                CopyLayer(noticeSource, folder, SkinLoader.NoticeLayer);
            else
                DeleteLayer(folder, SkinLoader.NoticeLayer);

            var percentText = OptionalText(config.Text, SkinLayer.PercentText);
            var meta = config.Meta ?? SkinMeta.None;
            var airPlay = config.AirPlay is { } a ? BuildAirPlay(a) : null;

            if (airPlay is not null || config.Notice is not null || noticeOff || percentText is not null
                || config.Scale != SkinClamps.DefaultScale || config.Fps != SkinClamps.DefaultFps
                || config.EmptyFrames != 1 || config.FullFrames != 1
                || config.MutedFrames != 1 || config.MutedDim != SkinClamps.DefaultMutedDim
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
                    percentText,
                    scale = config.Scale,
                    fps = config.Fps,
                    emptyFrames = config.EmptyFrames,
                    fullFrames = config.FullFrames,
                    // Both omitted at their defaults so a pre-1.8 skin resaves byte-identically.
                    mutedFrames = config.MutedFrames == 1 ? (int?)null : config.MutedFrames,
                    mutedDim = config.MutedDim == SkinClamps.DefaultMutedDim ? (double?)null : config.MutedDim,
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
        finally
        {
            foreach (var copy in staged)
                try { File.Delete(copy); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a temp file */ }
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
    /// round-trips as {show,x,y}; null for no text, or for the layer's
    /// <see cref="SkinText.HiddenOf"/>, which is the same skin. "show" is written as it is: a
    /// hidden text that reaches here has something to keep.</summary>
    private static Dictionary<string, object?>? OptionalText(SkinText? t, SkinLayer layer)
    {
        var none = SkinText.HiddenOf(layer);
        return t is null || t == none
            ? null
            : TextMembers(new Dictionary<string, object?> { ["show"] = t.Show, ["x"] = t.X, ["y"] = t.Y }, t, none.Weight);
    }

    /// <summary><paramref name="placement"/> followed by the styling members every text writes,
    /// each only where it differs from the loader's default; null when none is set. The legacy
    /// "bold" is never written: a bold text is "weight": 700.</summary>
    private static Dictionary<string, object?>? TextMembers(Dictionary<string, object?> placement, SkinText t, int defaultWeight)
    {
        placement["color"] = t.Color == SkinClamps.DefaultTextColor ? null : t.Color;
        placement["fontFamily"] = t.FontFamily == SkinClamps.DefaultFontFamily ? null : t.FontFamily;
        placement["fontSize"] = t.FontSize == SkinClamps.DefaultFontSize ? null : t.FontSize;
        placement["weight"] = t.Weight == defaultWeight ? null : t.Weight;
        placement["italic"] = t.Italic ? true : null;
        placement["letterSpacing"] = t.LetterSpacing == 0 ? null : t.LetterSpacing;
        placement["outlineColor"] = t.OutlineColor;
        placement["outlineWidth"] = t.OutlineColor is null || t.OutlineWidth == 0 ? null : t.OutlineWidth;
        placement["shadowColor"] = t.ShadowColor;
        placement["shadowBlur"] = t.ShadowColor is null || t.ShadowBlur == SkinClamps.DefaultShadowBlur ? null : t.ShadowBlur;
        placement["shadowDepth"] = t.ShadowColor is null || t.ShadowDepth == SkinClamps.DefaultShadowDepth ? null : t.ShadowDepth;
        placement["align"] = t.Align == "left" ? null : t.Align;
        return NonNullMembers(placement);
    }

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
        ["nameText"] = OptionalText(a.Name, SkinLayer.AirPlayName),
        ["percentText"] = OptionalText(a.Percent, SkinLayer.AirPlayLevel),
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
            text = TextMembers(new Dictionary<string, object?>
            {
                ["x"] = n.HasArt || n.Text.X != d.Text.X ? n.Text.X : null,
                ["y"] = n.HasArt || n.Text.Y != d.Text.Y ? n.Text.Y : null,
            }, n.Text, d.Text.Weight),
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

    private static bool IsGif(string source) =>
        Path.GetExtension(source).Equals(".gif", StringComparison.OrdinalIgnoreCase);

    private static string Destination(string source, string folder, string layer) =>
        Path.Combine(folder, layer + (IsGif(source) ? ".gif" : ".png"));

    private static bool IsDestination(string source, string folder, string layer) =>
        string.Equals(Path.GetFullPath(source), Path.GetFullPath(Destination(source, folder, layer)),
            StringComparison.OrdinalIgnoreCase);

    private static void CopyLayer(string source, string folder, string layer)
    {
        var staleVariant = Path.Combine(folder, layer + (IsGif(source) ? ".png" : ".gif"));

        if (!IsDestination(source, folder, layer))
            File.Copy(source, Destination(source, folder, layer), overwrite: true);
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
