using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using AorinEQ.Core;

namespace AorinEQ.UI;

/// <summary>Renders the skin percent number into a <see cref="Path"/> as glyph geometry, so the
/// outline is a real stroke (WPF text has no native stroke). Shared by <see cref="SkinOsdWindow"/>
/// and the designer preview so both look identical. All styling is best-effort: a malformed color
/// falls back (white text / no outline / no shadow) and never throws.</summary>
internal static class SkinComposite
{
    /// <summary>Clip that shows everything in a w×h canvas EXCEPT the filled bar span
    /// [barStart..fillWidth] — the union of the left region [0..barStart] and the right region
    /// [fillWidth..w]. Used for the empty layer so it never stacks under the (possibly translucent)
    /// full layer in the filled span, while decoration outside the fill range still shows.</summary>
    public static Geometry ComplementClip(double barStart, double fillWidth, double w, double h)
    {
        double leftEnd = Math.Clamp(barStart, 0, w);
        double rightStart = Math.Clamp(fillWidth, 0, w);
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        if (leftEnd > 0)
            group.Children.Add(new RectangleGeometry(new Rect(0, 0, leftEnd, h)));
        if (rightStart < w)
            group.Children.Add(new RectangleGeometry(new Rect(rightStart, 0, w - rightStart, h)));
        group.Freeze();
        return group;
    }
}

internal static class PercentTextRenderer
{
    /// <summary>Rebuilds <paramref name="path"/> to show <paramref name="text"/> styled per
    /// <paramref name="style"/> at <paramref name="scale"/> (font size, outline width, shadow
    /// blur/depth are all multiplied by scale so the number tracks the skin's zoom). An unbold
    /// style is drawn at <paramref name="unboldWeight"/>, which the caller owns: the percent
    /// number's historical SemiBold, the AirPlay labels' Normal - so moving a text onto this
    /// renderer never changes how an existing skin looks. Returns the measured text width (already
    /// scale-multiplied) so callers can place the Path's margin per the style's alignment — the
    /// width changes with the digit count, so the margin must be recomputed on every update.</summary>
    public static double Update(Path path, SkinText style, string text, double scale, double pixelsPerDip,
        FontWeight unboldWeight)
    {
        var formatted = Format(style, text, scale, pixelsPerDip, unboldWeight);

        // Geometry origin at (0,0); the Path is positioned by its Margin by the caller.
        path.Data = formatted.BuildGeometry(new System.Windows.Point(0, 0));
        path.Fill = Brush(style.Color) ?? System.Windows.Media.Brushes.White;

        var outline = style.OutlineColor is null ? null : Brush(style.OutlineColor);
        if (outline is not null && style.OutlineWidth > 0)
        {
            path.Stroke = outline;
            path.StrokeThickness = style.OutlineWidth * scale;
        }
        else
        {
            path.Stroke = null;
            path.StrokeThickness = 0;
        }

        path.Effect = SkinColor.Parse(style.ShadowColor) is { } shadow
            ? new DropShadowEffect
            {
                Color = ToMedia(shadow),
                BlurRadius = style.ShadowBlur * scale,
                ShadowDepth = style.ShadowDepth * scale,
                Direction = 315, // down-right, the conventional shadow angle
                Opacity = 1.0,
            }
            : null;

        return formatted.WidthIncludingTrailingWhitespace;
    }

    /// <summary>The size <see cref="Update"/> draws <paramref name="text"/> at in skin pixels (scale
    /// 1), before outline and shadow - what the designer's canvas outlines the text with.</summary>
    internal static System.Windows.Size Measure(SkinText style, string text, double pixelsPerDip, FontWeight unboldWeight)
    {
        var formatted = Format(style, text, 1.0, pixelsPerDip, unboldWeight);
        return new System.Windows.Size(formatted.WidthIncludingTrailingWhitespace, formatted.Height);
    }

    /// <summary>The text as both <see cref="Update"/> and <see cref="Measure"/> lay it out; an
    /// unbold style takes the caller's weight.</summary>
    private static FormattedText Format(SkinText style, string text, double scale, double pixelsPerDip, FontWeight unboldWeight)
    {
        var typeface = new Typeface(
            new System.Windows.Media.FontFamily(string.IsNullOrWhiteSpace(style.FontFamily) ? "Segoe UI" : style.FontFamily),
            FontStyles.Normal,
            style.Bold ? FontWeights.Bold : unboldWeight,
            FontStretches.Normal);
        return new FormattedText(
            text, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
            typeface, Math.Max(1.0, style.FontSize * scale),
            System.Windows.Media.Brushes.Black, pixelsPerDip);
    }

    /// <summary>A frozen brush for a user-authored colour, or null when it does not parse. Every
    /// skin colour in the app goes through here (and so through <see cref="SkinColor"/>): the
    /// notice, the AirPlay strip, the designer's swatches.</summary>
    internal static SolidColorBrush? Brush(string? color)
    {
        if (SkinColor.Parse(color) is not { } parsed) return null;
        var brush = new SolidColorBrush(ToMedia(parsed));
        brush.Freeze();
        return brush;
    }

    internal static System.Windows.Media.Color ToMedia(SkinColor c) =>
        System.Windows.Media.Color.FromArgb(c.A, c.R, c.G, c.B);
}
