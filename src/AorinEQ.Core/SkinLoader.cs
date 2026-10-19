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
    string Align = "left")
{
    /// <summary>A hidden text that carries nothing else. The writer omits it, so it is the same
    /// skin as no text at all; a hidden text with any other field set is written with
    /// "show": false and keeps its position and styling.</summary>
    public static SkinText Hidden { get; } = new(false, 0, 0);
}

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

/// <summary>Where the device-changed glyph sits on a notice and how it is drawn. A null Color
/// means "draw it in the text colour".</summary>
public sealed record SkinNoticeGlyph(bool Show, int X, int Y, double Size, string? Color);

/// <summary>The optional look of the device-changed notice: a plate (colours, corner, padding)
/// that a skin may replace wholesale with notice art. When ArtPath is set, Width/Height are the
/// art's logical frame size and X/Y on Text and Glyph are positions inside it; TextWidth bounds
/// the line so a long device name cannot run off the art.</summary>
public sealed record SkinNotice(
    string Background, string? Border, double CornerRadius, double PadX, double PadY,
    SkinText Text, SkinNoticeGlyph Glyph,
    string? ArtPath = null, bool ArtIsGif = false, int ArtFrames = 1,
    int Width = 0, int Height = 0, int TextWidth = 0)
{
    public bool HasArt => ArtPath is not null;
}

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
    SkinMeta Meta, string? Error, SkinAirPlay? AirPlay = null, string? AirPlayError = null,
    SkinNotice? Notice = null, string? NoticeError = null)
{
    public bool IsValid => Error is null;

    /// <summary>Whether this skin draws its own AirPlay bar. False for every skin written before
    /// the bar existed, and false for one whose AirPlay artwork did not load - both of which get
    /// the app's native AirPlay bar instead, so neither is a broken experience.</summary>
    public bool HasAirPlay => AirPlay is not null;

    /// <summary>Whether the skin describes its own device-changed notice. False for every skin
    /// written before the notice was themeable; those get the default dark pill.</summary>
    public bool HasNotice => Notice is not null;

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
        double scale = SkinClamps.DefaultScale;
        double fps = SkinClamps.DefaultFps;
        int emptyFrames = 1;
        int fullFrames = 1;
        int mutedFrames = 1;
        double mutedDim = SkinClamps.DefaultMutedDim;
        int? fillStartJson = null;
        int? fillEndJson = null;
        SkinMeta meta = SkinMeta.None;

        SkinInfo Bad(string error) => new(name, folder, emptyPath, fullPath, 0, 0, text, scale,
            fps, 1, 1, false, false, 0, 0, null, false, 1, SkinClamps.DefaultMutedDim, meta, error);

        try
        {
            name = new DirectoryInfo(folder).Name;
            if (!Directory.Exists(folder))
                return Bad($"Skin folder not found: {folder}");

            // skin.json is parsed before the images: declared sheet frame counts are needed to
            // validate sheet geometry below.
            JsonElement? airPlayJson = null;
            JsonElement? noticeJson = null;
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
                    scale = SkinClamps.Scale(rawScale);
                if (parsed?.Fps is { } rawFps)
                    fps = SkinClamps.Fps(rawFps);
                if (parsed?.EmptyFrames is { } ef)
                    emptyFrames = SkinClamps.Frames(ef);
                if (parsed?.FullFrames is { } ff)
                    fullFrames = SkinClamps.Frames(ff);
                if (parsed?.MutedFrames is { } mf)
                    mutedFrames = SkinClamps.Frames(mf);
                if (parsed?.MutedDim is { } dim)
                    mutedDim = SkinClamps.MutedDim(dim);
                fillStartJson = parsed?.FillStartX;
                fillEndJson = parsed?.FillEndX;
                airPlayJson = parsed?.AirPlay;
                noticeJson = parsed?.Notice;
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

            if (FrameMismatch(Path.GetFileName(full.Path), full, Path.GetFileName(empty.Path), empty) is { } fullMismatch)
                return Bad(fullMismatch);

            // Optional muted layer: same resolution and logical-frame-size rules as empty/full,
            // just never required — absence means "dim the empty layer by mutedDim" instead.
            string? mutedPath = null;
            bool mutedIsGif = false;
            if (File.Exists(Path.Combine(folder, "muted.png")) || File.Exists(Path.Combine(folder, "muted.gif")))
            {
                var muted = ResolveLayer(folder, "muted", mutedFrames);
                if (muted.Error is not null)
                    return Bad(muted.Error);
                if (FrameMismatch(Path.GetFileName(muted.Path), muted, Path.GetFileName(empty.Path), empty) is { } mutedMismatch)
                    return Bad(mutedMismatch);
                mutedPath = muted.Path;
                mutedIsGif = muted.IsGif;
            }

            // The AirPlay bar is resolved LAST and can only ever fail soft. A half-finished or
            // mismatched AirPlay layer must not cost somebody the volume OSD they actually use -
            // it loses its own bar, says why, and the app draws its native AirPlay bar instead.
            var (airPlay, airPlayError) = LoadAirPlay(folder, airPlayJson);
            var (notice, noticeError) = LoadNotice(folder, noticeJson);

            // Fill range: where the bar actually lives inside the (possibly wider, decorative)
            // image. Defaults to the full width; values clamp into the image, but an inverted or
            // empty range is an authoring error worth surfacing rather than guessing around.
            int fillStart = SkinClamps.FillStart(fillStartJson, empty.LogicalWidth);
            int fillEnd = SkinClamps.FillEnd(fillEndJson, empty.LogicalWidth);
            if (FillRangeError("", fillStart, fillEnd) is { } fillError)
                return Bad(fillError);

            return new SkinInfo(name, folder, empty.Path, full.Path,
                empty.LogicalWidth, empty.LogicalHeight, text, scale,
                fps, empty.IsGif ? 1 : emptyFrames, full.IsGif ? 1 : fullFrames,
                empty.IsGif, full.IsGif, fillStart, fillEnd,
                mutedPath, mutedIsGif, mutedPath is null || mutedIsGif ? 1 : mutedFrames, mutedDim,
                meta, null, airPlay, airPlayError, notice, noticeError);
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
        t is null ? null : SkinClamps.Text(new SkinText(t.Show, t.X, t.Y,
            Color: t.Color ?? "",
            FontFamily: t.FontFamily ?? "",
            FontSize: t.FontSize ?? SkinClamps.DefaultFontSize,
            Bold: t.Bold ?? false,
            OutlineColor: t.OutlineColor,
            OutlineWidth: t.OutlineWidth ?? 0,
            ShadowColor: t.ShadowColor,
            ShadowBlur: t.ShadowBlur ?? SkinClamps.DefaultShadowBlur,
            ShadowDepth: t.ShadowDepth ?? SkinClamps.DefaultShadowDepth,
            Align: t.Align ?? ""));

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

        int emptyFrames = SkinClamps.Frames(json?.EmptyFrames ?? 1);
        int fullFrames = SkinClamps.Frames(json?.FullFrames ?? 1);

        var empty = ResolveLayer(folder, "airplay-empty", emptyFrames);
        if (empty.Error is not null) return (null, empty.Error);
        var full = ResolveLayer(folder, "airplay-full", fullFrames);
        if (full.Error is not null) return (null, full.Error);

        if (FrameMismatch("airplay-full", full, "airplay-empty", empty) is { } stripMismatch)
            return (null, stripMismatch);

        int width = empty.LogicalWidth;
        int height = empty.LogicalHeight;

        int fillStart = SkinClamps.FillStart(json?.FillStartX, width);
        int fillEnd = SkinClamps.FillEnd(json?.FillEndX, width);
        if (FillRangeError("airplay ", fillStart, fillEnd) is { } stripFill)
            return (null, stripFill);

        // No declared region means the whole bar opens the dropdown, so a skin that only drops in
        // two PNGs still gets a usable control rather than a decorative strip.
        var hit = json?.DropdownHit is { } h
            ? SkinClamps.Hit(h.X, h.Y, h.W, h.H, width, height)
            : new SkinHitRect(0, 0, width, height);

        // The connect button is opt-in and has NO default region, which is the opposite of the
        // dropdown's. The dropdown falling back to the whole bar makes a two-PNG skin usable; a
        // connect button doing the same would put a hidden power switch under every pixel of every
        // skin written before this existed, and connecting by accident is not a small mistake.
        // A zero-area region is an authoring slip and is dropped (OptionalHit), failing soft.
        var connect = json?.ConnectHit is { } c
            ? SkinClamps.OptionalHit(SkinClamps.Hit(c.X, c.Y, c.W, c.H, width, height), width, height)
            : null;

        return (new SkinAirPlay(empty.Path, full.Path, width, height,
            empty.IsGif, full.IsGif,
            empty.IsGif ? 1 : emptyFrames, full.IsGif ? 1 : fullFrames,
            fillStart, fillEnd,
            ReadText(json?.NameText), ReadText(json?.PercentText), hit,
            connect, json?.ConnectColor), null);
    }

    /// <summary>The dark-pill plate: what a notice looks like when nothing says otherwise.</summary>
    public static readonly SkinNotice DefaultNotice = new(
        "#F2202020", null, 12, 16, 10,
        new SkinText(true, 0, 0, "#FFFFFFFF", "Segoe UI", 14, Bold: false),
        new SkinNoticeGlyph(true, 16, 0, 18, null));

    private static SkinNoticeJson? ParseNotice(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } obj) return null;
        try { return obj.Deserialize<SkinNoticeJson>(JsonOptions); }
        catch (JsonException) { return null; }
    }

    /// <summary>Loads the optional notice look. Soft like the AirPlay bar: nothing here can
    /// invalidate the volume skin, and every failure falls back to the default plate.</summary>
    private static (SkinNotice? Notice, string? Error) LoadNotice(string folder, JsonElement? raw)
    {
        var json = ParseNotice(raw);
        bool hasArt = File.Exists(Path.Combine(folder, "notice.png"))
                   || File.Exists(Path.Combine(folder, "notice.gif"));
        if (json is null && !hasArt) return (null, null);

        var d = DefaultNotice;
        var plate = ClampNotice(d with
        {
            Background = SkinClamps.OrDefault(json?.Background, d.Background),
            Border = SkinClamps.NullIfBlank(json?.Border),
            CornerRadius = json?.CornerRadius ?? d.CornerRadius,
            PadX = json?.Padding?.X ?? d.PadX,
            PadY = json?.Padding?.Y ?? d.PadY,
            Text = new SkinText(true, json?.Text?.X ?? 0, json?.Text?.Y ?? 0,
                Color: SkinClamps.OrDefault(json?.Text?.Color, d.Text.Color),
                FontFamily: SkinClamps.OrDefault(json?.Text?.FontFamily, d.Text.FontFamily),
                FontSize: json?.Text?.FontSize ?? d.Text.FontSize,
                Bold: json?.Text?.Bold ?? d.Text.Bold,
                Align: SkinClamps.Align(json?.Text?.Align)),
            Glyph = new SkinNoticeGlyph(json?.Glyph?.Show ?? true, json?.Glyph?.X ?? d.Glyph.X, 0,
                json?.Glyph?.Size ?? d.Glyph.Size, SkinClamps.NullIfBlank(json?.Glyph?.Color)),
        });
        if (!hasArt) return (plate, null);

        int frames = SkinClamps.Frames(json?.Frames ?? 1);
        var art = ResolveLayer(folder, "notice", frames);
        if (art.Error is not null) return (null, art.Error);

        return (PlaceNoticeArt(plate, art.Path, art.IsGif, art.IsGif ? 1 : frames,
            art.LogicalWidth, art.LogicalHeight,
            json?.Text?.X, json?.Text?.Y, json?.Glyph?.X, json?.Glyph?.Y, json?.TextWidth), null);
    }

    /// <summary>A notice's plate values pulled into range: corner radius and padding 0..64, text
    /// and glyph sizes 4..200. The loader and the Skin Designer both go through here, so what the
    /// designer previews is what the saved skin loads as.</summary>
    public static SkinNotice ClampNotice(SkinNotice look) => look with
    {
        CornerRadius = SkinClamps.CornerRadius(look.CornerRadius),
        PadX = SkinClamps.Padding(look.PadX),
        PadY = SkinClamps.Padding(look.PadY),
        Text = look.Text with { FontSize = SkinClamps.FontSize(look.Text.FontSize) },
        Glyph = look.Glyph with { Size = SkinClamps.FontSize(look.Glyph.Size) },
    };

    /// <summary>Puts a (clamped) plate on notice art of <paramref name="width"/> x
    /// <paramref name="height"/> logical pixels. Positions left null take the defaults, which centre
    /// the glyph and the text line vertically (1.33 is the line-box-to-em ratio the AirPlay power
    /// glyph is centred with as well); every position is clamped into the art, and the text width
    /// into what is left of it right of the text.</summary>
    public static SkinNotice PlaceNoticeArt(SkinNotice plate, string artPath, bool artIsGif, int artFrames,
        int width, int height, int? textX, int? textY, int? glyphX, int? glyphY, int? textWidth)
    {
        int w = width, h = height;
        int tx = Math.Clamp(textX ?? 48, 0, w);
        int ty = Math.Clamp(textY ?? (h - (int)Math.Round(plate.Text.FontSize * SkinClamps.LineHeightRatio)) / 2, 0, h);
        int gx = Math.Clamp(glyphX ?? 16, 0, w);
        int gy = Math.Clamp(glyphY ?? (int)((h - plate.Glyph.Size) / 2), 0, h);
        int tw = Math.Clamp(textWidth ?? w - tx - 12, 0, w - tx);

        return plate with
        {
            Text = plate.Text with { X = tx, Y = ty },
            Glyph = plate.Glyph with { X = gx, Y = gy },
            ArtPath = artPath, ArtIsGif = artIsGif, ArtFrames = artFrames,
            Width = w, Height = h, TextWidth = tw,
        };
    }

    /// <summary>Resolves one layer: .png wins over .gif, and the header is judged by
    /// <see cref="ImageProbe.Evaluate"/> - the same verdict, in the same words, the designer gets.</summary>
    private static LayerProbe ResolveLayer(string folder, string layer, int declaredFrames)
    {
        string pngPath = Path.Combine(folder, layer + ".png");
        string gifPath = Path.Combine(folder, layer + ".gif");
        if (File.Exists(pngPath))
            return ImageProbe.Evaluate(pngPath, layer, declaredFrames, PngHeader.Read(pngPath));
        if (File.Exists(gifPath))
            return ImageProbe.Evaluate(gifPath, layer, declaredFrames, GifHeader.Read(gifPath));
        return new LayerProbe(pngPath, false, 0, 0, LayerProblem.NotFound, $"{layer}.png or {layer}.gif not found");
    }

    /// <summary>Whether two layers have the same logical frame size.</summary>
    internal static bool SameFrame(LayerProbe a, LayerProbe b) =>
        a.LogicalWidth == b.LogicalWidth && a.LogicalHeight == b.LogicalHeight;

    /// <summary>Null when <paramref name="layer"/>'s frame matches <paramref name="reference"/>'s,
    /// else the reason, naming each by its label. The loader and the Skin Designer's document both
    /// report a mismatch through here, so they say the same thing.</summary>
    internal static string? FrameMismatch(string label, LayerProbe layer, string referenceLabel, LayerProbe reference) =>
        SameFrame(layer, reference)
            ? null
            : $"{label} frame is {layer.LogicalWidth}\u00d7{layer.LogicalHeight} " +
              $"but {referenceLabel} frame is {reference.LogicalWidth}\u00d7{reference.LogicalHeight}";

    /// <summary>Null for a usable fill range, else the reason. <paramref name="prefix"/> is "" for
    /// the volume bar and "airplay " for the strip, as the messages have always read.</summary>
    internal static string? FillRangeError(string prefix, int start, int end) =>
        start < end ? null : $"{prefix}fillStartX ({start}) must be less than fillEndX ({end})";

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

        // Raw for the same reason: "notice": 42 must not throw and cost the skin.
        public JsonElement? Notice { get; set; }

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

    private sealed class SkinNoticeJson
    {
        public string? Background { get; set; }
        public string? Border { get; set; }
        public double? CornerRadius { get; set; }
        public SkinPadJson? Padding { get; set; }
        public SkinNoticeTextJson? Text { get; set; }
        public int? TextWidth { get; set; }
        public SkinNoticeGlyphJson? Glyph { get; set; }
        public int? Frames { get; set; }
    }
    private sealed class SkinPadJson { public double? X { get; set; } public double? Y { get; set; } }
    private sealed class SkinNoticeTextJson
    {
        public int? X { get; set; } public int? Y { get; set; }
        public string? Color { get; set; } public string? FontFamily { get; set; }
        public double? FontSize { get; set; } public bool? Bold { get; set; } public string? Align { get; set; }
    }
    private sealed class SkinNoticeGlyphJson
    {
        public bool? Show { get; set; } public int? X { get; set; } public int? Y { get; set; }
        public double? Size { get; set; } public string? Color { get; set; }
    }
}
