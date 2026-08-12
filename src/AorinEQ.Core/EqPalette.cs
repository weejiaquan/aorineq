using System.Drawing;

namespace AorinEQ.Core;

/// <summary>The colours the equalizer editor's custom-drawn surfaces use — the response plot, the
/// band nodes, the output meters and the spectrum.
///
/// These cannot come from the Fluent theme dictionaries the rest of the app uses. A theme brush
/// says "this is a card background" or "this is secondary text"; a plot needs "this is the curve",
/// "this is the 0 dB line", "this is a clipping meter", and those have to stay distinguishable from
/// each other AND readable against the plot surface. So the palette is authored, once, for each
/// theme — and the pairing is checked by a contrast test rather than by eye, because the failure
/// mode this exists to prevent (an instrument panel that was tuned for dark and is unreadable in
/// light) is exactly the kind that survives a quick look.
///
/// The plot keeps its own surface colour rather than adopting the window's: it is an instrument
/// readout, and every audio tool draws one as a distinct panel. What changes with the theme is
/// which way round that panel is.</summary>
public sealed record EqPalette(
    /// <summary>The plot canvas itself, and the surrounding instrument panels.</summary>
    Color PlotBackground,
    Color PanelBackground,
    /// <summary>Decade grid lines, and the heavier 0 dB line across them.</summary>
    Color Grid,
    Color ZeroLine,
    /// <summary>Axis numbers and the corner scale label — the dimmest thing that must still be
    /// readable, so it is what the contrast test is strictest about.</summary>
    Color AxisText,
    /// <summary>Body text on the panels, and the dimmer labels beside it.</summary>
    Color Text,
    Color TextDim,
    /// <summary>The summed response curve, and the translucent fill under a single band's own
    /// response.</summary>
    Color Curve,
    Color BandFill,
    Color BandSelectedFill,
    /// <summary>Draggable band nodes: normal, selected, and the outline that keeps either
    /// readable where it crosses the curve.</summary>
    Color Node,
    Color NodeSelected,
    Color NodeStroke,
    /// <summary>Live spectrum trace, drawn as a translucent fill BEHIND the curve — kept
    /// deliberately quiet so it reads as context rather than competing with the response it
    /// sits under.</summary>
    Color Spectrum,
    /// <summary>Output meters: the track they sit in, the RMS bar, and the peak tick.</summary>
    Color MeterTrack,
    Color MeterRms,
    Color MeterPeak,
    /// <summary>The clip indicator, idle and latched.</summary>
    Color ClipIdle,
    Color ClipIdleText,
    Color ClipLatched,
    Color ClipLatchedText)
{
    /// <summary>The palette for the current Windows theme. The only entry point — the editor never
    /// picks a colour itself.</summary>
    public static EqPalette For(bool light) => light ? Light : Dark;

    /// <summary>The original instrument panel: a near-black plot the app has drawn since v2.0.0,
    /// unchanged so a dark-theme user sees exactly the editor they already know.</summary>
    public static readonly EqPalette Dark = new(
        PlotBackground: Rgb(0x15, 0x15, 0x1A),
        PanelBackground: Rgb(0x1E, 0x1E, 0x24),
        Grid: Rgb(0x2E, 0x2E, 0x38),
        ZeroLine: Rgb(0x4A, 0x4A, 0x58),
        AxisText: Rgb(0x8A, 0x8A, 0x98),
        Text: Rgb(0xDD, 0xDD, 0xDD),
        TextDim: Rgb(0x99, 0x99, 0xA5),
        Curve: Rgb(0x6F, 0xA8, 0xFF),
        BandFill: Argb(80, 0x8C, 0xAA, 0xFF),
        BandSelectedFill: Argb(150, 0xFF, 0xC8, 0x5A),
        Node: Rgb(0x6F, 0xA8, 0xFF),
        NodeSelected: Rgb(0xFF, 0xC8, 0x5A),
        NodeStroke: Rgb(0xFF, 0xFF, 0xFF),
        Spectrum: Argb(64, 0x78, 0xC8, 0xFF),
        MeterTrack: Rgb(0x26, 0x26, 0x2E),
        MeterRms: Rgb(0x4C, 0x9F, 0x6E),
        MeterPeak: Rgb(0xC8, 0xE6, 0xC9),
        ClipIdle: Rgb(0x33, 0x33, 0x33),
        ClipIdleText: Rgb(0x9C, 0x9C, 0x9C),
        ClipLatched: Rgb(0xB2, 0x22, 0x22),
        ClipLatchedText: Rgb(0xFF, 0xFF, 0xFF));

    /// <summary>The light-theme instrument panel. Not the dark one inverted: the curve and node
    /// colours are DARKENED rather than flipped, because a mid blue that reads well on near-black
    /// washes out on near-white, and the meter green has to stay green to keep meaning what it
    /// means.</summary>
    public static readonly EqPalette Light = new(
        PlotBackground: Rgb(0xFA, 0xFA, 0xFC),
        PanelBackground: Rgb(0xF0, 0xF0, 0xF4),
        Grid: Rgb(0xD2, 0xD2, 0xDA),
        ZeroLine: Rgb(0x9A, 0x9A, 0xA8),
        AxisText: Rgb(0x5A, 0x5A, 0x68),
        Text: Rgb(0x1A, 0x1A, 0x1E),
        TextDim: Rgb(0x55, 0x55, 0x60),
        Curve: Rgb(0x1A, 0x56, 0xC4),
        BandFill: Argb(70, 0x1A, 0x56, 0xC4),
        BandSelectedFill: Argb(120, 0xC8, 0x7A, 0x00),
        Node: Rgb(0x1A, 0x56, 0xC4),
        NodeSelected: Rgb(0xB0, 0x6A, 0x00),
        NodeStroke: Rgb(0x1A, 0x1A, 0x1E),
        Spectrum: Argb(96, 0x2A, 0x76, 0xC0),
        MeterTrack: Rgb(0xDC, 0xDC, 0xE2),
        MeterRms: Rgb(0x2F, 0x7D, 0x4F),
        MeterPeak: Rgb(0x1B, 0x4D, 0x2F),
        ClipIdle: Rgb(0xDC, 0xDC, 0xE2),
        ClipIdleText: Rgb(0x55, 0x55, 0x60),
        ClipLatched: Rgb(0xB2, 0x22, 0x22),
        ClipLatchedText: Rgb(0xFF, 0xFF, 0xFF));

    private static Color Rgb(int r, int g, int b) => Color.FromArgb(255, r, g, b);

    private static Color Argb(int a, int r, int g, int b) => Color.FromArgb(a, r, g, b);

    /// <summary>WCAG relative luminance. Used by the contrast checks — the sRGB channel values are
    /// linearised before weighting, which is why a naive average of the bytes gives a different
    /// (and wrong) answer for saturated colours like the meter green.</summary>
    public static double RelativeLuminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    /// <summary>WCAG contrast ratio, 1.0 (identical) to 21.0 (black on white). Colours with alpha
    /// are composited over <paramref name="background"/> first, so a translucent band fill is
    /// measured as it will actually be seen.</summary>
    public static double ContrastRatio(Color foreground, Color background)
    {
        var flat = Flatten(foreground, background);
        double a = RelativeLuminance(flat), b = RelativeLuminance(background);
        (double hi, double lo) = a >= b ? (a, b) : (b, a);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>Perceptual distance between two colours, 0 (identical) to ~765.
    ///
    /// Contrast ratio is the wrong tool for "can the user tell these two apart": it measures only
    /// LIGHTNESS, so the editor's blue node and amber selected node — obviously different to look
    /// at — score barely above 1.5:1. This is the low-cost redmean approximation of CIE76, which
    /// does account for hue.</summary>
    public static double Distance(Color a, Color b)
    {
        double rMean = (a.R + b.R) / 2.0;
        double dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return Math.Sqrt(
            (2 + rMean / 256) * dr * dr + 4 * dg * dg + (2 + (255 - rMean) / 256) * db * db);
    }

    /// <summary>Source-over composite of a possibly translucent colour onto an opaque one.</summary>
    public static Color Flatten(Color foreground, Color background)
    {
        if (foreground.A == 255) return foreground;
        double a = foreground.A / 255.0;
        static byte Mix(double a, byte f, byte b) => (byte)Math.Round(f * a + b * (1 - a));
        return Color.FromArgb(255,
            Mix(a, foreground.R, background.R),
            Mix(a, foreground.G, background.G),
            Mix(a, foreground.B, background.B));
    }
}
