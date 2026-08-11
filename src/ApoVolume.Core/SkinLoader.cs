using System.Text.Json;

namespace ApoVolume.Core;

/// <summary>Position, visibility, and styling of the percent-text overlay drawn on top of a
/// skin. Colors are stored as raw strings (#AARRGGBB or #RRGGBB) and parsed in the UI layer,
/// which falls back gracefully on malformed values; a null OutlineColor/ShadowColor means that
/// effect is off. Defaults reproduce the pre-styling look (white, 14px, bold-ish).</summary>
public sealed record SkinText(bool Show, int X, int Y,
    string Color = "#FFFFFFFF", string FontFamily = "Segoe UI", double FontSize = 14, bool Bold = false,
    string? OutlineColor = null, double OutlineWidth = 0,
    string? ShadowColor = null, double ShadowBlur = 4, double ShadowDepth = 2,
    string Align = "left");

/// <summary>Result of loading one skin folder. Always has Name/Folder/EmptyPath/FullPath populated;
/// on failure Width/Height are 0 and Error describes what went wrong. Width/Height are the LOGICAL
/// frame size: for a sprite-sheet PNG that is height/frames; for a GIF the logical screen size.
/// EmptyFrames/FullFrames/MutedFrames are the declared sheet frame counts (always 1 for GIF layers
/// — a GIF's actual frame count and per-frame delays are discovered at decode time in the UI
/// layer). MutedPath is the optional muted-state artwork (null = dim the empty layer by MutedDim
/// instead).</summary>
public sealed record SkinInfo(string Name, string Folder, string EmptyPath, string FullPath,
    int Width, int Height, SkinText? Text, double Scale,
    double Fps, int EmptyFrames, int FullFrames, bool EmptyIsGif, bool FullIsGif,
    int FillStartX, int FillEndX,
    string? MutedPath, bool MutedIsGif, int MutedFrames, double MutedDim, string? Error)
{
    public bool IsValid => Error is null;

    /// <summary>Whether the skin ships dedicated muted-state artwork.</summary>
    public bool HasMuted => MutedPath is not null;
}

/// <summary>Loads and validates skin folders. Each layer ("empty", "full") resolves to
/// &lt;layer&gt;.png (preferred — static or sprite sheet) or &lt;layer&gt;.gif (animated).</summary>
public static class SkinLoader
{
    private const double MinScale = 0.25;
    private const double MaxScale = 4.0;
    private const double DefaultScale = 1.0;
    private const double MinFps = 1.0;
    private const double MaxFps = 60.0;
    private const double DefaultFps = 10.0;
    private const double MinFontSize = 4.0;
    private const double MaxFontSize = 200.0;
    private const double MaxOutlineWidth = 20.0;
    private const double MaxShadow = 50.0;
    private const double DefaultMutedDim = 0.6; // the pre-1.8 hardcoded mute dim opacity

    private static string EmptyToDefault(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    /// <summary>Case-insensitive align parse; anything but center/right reads as left, the
    /// historical anchor semantics of x.</summary>
    private static string NormalizeAlign(string? value)
    {
        var v = value?.Trim().ToLowerInvariant();
        return v is "center" or "right" ? v : "left";
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    // JsonSerializerOptions caches type metadata internally — a fresh instance per Deserialize
    // call would rebuild that metadata every time a skin is (re)loaded or the folder is scanned.
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Loads and validates a single skin folder. Never throws; any failure is reported via SkinInfo.Error.</summary>
    public static SkinInfo Load(string folder)
    {
        string name = folder;
        string emptyPath = Path.Combine(folder, "empty.png");
        string fullPath = Path.Combine(folder, "full.png");
        SkinText? text = null;
        double scale = DefaultScale;
        double fps = DefaultFps;
        int emptyFrames = 1;
        int fullFrames = 1;
        int mutedFrames = 1;
        double mutedDim = DefaultMutedDim;
        int? fillStartJson = null;
        int? fillEndJson = null;

        SkinInfo Bad(string error) => new(name, folder, emptyPath, fullPath, 0, 0, text, scale,
            fps, 1, 1, false, false, 0, 0, null, false, 1, DefaultMutedDim, error);

        try
        {
            name = new DirectoryInfo(folder).Name;
            if (!Directory.Exists(folder))
                return Bad($"Skin folder not found: {folder}");

            // skin.json is parsed before the images: declared sheet frame counts are needed to
            // validate sheet geometry below.
            string jsonPath = Path.Combine(folder, "skin.json");
            if (File.Exists(jsonPath))
            {
                SkinJson? parsed;
                try
                {
                    parsed = JsonSerializer.Deserialize<SkinJson>(File.ReadAllText(jsonPath), JsonOptions);
                }
                catch (JsonException ex)
                {
                    return Bad($"skin.json is invalid: {ex.Message}");
                }

                if (parsed?.PercentText is { } pt)
                    text = new SkinText(pt.Show, pt.X, pt.Y,
                        Color: EmptyToDefault(pt.Color, "#FFFFFFFF"),
                        FontFamily: EmptyToDefault(pt.FontFamily, "Segoe UI"),
                        FontSize: Math.Clamp(pt.FontSize ?? 14, MinFontSize, MaxFontSize),
                        Bold: pt.Bold ?? false,
                        OutlineColor: NullIfBlank(pt.OutlineColor),
                        OutlineWidth: Math.Clamp(pt.OutlineWidth ?? 0, 0, MaxOutlineWidth),
                        ShadowColor: NullIfBlank(pt.ShadowColor),
                        ShadowBlur: Math.Clamp(pt.ShadowBlur ?? 4, 0, MaxShadow),
                        ShadowDepth: Math.Clamp(pt.ShadowDepth ?? 2, 0, MaxShadow),
                        Align: NormalizeAlign(pt.Align));
                if (parsed?.Scale is { } rawScale)
                    scale = Math.Clamp(rawScale, MinScale, MaxScale);
                if (parsed?.Fps is { } rawFps)
                    fps = Math.Clamp(rawFps, MinFps, MaxFps);
                if (parsed?.EmptyFrames is { } ef)
                    emptyFrames = Math.Max(1, ef);
                if (parsed?.FullFrames is { } ff)
                    fullFrames = Math.Max(1, ff);
                if (parsed?.MutedFrames is { } mf)
                    mutedFrames = Math.Max(1, mf);
                if (parsed?.MutedDim is { } dim)
                    mutedDim = Math.Clamp(dim, 0.0, 1.0);
                fillStartJson = parsed?.FillStartX;
                fillEndJson = parsed?.FillEndX;
            }

            var empty = ResolveLayer(folder, "empty", emptyFrames);
            emptyPath = empty.Path;
            if (empty.Error is not null)
                return Bad(empty.Error);
            var full = ResolveLayer(folder, "full", fullFrames);
            fullPath = full.Path;
            if (full.Error is not null)
                return Bad(full.Error);

            if (empty.LogicalWidth != full.LogicalWidth || empty.LogicalHeight != full.LogicalHeight)
                return Bad(
                    $"{Path.GetFileName(full.Path)} frame is {full.LogicalWidth}×{full.LogicalHeight} " +
                    $"but {Path.GetFileName(empty.Path)} frame is {empty.LogicalWidth}×{empty.LogicalHeight}");

            // Optional muted layer: same resolution and logical-frame-size rules as empty/full,
            // just never required — absence means "dim the empty layer by mutedDim" instead.
            string? mutedPath = null;
            bool mutedIsGif = false;
            if (File.Exists(Path.Combine(folder, "muted.png")) || File.Exists(Path.Combine(folder, "muted.gif")))
            {
                var muted = ResolveLayer(folder, "muted", mutedFrames);
                if (muted.Error is not null)
                    return Bad(muted.Error);
                if (muted.LogicalWidth != empty.LogicalWidth || muted.LogicalHeight != empty.LogicalHeight)
                    return Bad(
                        $"{Path.GetFileName(muted.Path)} frame is {muted.LogicalWidth}×{muted.LogicalHeight} " +
                        $"but {Path.GetFileName(empty.Path)} frame is {empty.LogicalWidth}×{empty.LogicalHeight}");
                mutedPath = muted.Path;
                mutedIsGif = muted.IsGif;
            }

            // Fill range: where the bar actually lives inside the (possibly wider, decorative)
            // image. Defaults to the full width; values clamp into the image, but an inverted or
            // empty range is an authoring error worth surfacing rather than guessing around.
            int fillStart = Math.Clamp(fillStartJson ?? 0, 0, empty.LogicalWidth);
            int fillEnd = Math.Clamp(fillEndJson ?? empty.LogicalWidth, 0, empty.LogicalWidth);
            if (fillStart >= fillEnd)
                return Bad($"fillStartX ({fillStart}) must be less than fillEndX ({fillEnd})");

            return new SkinInfo(name, folder, empty.Path, full.Path,
                empty.LogicalWidth, empty.LogicalHeight, text, scale,
                fps, empty.IsGif ? 1 : emptyFrames, full.IsGif ? 1 : fullFrames,
                empty.IsGif, full.IsGif, fillStart, fillEnd,
                mutedPath, mutedIsGif, mutedPath is null || mutedIsGif ? 1 : mutedFrames, mutedDim, null);
        }
        catch (Exception ex)
        {
            return Bad($"Failed to load skin: {ex.Message}");
        }
    }

    /// <summary>Resolves one layer: .png wins over .gif. For PNG sheets the declared frame count
    /// must divide the pixel height evenly; the logical height is one frame's. GIFs ignore the
    /// declared count (their real frames come from the decoder) and use the logical screen size.</summary>
    private static (string Path, bool IsGif, int LogicalWidth, int LogicalHeight, string? Error)
        ResolveLayer(string folder, string layer, int declaredFrames)
    {
        string pngPath = Path.Combine(folder, layer + ".png");
        string gifPath = Path.Combine(folder, layer + ".gif");
        if (File.Exists(pngPath))
        {
            var size = PngHeader.Read(pngPath);
            if (size is null)
                return (pngPath, false, 0, 0, $"{layer}.png is not a valid PNG");
            if (size.Value.Height % declaredFrames != 0)
                return (pngPath, false, 0, 0,
                    $"{layer}.png height {size.Value.Height} is not divisible by {layer}Frames {declaredFrames}");
            return (pngPath, false, size.Value.Width, size.Value.Height / declaredFrames, null);
        }
        if (File.Exists(gifPath))
        {
            var size = GifHeader.Read(gifPath);
            if (size is null)
                return (gifPath, true, 0, 0, $"{layer}.gif is not a valid GIF");
            return (gifPath, true, size.Value.Width, size.Value.Height, null);
        }
        return (pngPath, false, 0, 0, $"{layer}.png or {layer}.gif not found");
    }

    /// <summary>Lists every subfolder of skinsRoot as a SkinInfo (valid and invalid alike). Empty list if root is missing.</summary>
    public static IReadOnlyList<SkinInfo> Scan(string skinsRoot)
    {
        if (!Directory.Exists(skinsRoot)) return Array.Empty<SkinInfo>();

        return Directory.GetDirectories(skinsRoot)
            .Where(d => !new DirectoryInfo(d).Name.StartsWith('.')) // dot-folders: import staging, VCS
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .Select(Load)
            .ToList();
    }

    private sealed class SkinJson
    {
        public SkinTextJson? PercentText { get; set; }
        public double? Scale { get; set; }
        public double? Fps { get; set; }
        public int? EmptyFrames { get; set; }
        public int? FullFrames { get; set; }
        public int? MutedFrames { get; set; }
        public double? MutedDim { get; set; }
        public int? FillStartX { get; set; }
        public int? FillEndX { get; set; }
    }

    private sealed class SkinTextJson
    {
        public bool Show { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public string? Color { get; set; }
        public string? FontFamily { get; set; }
        public double? FontSize { get; set; }
        public bool? Bold { get; set; }
        public string? OutlineColor { get; set; }
        public double? OutlineWidth { get; set; }
        public string? ShadowColor { get; set; }
        public double? ShadowBlur { get; set; }
        public double? ShadowDepth { get; set; }
        public string? Align { get; set; }
    }
}
