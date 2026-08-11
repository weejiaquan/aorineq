using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using ApoVolume.Core;

namespace ApoVolume.UI;

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
    /// blur/depth are all multiplied by scale so the number tracks the skin's zoom). Returns the
    /// measured text width (already scale-multiplied) so callers can place the Path's margin per
    /// the style's alignment — the width changes with the digit count, so the margin must be
    /// recomputed on every update.</summary>
    public static double Update(Path path, SkinText style, string text, double scale, double pixelsPerDip)
    {
        var typeface = new Typeface(
            new System.Windows.Media.FontFamily(string.IsNullOrWhiteSpace(style.FontFamily) ? "Segoe UI" : style.FontFamily),
            FontStyles.Normal,
            // Unbold baseline is SemiBold — the historical look of the percent number before
            // styling existed, so plain {show,x,y} skins render exactly as they always did.
            style.Bold ? FontWeights.Bold : FontWeights.SemiBold,
            FontStretches.Normal);

        var formatted = new FormattedText(
            text, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
            typeface, Math.Max(1.0, style.FontSize * scale),
            System.Windows.Media.Brushes.Black, pixelsPerDip);

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

        var shadow = style.ShadowColor is null ? null : ParseColor(style.ShadowColor);
        path.Effect = shadow is { } sc
            ? new DropShadowEffect
            {
                Color = sc,
                BlurRadius = style.ShadowBlur * scale,
                ShadowDepth = style.ShadowDepth * scale,
                Direction = 315, // down-right, the conventional shadow angle
                Opacity = 1.0,
            }
            : null;

        return formatted.WidthIncludingTrailingWhitespace;
    }

    private static SolidColorBrush? Brush(string? hex)
    {
        var c = ParseColor(hex);
        if (c is null) return null;
        var brush = new SolidColorBrush(c.Value);
        brush.Freeze();
        return brush;
    }

    /// <summary>Parses #AARRGGBB / #RRGGBB (and named colors) via WPF's converter; null on any
    /// failure so callers fall back rather than crash on user-authored strings.</summary>
    private static System.Windows.Media.Color? ParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try
        {
            var converted = System.Windows.Media.ColorConverter.ConvertFromString(hex);
            return converted is System.Windows.Media.Color c ? c : null;
        }
        catch (FormatException) { return null; }
        catch (NotSupportedException) { return null; }
    }
}
