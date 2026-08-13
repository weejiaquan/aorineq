using System.Text.Json;
using System.Text.Json.Serialization;

namespace AorinEQ.Core;

/// <summary>The two HUD interaction modes, global rather than per widget.
///
/// In <see cref="Edit"/> the widget windows accept mouse input — drag to move, resize handles,
/// right-click for that widget's settings. In <see cref="Live"/> they carry WS_EX_TRANSPARENT and
/// are invisible to input entirely, so a click over one reaches the desktop underneath.
///
/// Without the split the widgets are either immovable or they eat desktop clicks. String constants
/// rather than an enum for the same reason <see cref="VolumeModes"/> is: the persisted json stays
/// human-readable and an unknown value normalizes gracefully.</summary>
public static class HudModes
{
    public const string Live = "live";
    public const string Edit = "edit";

    /// <summary>Anything unrecognised becomes <see cref="Live"/> — the mode in which the HUD
    /// cannot interfere with anything the user is doing.</summary>
    public static string Normalize(string? mode) => mode == Edit ? Edit : Live;
}

/// <summary>Which way a spectrum's bars run.</summary>
public static class HudOrientations
{
    public const string LeftToRight = "left-right";
    public const string RightToLeft = "right-left";
    public const string Vertical = "vertical";
    public const string Mirrored = "mirrored";

    public static readonly IReadOnlyList<string> All = [LeftToRight, RightToLeft, Vertical, Mirrored];

    public static string Normalize(string? value) => All.Contains(value) ? value! : LeftToRight;
}

/// <summary>The v1 widget set. Every one of them shows the USER'S OWN audio chain — that is the
/// whole point of the HUD over a generic desktop widget, and it is why nothing here is a clock or
/// a CPU meter.</summary>
public static class HudWidgetTypes
{
    /// <summary>FFT bars.</summary>
    public const string Spectrum = "spectrum";

    /// <summary>Per-channel peak + RMS in dBFS with a latching clip indicator.</summary>
    public const string Levels = "levels";

    /// <summary>Live response of the active scope, the same RBJ maths the editor draws.</summary>
    public const string EqCurve = "eqcurve";

    /// <summary>Current percent and dB, rendered THROUGH THE SKIN PIPELINE.</summary>
    public const string Volume = "volume";

    public static readonly IReadOnlyList<string> All = [Spectrum, Levels, EqCurve, Volume];

    public static bool IsType(string? type) => type is not null && All.Contains(type);

    /// <summary>Whether this widget needs the loopback capture running while it is visible.
    ///
    /// This is what the reference count is counted over. The EQ curve is drawn from the band chain
    /// and the volume widget from the volume state — both are redrawn when their source changes and
    /// neither reads a single sample, so a HUD showing only those two must not hold the capture
    /// open (and with it a thread, an event handle and a COM object) for nothing.</summary>
    public static bool ConsumesAudio(string? type) => type is Spectrum or Levels;

    /// <summary>The name shown in menus.</summary>
    public static string DisplayName(string type) => type switch
    {
        Spectrum => "Spectrum",
        Levels => "Levels",
        EqCurve => "EQ curve",
        Volume => "Volume",
        _ => type,
    };
}

/// <summary>One widget's persisted record: what it is, which screen it lives on, where and how big
/// it is, and its own style knobs.
///
/// The style knobs of every type share one record rather than living in a per-type payload. There
/// are four types and a dozen knobs; a polymorphic payload would buy type safety in exchange for a
/// custom converter, and a converter is exactly the sort of thing that turns a downloaded or
/// hand-edited hud.json into a thrown exception rather than a normalized value. Unused knobs
/// simply do not apply to a type.</summary>
public sealed record HudWidget
{
    public const int MinSize = 40;
    public const int MaxSize = 8000;
    public const int MinBands = 4;
    public const int MaxBands = 256;
    public const double MinHzLimit = 10;
    public const double MaxHzLimit = 24000;
    public const double MinOpacity = 0.05;
    public const double MinScale = 0.25;
    public const double MaxScale = 4.0;

    /// <summary>Stable identity, so a widget survives being reordered in the file.</summary>
    public string Id { get; init; } = "";

    public string Type { get; init; } = HudWidgetTypes.Spectrum;

    /// <summary>The DEVICE PATH of the screen this widget belongs to — never an index. An index
    /// changes when a display is added, removed or docked, and a widget that jumps because the
    /// user plugged in a monitor is a widget the user has to place again every day. Empty means
    /// "not placed yet": the primary screen, and NOT a fallback (see <see cref="HudPlacement"/>).</summary>
    public string MonitorId { get; init; } = "";

    /// <summary>Position and size within that monitor's WORK AREA, in physical pixels (see
    /// <see cref="HudRect"/> for why pixels). Relative to the work area rather than absolute for
    /// the same reason the identity is a path: the desktop's coordinate space is rearranged by
    /// docking, and the same numbers must keep meaning the same place on the same screen.</summary>
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; } = 320;
    public int Height { get; init; } = 120;

    /// <summary>Draw order among widgets. Higher is nearer the front.</summary>
    public int Z { get; init; }

    public bool Visible { get; init; } = true;

    /// <summary>Background opacity behind the widget's own drawing.</summary>
    public double Opacity { get; init; } = 0.55;

    // ---- spectrum ----
    public int BandCount { get; init; } = 32;
    public double MinHz { get; init; } = 20;
    public double MaxHz { get; init; } = 20000;

    /// <summary>0 = follow the input exactly, approaching 1 = a longer fall. Attack is always
    /// instant; only the release is smoothed.</summary>
    public double Smoothing { get; init; } = 0.6;

    public bool PeakHold { get; init; } = true;
    public double PeakDecayDbPerSecond { get; init; } = 24;
    public string Orientation { get; init; } = HudOrientations.LeftToRight;
    public int BarGap { get; init; } = 2;

    /// <summary>Bar colour. Equal start and end is a solid fill; different ones ramp between them.</summary>
    public string ColorStart { get; init; } = "#FF4FC3F7";
    public string ColorEnd { get; init; } = "#FF7E57C2";

    // ---- eq curve ----
    public bool ShowNodes { get; init; } = true;
    public bool ShowGrid { get; init; } = true;

    // ---- volume (skin-driven) ----
    /// <summary>Zoom applied to the skin's own logical size.</summary>
    public double Scale { get; init; } = 1.0;
    public bool ShowDeviceName { get; init; }

    /// <summary>A widget of <paramref name="type"/> with this build's defaults and a fresh id.</summary>
    public static HudWidget Create(string type)
    {
        var t = HudWidgetTypes.IsType(type) ? type : HudWidgetTypes.Spectrum;
        var w = new HudWidget { Id = NewId(), Type = t };
        return t switch
        {
            HudWidgetTypes.Spectrum => w with { Width = 360, Height = 140 },
            HudWidgetTypes.Levels => w with { Width = 150, Height = 190 },
            HudWidgetTypes.EqCurve => w with { Width = 360, Height = 180 },
            HudWidgetTypes.Volume => w with { Width = 260, Height = 90 },
            _ => w,
        };
    }

    internal static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>Clamps every field into the range this build can actually render. hud.json is
    /// written by dragging but it is also a plain file a user can edit, so nothing that comes back
    /// off disk is taken on trust.</summary>
    public HudWidget Normalize()
    {
        double minHz = Math.Clamp(double.IsFinite(MinHz) ? MinHz : 20, MinHzLimit, MaxHzLimit - 1);
        double maxHz = Math.Clamp(double.IsFinite(MaxHz) ? MaxHz : 20000, MinHzLimit + 1, MaxHzLimit);
        if (maxHz <= minHz)
            (minHz, maxHz) = (20, 20000);

        return this with
        {
            Id = string.IsNullOrWhiteSpace(Id) ? NewId() : Id.Trim(),
            Type = HudWidgetTypes.IsType(Type) ? Type : HudWidgetTypes.Spectrum,
            MonitorId = MonitorId ?? "",
            Width = Math.Clamp(Width, MinSize, MaxSize),
            Height = Math.Clamp(Height, MinSize, MaxSize),
            Opacity = Math.Clamp(double.IsFinite(Opacity) ? Opacity : 0.55, MinOpacity, 1.0),
            BandCount = Math.Clamp(BandCount, MinBands, MaxBands),
            MinHz = minHz,
            MaxHz = maxHz,
            Smoothing = Math.Clamp(double.IsFinite(Smoothing) ? Smoothing : 0.6, 0, 1),
            PeakDecayDbPerSecond = double.IsFinite(PeakDecayDbPerSecond) && PeakDecayDbPerSecond > 0
                ? Math.Min(PeakDecayDbPerSecond, 240)
                : 24,
            Orientation = HudOrientations.Normalize(Orientation),
            BarGap = Math.Clamp(BarGap, 0, 40),
            Scale = Math.Clamp(double.IsFinite(Scale) ? Scale : 1.0, MinScale, MaxScale),
        };
    }
}

/// <summary>The HUD's layout record, persisted to <c>%APPDATA%\AorinEQ\hud.json</c>.
///
/// DELIBERATELY NOT settings.json. This file is rewritten by DRAGGING: mixing it into the settings
/// record would make every drag a settings write, and settings.json carries the user's volume, EQ
/// chains and device map — state that has no business being rewritten sixty times while somebody
/// nudges a widget across a screen.</summary>
public sealed record HudLayout
{
    public const int DefaultFps = 30;
    public const int MinFps = 5;
    public const int MaxFps = 60;

    /// <summary>An upper bound on widgets, because each one is a real top-level window with a real
    /// render loop. A hand-edited (or corrupted) file must not open hundreds of them.</summary>
    public const int MaxWidgets = 24;

    public string Mode { get; init; } = HudModes.Live;

    /// <summary>Hide every widget while a fullscreen app has focus. DEFAULT ON: overlays over
    /// exclusive fullscreen either flicker or simply do not composite, and showing them there is
    /// worse than hiding them.</summary>
    public bool HideWhenFullscreen { get; init; } = true;

    /// <summary>Show the widgets only while audio is actually playing. Default OFF.</summary>
    public bool OnlyWhilePlaying { get; init; }

    /// <summary>Redraw cap, shared by every widget — there is ONE timer for the whole HUD.</summary>
    public int Fps { get; init; } = DefaultFps;

    public IReadOnlyList<HudWidget> Widgets { get; init; } = [];

    /// <summary>The default location: beside settings.json, not inside it.</summary>
    public static string DefaultPath => Path.Combine(ApoPaths.GetStateRoot(), "hud.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Reads and normalizes the layout. Never throws: a missing, locked or unparseable
    /// file yields the documented defaults, because a broken hud.json must cost the user their
    /// widget positions and nothing else.</summary>
    public static HudLayout Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new HudLayout();
            var raw = JsonSerializer.Deserialize<HudLayout>(File.ReadAllText(path), JsonOptions);
            return raw is null ? new HudLayout() : raw.Normalize();
        }
        catch (JsonException) { return new HudLayout(); }
        catch (IOException) { return new HudLayout(); }
        catch (UnauthorizedAccessException) { return new HudLayout(); }
    }

    public HudLayout Normalize()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var widgets = new List<HudWidget>();
        foreach (var raw in Widgets ?? [])
        {
            if (raw is null) continue;
            // An unknown TYPE is dropped rather than coerced: there is nothing that can render it,
            // and coercing it would silently turn a widget the user does not have into one they
            // never asked for, in a file that is then written back.
            if (!HudWidgetTypes.IsType(raw.Type)) continue;
            var w = raw.Normalize();
            if (!seen.Add(w.Id))
            {
                w = w with { Id = HudWidget.NewId() };
                seen.Add(w.Id);
            }
            widgets.Add(w);
            if (widgets.Count == MaxWidgets) break;
        }

        return this with
        {
            Mode = HudModes.Normalize(Mode),
            Fps = Math.Clamp(Fps, MinFps, MaxFps),
            Widgets = widgets,
        };
    }

    /// <summary>Writes the layout with the temp + rename discipline ApoWriter uses, so a crash
    /// mid-write can never leave a half-written file where the layout used to be. Coalescing —
    /// so a drag does not write per frame — belongs to the caller (see <see cref="HudStore"/>).</summary>
    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    public HudWidget? Find(string id) => Widgets.FirstOrDefault(w => w.Id == id);

    /// <summary>The layout with one widget replaced by <paramref name="updated"/>, matched by id.
    /// An id this layout does not hold is a no-op rather than an append — the caller has a stale
    /// reference, and appending it would resurrect a widget the user deleted.</summary>
    public HudLayout With(HudWidget updated) =>
        Widgets.Any(w => w.Id == updated.Id)
            ? this with { Widgets = Widgets.Select(w => w.Id == updated.Id ? updated : w).ToList() }
            : this;

    public HudLayout Add(HudWidget widget) =>
        Widgets.Count >= MaxWidgets
            ? this
            : this with { Widgets = [.. Widgets, widget with { Z = NextZ() }] };

    public HudLayout Remove(string id) =>
        this with { Widgets = Widgets.Where(w => w.Id != id).ToList() };

    private int NextZ() => Widgets.Count == 0 ? 0 : Widgets.Max(w => w.Z) + 1;

    /// <summary>Whether anything visible needs the loopback capture. The HUD's whole registration
    /// with the shared pipeline is decided by this one predicate.</summary>
    public bool NeedsAudio() =>
        Widgets.Any(w => w.Visible && HudWidgetTypes.ConsumesAudio(w.Type));
}
