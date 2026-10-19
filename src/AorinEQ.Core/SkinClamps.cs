namespace AorinEQ.Core;

/// <summary>Every range a skin value is pulled into, written once.
///
/// <see cref="SkinLoader"/> applies these to what it reads from skin.json and the Skin Designer's
/// <c>SkinEdits</c> apply them to what the user changes, so an edited value is exactly what a save
/// and a reload give back - the designer cannot preview a value the saved skin will not draw.
///
/// A non-finite number (NaN, ±Infinity) takes the field's default rather than passing through:
/// Math.Clamp lets NaN straight out, and a NaN font size is an exception the first time WPF lays
/// the text out.</summary>
public static class SkinClamps
{
    public const double MinScale = 0.25;
    public const double MaxScale = 4.0;
    public const double DefaultScale = 1.0;
    public const double MinFps = 1.0;
    public const double MaxFps = 60.0;
    public const double DefaultFps = 10.0;
    public const double MinFontSize = 4.0;
    public const double MaxFontSize = 200.0;
    public const double DefaultFontSize = 14.0;
    public const double MaxOutlineWidth = 20.0;
    public const double MaxShadow = 50.0;
    public const double DefaultShadowBlur = 4.0;
    public const double DefaultShadowDepth = 2.0;
    public const double DefaultMutedDim = 0.6; // the pre-1.8 hardcoded mute dim opacity
    public const double MaxCornerRadius = 64;
    public const double MaxPadding = 64;
    public const int MinWeight = 100;
    public const int MaxWeight = 900;
    public const double MinLetterSpacing = -20;
    public const double MaxLetterSpacing = 100;

    /// <summary>The OpenType weight a text has when its skin names none - each text's look from
    /// before weights could be chosen: SemiBold for the percent number and the notice name,
    /// Normal for the AirPlay labels.</summary>
    public const int DefaultWeight = 600;
    public const int DefaultLabelWeight = 400;

    /// <summary>What the legacy "bold": true reads as.</summary>
    public const int BoldWeight = 700;

    /// <summary>The line box of a text line as a multiple of its em size: what the loader centres
    /// notice text and the AirPlay power glyph with, and what the designer outlines text with.</summary>
    public const double LineHeightRatio = 1.33;

    public const string DefaultTextColor = "#FFFFFFFF";
    public const string DefaultFontFamily = "Segoe UI";

    /// <summary>What an outline or a shadow starts as when the designer switches it on.</summary>
    public const string DefaultOutlineColor = "#FF000000";
    public const double DefaultOutlineWidth = 2.0;
    public const string DefaultShadowColor = "#80000000";

    public static double Scale(double value) => Clamp(value, MinScale, MaxScale, DefaultScale);

    public static double Fps(double value) => Clamp(value, MinFps, MaxFps, DefaultFps);

    public static int Frames(int value) => Math.Max(1, value);

    public static double MutedDim(double value) => Clamp(value, 0.0, 1.0, DefaultMutedDim);

    public static double FontSize(double value) => Clamp(value, MinFontSize, MaxFontSize, DefaultFontSize);

    public static double OutlineWidth(double value) => Clamp(value, 0, MaxOutlineWidth, 0);

    public static double ShadowBlur(double value) => Clamp(value, 0, MaxShadow, DefaultShadowBlur);

    public static double ShadowDepth(double value) => Clamp(value, 0, MaxShadow, DefaultShadowDepth);

    /// <summary>A weight on the OpenType scale: rounded to the nearest 100 (half away from zero)
    /// and pulled into 100..900.</summary>
    public static int Weight(int value) =>
        Math.Clamp((int)Math.Round(value / 100.0, MidpointRounding.AwayFromZero) * 100, MinWeight, MaxWeight);

    public static double LetterSpacing(double value) => Clamp(value, MinLetterSpacing, MaxLetterSpacing, 0);

    public static double CornerRadius(double value) => Clamp(value, 0, MaxCornerRadius, 0);

    public static double Padding(double value) => Clamp(value, 0, MaxPadding, 0);

    /// <summary>Where 0 % sits: the declared x pulled into the image, the image's left edge when
    /// none is declared.</summary>
    public static int FillStart(int? value, int width) => Math.Clamp(value ?? 0, 0, Math.Max(0, width));

    /// <summary>Where 100 % sits: the declared x pulled into the image, the image's right edge when
    /// none is declared.</summary>
    public static int FillEnd(int? value, int width) => Math.Clamp(value ?? width, 0, Math.Max(0, width));

    /// <summary>A declared region pulled inside the artwork it belongs to; any side left undeclared
    /// takes the whole area. A region running off the artwork is unclickable at its far end, and an
    /// author cannot see that from the JSON.</summary>
    public static SkinHitRect Hit(int? x, int? y, int? width, int? height, int areaWidth, int areaHeight)
    {
        int w = Math.Max(0, areaWidth), h = Math.Max(0, areaHeight);
        int cx = Math.Clamp(x ?? 0, 0, w);
        int cy = Math.Clamp(y ?? 0, 0, h);
        int cw = Math.Clamp(width ?? w, 0, w - cx);
        int ch = Math.Clamp(height ?? h, 0, h - cy);
        return new SkinHitRect(cx, cy, cw, ch);
    }

    public static SkinHitRect Hit(SkinHitRect rect, int areaWidth, int areaHeight) =>
        Hit(rect.X, rect.Y, rect.Width, rect.Height, areaWidth, areaHeight);

    /// <summary><see cref="Hit(SkinHitRect,int,int)"/> for an opt-in region (the AirPlay power
    /// button): one that clamps to zero area is an authoring slip, not a control, and is dropped.</summary>
    public static SkinHitRect? OptionalHit(SkinHitRect rect, int areaWidth, int areaHeight)
    {
        var clamped = Hit(rect, areaWidth, areaHeight);
        return clamped.Width == 0 || clamped.Height == 0 ? null : clamped;
    }

    /// <summary>Case-insensitive align; anything but center/right reads as left, the historical
    /// anchor semantics of x.</summary>
    public static string Align(string? value)
    {
        var v = value?.Trim().ToLowerInvariant();
        return v is "center" or "right" ? v : "left";
    }

    /// <summary>One text overlay with every blank defaulted and every number clamped. The volume
    /// bar's percent text, both AirPlay labels and the notice name share it.</summary>
    public static SkinText Text(SkinText text) => text with
    {
        Color = OrDefault(text.Color, DefaultTextColor),
        FontFamily = OrDefault(text.FontFamily, DefaultFontFamily),
        FontSize = FontSize(text.FontSize),
        Weight = Weight(text.Weight),
        LetterSpacing = LetterSpacing(text.LetterSpacing),
        OutlineColor = NullIfBlank(text.OutlineColor),
        OutlineWidth = OutlineWidth(text.OutlineWidth),
        ShadowColor = NullIfBlank(text.ShadowColor),
        ShadowBlur = ShadowBlur(text.ShadowBlur),
        ShadowDepth = ShadowDepth(text.ShadowDepth),
        Align = Align(text.Align),
    };

    /// <summary><see cref="Text"/>, except that a text which clamps to <paramref name="hidden"/> -
    /// <see cref="SkinText.Hidden"/>, or <see cref="SkinText.HiddenLabel"/> for an AirPlay label -
    /// is no text: the writer omits it and the loader reads it back as null, so the document must
    /// hold null too, or a save and a reload would disagree.</summary>
    public static SkinText? OptionalText(SkinText? text, SkinText hidden)
    {
        if (text is null) return null;
        var clamped = Text(text);
        return clamped == hidden ? null : clamped;
    }

    internal static string OrDefault(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    internal static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
