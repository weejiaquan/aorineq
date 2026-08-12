using System.Drawing;
using System.Drawing.Drawing2D;

namespace AorinEQ.Core;

/// <summary>The tray glyph: a monochrome speaker whose arc count tracks the volume, mirroring the
/// Windows volume icon. Drawn at runtime rather than shipped as art, because the look depends on
/// three things only the running machine knows — the volume, the taskbar's theme, and the shell's
/// current small-icon size (which changes with DPI).
///
/// Every coordinate is expressed in the 32x32 grid the glyph was designed on and multiplied by
/// <c>size / 32</c>, so one authored geometry serves 16px at 100% DPI and 32px at 200% alike.
///
/// This type only draws. Caching, and the OS handles that come with turning a bitmap into an icon,
/// live in <see cref="TrayIconRenderer"/>.</summary>
public static class TrayGlyph
{
    /// <summary>Arcs drawn at full volume. Three is the Windows volume icon's own idiom, and the
    /// most that fits legibly beside the speaker at 16px.</summary>
    public const int MaxArcs = 3;

    /// <summary>The grid the geometry below is authored on.</summary>
    private const float DesignGrid = 32f;

    /// <summary>White on a dark taskbar; near-black (not pure black, which reads as a hole beside
    /// the shell's own glyphs) on a light one.</summary>
    private static readonly Color DarkTaskbarGlyph = Color.FromArgb(255, 255, 255, 255);
    private static readonly Color LightTaskbarGlyph = Color.FromArgb(255, 25, 25, 25);

    /// <summary>Diameter and stroke width of each arc, in design units, innermost first. They grow
    /// outward from the same centre so adding an arc never moves the ones already drawn.</summary>
    private static readonly (float Diameter, float StrokeWidth)[] ArcSpecs =
    [
        (8f, 2.2f),
        (15f, 2.2f),
        (22f, 2.2f),
    ];

    /// <summary>Arc centre (design units) — two units right of the cone's mouth, so the arcs sit
    /// concentric with the sound leaving the speaker.</summary>
    private const float ArcCentreX = 17f;
    private const float ArcCentreY = 16f;

    /// <summary>The arcs open toward the right: 110° of sweep centred on the +x axis.</summary>
    private const float ArcStartAngle = -55f;
    private const float ArcSweepAngle = 110f;

    /// <summary>How many arcs a volume level shows. The bands mirror the Windows volume icon:
    /// silence draws none, and the rest of the scale is split in thirds. Percent is clamped, so a
    /// device reporting something out of range degrades to silent or full rather than throwing
    /// inside the renderer.</summary>
    public static int ArcCount(int percent) => Math.Clamp(percent, 0, 100) switch
    {
        0 => 0,
        <= 33 => 1,
        <= 66 => 2,
        _ => MaxArcs,
    };

    /// <summary>The glyph colour for the taskbar it will sit on. Takes the taskbar's theme (the
    /// shell's <c>SystemUsesLightTheme</c>), not the apps theme — Windows lets those differ.</summary>
    public static Color GlyphColor(bool lightTaskbar) => lightTaskbar ? LightTaskbarGlyph : DarkTaskbarGlyph;

    /// <summary>Draws the glyph into a fresh transparent 32-bit bitmap of <paramref name="sizePx"/>
    /// square. The caller owns the bitmap.</summary>
    /// <param name="arcs">0..<see cref="MaxArcs"/>, from <see cref="ArcCount"/>. Ignored when
    /// <paramref name="muted"/> — muted draws the cross instead, at any volume.</param>
    public static Bitmap Draw(int arcs, bool muted, Color color, int sizePx)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(arcs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(arcs, MaxArcs);
        ArgumentOutOfRangeException.ThrowIfLessThan(sizePx, 1);

        var bmp = new Bitmap(sizePx, sizePx, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            float u = sizePx / DesignGrid;
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using (var brush = new SolidBrush(color))
            {
                // Body: the boxy back of the speaker. Cone: the flare, drawn as one quad so the
                // two shapes meet without a seam at x=9.
                g.FillRectangle(brush, 4 * u, 12 * u, 5 * u, 8 * u);
                g.FillPolygon(brush,
                [
                    new PointF(9 * u, 12 * u),
                    new PointF(15 * u, 6 * u),
                    new PointF(15 * u, 26 * u),
                    new PointF(9 * u, 20 * u),
                ]);
            }

            if (muted)
            {
                using var pen = RoundPen(color, 2.4f * u);
                g.DrawLine(pen, 19 * u, 12 * u, 27 * u, 20 * u);
                g.DrawLine(pen, 27 * u, 12 * u, 19 * u, 20 * u);
            }
            else
            {
                for (int i = 0; i < arcs; i++)
                {
                    var (diameter, strokeWidth) = ArcSpecs[i];
                    using var pen = RoundPen(color, strokeWidth * u);

                    // Snap the arc's bounding box to whole pixels. The three rings are ~1px thick
                    // and ~2px apart at tray sizes, so leaving them on sub-pixel boundaries spreads
                    // each stroke over two pixel columns at half alpha and they smear into one grey
                    // blob — this is the difference between "three arcs" and "a smudge" at 16px.
                    // The body and cone are large filled shapes and need no such hinting.
                    // A diameter can round to 0 at absurd icon sizes, which GDI+ rejects.
                    float d = MathF.Max(1f, MathF.Round(diameter * u));
                    float half = (diameter * u) / 2f;
                    g.DrawArc(pen,
                        MathF.Round((ArcCentreX * u) - half), MathF.Round((ArcCentreY * u) - half),
                        d, d, ArcStartAngle, ArcSweepAngle);
                }
            }

            return bmp;
        }
        catch
        {
            bmp.Dispose();
            throw;
        }
    }

    /// <summary>Round caps everywhere: at 16px a butt cap on a ~1px stroke reads as a notch.</summary>
    private static Pen RoundPen(Color color, float width) =>
        new(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
}
