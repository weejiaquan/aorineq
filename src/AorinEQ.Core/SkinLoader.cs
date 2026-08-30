using System.Text.Json;

namespace AorinEQ.Core;

/// <summary>Position, visibility, and styling of the percent-text overlay drawn on top of a
/// skin. Colors are stored as raw strings (#AARRGGBB or #RRGGBB) and parsed in the UI layer,
/// which falls back gracefully on malformed values; a null OutlineColor/ShadowColor means that
/// effect is off. Defaults reproduce the pre-styling look (white, 14px, bold-ish).</summary>
public sealed record SkinText(bool Show, int X, int Y,
    string Color = "#FFFFFFFF", string FontFamily = "Segoe UI", double FontSize = 14, bool Bold = false,
    string? OutlineColor = null, double OutlineWidth = 0,
    string? ShadowColor = null, double ShadowBlur = 4, double ShadowDepth = 2,
    string Align = "left");

/// <summary>A rectangle in skin coordinates - the click target a skin declares for the AirPlay
/// dropdown. Always inside the bar it belongs to: the loader clamps it, because a region running
/// off the artwork is unclickable at its far end and a skin author has no way to see that from
/// looking at the JSON.</summary>
public sealed record SkinHitRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    /// <summary>Whether a point in skin coordinates falls inside.</summary>
    public bool Contains(double x, double y) => x >= X && x < Right && y >= Y && y < Bottom;

    public override string ToString() => $"[{X},{Y} {Width}x{Height}]";
}

/// <summary>The optional AirPlay bar a skin draws under its volume bar: two layers, a fill range,
/// text anchors for the receiver's name and level, and the region that opens the device dropdown.
///
/// Its own size, deliberately. It sits UNDER the volume bar rather than on top of it, so tying it
/// to the volume bar's dimensions would stop a skin drawing a slim device strip beneath a tall
/// volume graphic, and buy nothing.</summary>
public sealed record SkinAirPlay(
    string EmptyPath, string FullPath, int Width, int Height,
    bool EmptyIsGif, bool FullIsGif, int EmptyFrames, int FullFrames,
    int FillStartX, int FillEndX,
    SkinText? Name, SkinText? Percent, SkinHitRect DropdownHit,
    SkinHitRect? ConnectHit = null, string? ConnectColor = null);

/// <summary>Result of loading one skin folder. Always has Name/Folder/EmptyPath/FullPath populated;
/// on failure Width/Height are 0 and Error describes what went wrong. Width/Height are the LOGICAL
/// frame size: for a sprite-sheet PNG that is height/frames; for a GIF the logical screen size.
/// EmptyFrames/FullFrames/MutedFrames are the declared sheet frame counts (always 1 for GIF layers
/// — a GIF's actual frame count and per-frame delays are discovered at decode time in the UI
/// layer). MutedPath is the optional muted-state artwork (null = dim the empty layer by MutedDim
/// instead). Meta is the optional authorship/gallery metadata, never null: a skin without any is
/// <see cref="SkinMeta.None"/>, and it is populated even for an INVALID skin so a picker can still
/// say whose broken skin it is.</summary>
public sealed record SkinInfo(string Name, string Folder, string EmptyPath, string FullPath,
    int Width, int Height, SkinText? Text, double Scale,
    double Fps, int EmptyFrames, int FullFrames, bool EmptyIsGif, bool FullIsGif,
    int FillStartX, int FillEndX,
    string? MutedPath, bool MutedIsGif, int MutedFrames, double MutedDim,
    SkinMeta Meta, string? Error, SkinAirPlay? AirPlay = null, string? AirPlayError = null)
{
    public bool IsValid => Error is null;

    /// <summary>Whether this skin draws its own AirPlay bar. False for every skin written before
    /// the bar existed, and false for one whose AirPlay artwork did not load - both of which get
    /// the app's native AirPlay bar instead, so neither is a broken experience.</summary>
    public bool HasAirPlay => AirPlay is not null;

    /// <summary>Whether the skin ships dedicated muted-state artwork.</summary>
    public bool HasMuted => MutedPath is not null;

    /// <summary>The credit a picker shows for this skin: its title and author when it has them,
    /// its folder name otherwise.</summary>
    public string DisplayLabel => Meta.DisplayLabel(Name);
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
        SkinMeta meta = SkinMeta.None;

        SkinInfo Bad(string error) => new(name, folder, emptyPath, fullPath, 0, 0, text, scale,
            fps, 1, 1, false, false, 0, 0, null, false, 1, DefaultMutedDim, meta, error);

        try
        {
            name = new DirectoryInfo(folder).Name;
            if (!Directory.Exists(folder))
                return Bad($"Skin folder not found: {folder}");

            // skin.json is parsed before the images: declared sheet frame counts are needed to
            // validate sheet geometry below.
            JsonElement? airPlayJson = null;
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

                text = ReadText(parsed?.PercentText) ?? text;
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
                airPlayJson = parsed?.AirPlay;
                if (parsed is not null)
                    meta = SkinMeta.Create(
                        JsonString(parsed.Title), JsonString(parsed.Author),
                        JsonString(parsed.Description), JsonString(parsed.Version),
                        JsonStrings(parsed.Tags), JsonString(parsed.SourceUrl));
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

            // The AirPlay bar is resolved LAST and can only ever fail soft. A half-finished or
            // mismatched AirPlay layer must not cost somebody the volume OSD they actually use -
            // it loses its own bar, says why, and the app draws its native AirPlay bar instead.
            var (airPlay, airPlayError) = LoadAirPlay(folder, airPlayJson);

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
                mutedPath, mutedIsGif, mutedPath is null || mutedIsGif ? 1 : mutedFrames, mutedDim,
                meta, null, airPlay, airPlayError);
        }
        catch (Exception ex)
        {
            return Bad($"Failed to load skin: {ex.Message}");
        }
    }

    /// <summary>One text overlay's position and styling, with every value defaulted and clamped.
    ///
    /// Shared by the volume bar's percent text and by both of the AirPlay bar's labels: three
    /// callers, one set of defaults, so a clamp fixed in one place is fixed everywhere.</summary>
    private static SkinText? ReadText(SkinTextJson? t) =>
        t is null ? null : new SkinText(t.Show, t.X, t.Y,
            Color: EmptyToDefault(t.Color, "#FFFFFFFF"),
            FontFamily: EmptyToDefault(t.FontFamily, "Segoe UI"),
            FontSize: Math.Clamp(t.FontSize ?? 14, MinFontSize, MaxFontSize),
            Bold: t.Bold ?? false,
            OutlineColor: NullIfBlank(t.OutlineColor),
            OutlineWidth: Math.Clamp(t.OutlineWidth ?? 0, 0, MaxOutlineWidth),
            ShadowColor: NullIfBlank(t.ShadowColor),
            ShadowBlur: Math.Clamp(t.ShadowBlur ?? 4, 0, MaxShadow),
            ShadowDepth: Math.Clamp(t.ShadowDepth ?? 2, 0, MaxShadow),
            Align: NormalizeAlign(t.Align));

    /// <summary>Reads the "airplay" block out of its raw element, or null when it is absent or is
    /// not an object at all. Deliberately total: a wrong type there must not throw, for the same
    /// reason "title": 42 must not - a malformed decoration cannot be allowed to cost the skin.</summary>
    private static SkinAirPlayJson? ParseAirPlay(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } obj) return null;
        try
        {
            return obj.Deserialize<SkinAirPlayJson>(JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Loads the optional AirPlay bar. Returns the bar, or null plus the reason.
    ///
    /// Every failure here is SOFT. The volume bar has already been resolved and validated by the
    /// time this runs, and nothing this returns can invalidate it - a skin whose AirPlay artwork
    /// is half-written keeps working as the volume skin it has always been.</summary>
    private static (SkinAirPlay? Bar, string? Error) LoadAirPlay(string folder, JsonElement? raw)
    {
        var json = ParseAirPlay(raw);
        bool hasEmpty = File.Exists(Path.Combine(folder, "airplay-empty.png"))
                     || File.Exists(Path.Combine(folder, "airplay-empty.gif"));
        bool hasFull = File.Exists(Path.Combine(folder, "airplay-full.png"))
                    || File.Exists(Path.Combine(folder, "airplay-full.gif"));

        if (!hasEmpty && !hasFull)
        {
            // A block promising a bar the folder cannot draw. Not fatal, but the author wrote it
            // for a reason and should be told why nothing appeared.
            return raw is null
                ? (null, null)
                : (null, "skin.json declares an \"airplay\" block but the folder has no "
                       + "airplay-empty/airplay-full artwork.");
        }

        int emptyFrames = Math.Max(1, json?.EmptyFrames ?? 1);
        int fullFrames = Math.Max(1, json?.FullFrames ?? 1);

        var empty = ResolveLayer(folder, "airplay-empty", emptyFrames);
        if (empty.Error is not null) return (null, empty.Error);
        var full = ResolveLayer(folder, "airplay-full", fullFrames);
        if (full.Error is not null) return (null, full.Error);

        if (empty.LogicalWidth != full.LogicalWidth || empty.LogicalHeight != full.LogicalHeight)
        {
            return (null,
                $"airplay-full frame is {full.LogicalWidth}\u00d7{full.LogicalHeight} but "
                + $"airplay-empty frame is {empty.LogicalWidth}\u00d7{empty.LogicalHeight}");
        }

        int width = empty.LogicalWidth;
        int height = empty.LogicalHeight;

        int fillStart = Math.Clamp(json?.FillStartX ?? 0, 0, width);
        int fillEnd = Math.Clamp(json?.FillEndX ?? width, 0, width);
        if (fillStart >= fillEnd)
            return (null, $"airplay fillStartX ({fillStart}) must be less than fillEndX ({fillEnd})");

        // No declared region means the whole bar opens the dropdown, so a skin that only drops in
        // two PNGs still gets a usable control rather than a decorative strip.
        var hit = json?.DropdownHit is { } h
            ? ClampHit(h, width, height)
            : new SkinHitRect(0, 0, width, height);

        // The connect button is opt-in and has NO default region, which is the opposite of the
        // dropdown's. The dropdown falling back to the whole bar makes a two-PNG skin usable; a
        // connect button doing the same would put a hidden power switch under every pixel of every
        // skin written before this existed, and connecting by accident is not a small mistake.
        var connect = json?.ConnectHit is { } c ? ClampHit(c, width, height) : null;

        // A zero-area region is an authoring slip, not a control. Dropped rather than reported,
        // for the same reason everything else here fails soft: it costs the strip nothing.
        if (connect is { Width: 0 } or { Height: 0 }) connect = null;

        return (new SkinAirPlay(empty.Path, full.Path, width, height,
            empty.IsGif, full.IsGif,
            empty.IsGif ? 1 : emptyFrames, full.IsGif ? 1 : fullFrames,
            fillStart, fillEnd,
            ReadText(json?.NameText), ReadText(json?.PercentText), hit,
            connect, json?.ConnectColor), null);
    }

    /// <summary>Pulls a declared hit region inside the artwork it belongs to.</summary>
    private static SkinHitRect ClampHit(SkinHitJson h, int width, int height)
    {
        int x = Math.Clamp(h.X ?? 0, 0, width);
        int y = Math.Clamp(h.Y ?? 0, 0, height);
        int w = Math.Clamp(h.W ?? width, 0, width - x);
        int hh = Math.Clamp(h.H ?? height, 0, height - y);
        return new SkinHitRect(x, y, w, hh);
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

    /// <summary>The one string of a metadata element, or null when it is any other JSON kind.
    /// Wrong types are IGNORED rather than fatal: a nonsense credit field in a downloaded
    /// skin.json must not cost the user the artwork the file is actually for.</summary>
    private static string? JsonString(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    /// <summary>The string entries of a metadata array, skipping any that aren't strings; null
    /// when the value isn't an array at all. Same ignore-don't-throw policy as
    /// <see cref="JsonString"/>.</summary>
    private static IEnumerable<string>? JsonStrings(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Array } array)
            return null;
        return array.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
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

        // A JsonElement, not a SkinAirPlayJson, for the same reason the metadata fields are: a
        // wrong TYPE here ("airplay": 42) must not throw and take the whole skin down with it.
        public JsonElement? AirPlay { get; set; }

        // The metadata fields deserialize as raw JsonElements, unlike everything above, because
        // a wrong TYPE here must not throw: System.Text.Json would reject "title": 42 for a
        // string property and the whole skin would fail to load over a credit line. Kind checks
        // in JsonString/JsonStrings then keep only what is usable. (JsonElement values produced
        // by Deserialize are clones, so they outlive the document they came from.)
        public JsonElement? Title { get; set; }
        public JsonElement? Author { get; set; }
        public JsonElement? Description { get; set; }
        public JsonElement? Version { get; set; }
        public JsonElement? Tags { get; set; }
        public JsonElement? SourceUrl { get; set; }
    }

    private sealed class SkinAirPlayJson
    {
        public int? FillStartX { get; set; }
        public int? FillEndX { get; set; }
        public int? EmptyFrames { get; set; }
        public int? FullFrames { get; set; }
        public SkinTextJson? NameText { get; set; }
        public SkinTextJson? PercentText { get; set; }
        public SkinHitJson? DropdownHit { get; set; }
        public SkinHitJson? ConnectHit { get; set; }
        public string? ConnectColor { get; set; }
    }

    private sealed class SkinHitJson
    {
        public int? X { get; set; }
        public int? Y { get; set; }
        public int? W { get; set; }
        public int? H { get; set; }
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
