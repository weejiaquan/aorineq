using System.Text.Json;

namespace ApoVolume.Core;

/// <summary>Position/visibility of the percent-text overlay drawn on top of a skin.</summary>
public sealed record SkinText(bool Show, int X, int Y);

/// <summary>Result of loading one skin folder. Always has Name/Folder/EmptyPath/FullPath populated;
/// on failure Width/Height are 0 and Error describes what went wrong. Width/Height are the LOGICAL
/// frame size: for a sprite-sheet PNG that is height/frames; for a GIF the logical screen size.
/// EmptyFrames/FullFrames are the declared sheet frame counts (always 1 for GIF layers — a GIF's
/// actual frame count and per-frame delays are discovered at decode time in the UI layer).</summary>
public sealed record SkinInfo(string Name, string Folder, string EmptyPath, string FullPath,
    int Width, int Height, SkinText? Text, double Scale,
    double Fps, int EmptyFrames, int FullFrames, bool EmptyIsGif, bool FullIsGif, string? Error)
{
    public bool IsValid => Error is null;
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

        SkinInfo Bad(string error) => new(name, folder, emptyPath, fullPath, 0, 0, text, scale,
            fps, 1, 1, false, false, error);

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
                    text = new SkinText(pt.Show, pt.X, pt.Y);
                if (parsed?.Scale is { } rawScale)
                    scale = Math.Clamp(rawScale, MinScale, MaxScale);
                if (parsed?.Fps is { } rawFps)
                    fps = Math.Clamp(rawFps, MinFps, MaxFps);
                if (parsed?.EmptyFrames is { } ef)
                    emptyFrames = Math.Max(1, ef);
                if (parsed?.FullFrames is { } ff)
                    fullFrames = Math.Max(1, ff);
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

            return new SkinInfo(name, folder, empty.Path, full.Path,
                empty.LogicalWidth, empty.LogicalHeight, text, scale,
                fps, empty.IsGif ? 1 : emptyFrames, full.IsGif ? 1 : fullFrames,
                empty.IsGif, full.IsGif, null);
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
    }

    private sealed class SkinTextJson
    {
        public bool Show { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
    }
}
