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
    /// blur/depth and letter spacing are all multiplied by scale so the number tracks the skin's
    /// zoom), at the style's own weight and slant. Returns the measured text width (already
    /// scale-multiplied) so callers can place the Path's margin per the style's alignment — the
    /// width changes with the digit count, so the margin must be recomputed on every update.</summary>
    public static double Update(Path path, SkinText style, string text, double scale, double pixelsPerDip) =>
        Update(path, style, text, scale, pixelsPerDip, null, double.PositiveInfinity).Width;

    /// <summary><see cref="Update(Path,SkinText,string,double,double)"/> for a text laid out in a
    /// box (the notice name): drawn in <paramref name="family"/> when one is given instead of the
    /// style's, and trimmed with an ellipsis to <paramref name="maxWidth"/>. Returns the size drawn.</summary>
    internal static System.Windows.Size Update(Path path, SkinText style, string text, double scale, double pixelsPerDip,
        System.Windows.Media.FontFamily? family, double maxWidth)
    {
        var (geometry, size) = Layout(style, text, scale, pixelsPerDip, family, maxWidth);

        // Geometry origin at (0,0); the Path is positioned by its Margin by the caller.
        path.Data = geometry;
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

        return size;
    }

    /// <summary>The size <see cref="Update(Path,SkinText,string,double,double)"/> draws
    /// <paramref name="text"/> at in skin pixels (scale 1), before outline and shadow - what the
    /// designer's canvas outlines the text with.</summary>
    internal static System.Windows.Size Measure(SkinText style, string text, double pixelsPerDip) =>
        Layout(style, text, 1.0, pixelsPerDip, null, double.PositiveInfinity).Size;

    /// <summary>The text as both Update and <see cref="Measure"/> lay it out: one line, its height
    /// the font's line height. Without letter spacing it is one FormattedText; with it, each text
    /// element (<see cref="SkinMath.TextElements"/>) is laid out on its own with the spacing between
    /// them, never after the last, and the width counts exactly that. Wider than
    /// <paramref name="maxWidth"/>, the text is trimmed as a TextBlock trims, ending in an ellipsis;
    /// a box with no room draws nothing.</summary>
    private static (Geometry Geometry, System.Windows.Size Size) Layout(SkinText style, string text, double scale,
        double pixelsPerDip, System.Windows.Media.FontFamily? family, double maxWidth)
    {
        var typeface = new Typeface(
            family ?? new System.Windows.Media.FontFamily(string.IsNullOrWhiteSpace(style.FontFamily) ? SkinClamps.DefaultFontFamily : style.FontFamily),
            style.Italic ? FontStyles.Italic : FontStyles.Normal,
            FontWeight.FromOpenTypeWeight(style.Weight),
            FontStretches.Normal);
        FormattedText Format(string s) => new(
            s, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
            typeface, Math.Max(1.0, style.FontSize * scale),
            System.Windows.Media.Brushes.Black, pixelsPerDip);

        var whole = Format(text);
        if (maxWidth <= 0) return (Geometry.Empty, new System.Windows.Size(0, whole.Height));

        double spacing = style.LetterSpacing * scale;
        var elements = SkinMath.TextElements(text);
        if (spacing == 0 || elements.Count < 2)
        {
            if (whole.WidthIncludingTrailingWhitespace > maxWidth)
            {
                whole.MaxTextWidth = maxWidth;
                whole.MaxLineCount = 1;
                whole.Trimming = TextTrimming.CharacterEllipsis;
            }
            return (whole.BuildGeometry(new System.Windows.Point(0, 0)),
                new System.Windows.Size(whole.WidthIncludingTrailingWhitespace, whole.Height));
        }

        var pieces = elements.Select(Format).ToList();
        if (Spaced(pieces, pieces.Count, spacing) > maxWidth)
        {
            // Whole elements come off the end until what is left, the spacing and an ellipsis fit.
            var ellipsis = Format("…");
            int keep = pieces.Count - 1;
            while (keep > 0 && Spaced(pieces, keep, spacing) + spacing + ellipsis.WidthIncludingTrailingWhitespace > maxWidth) keep--;
            pieces = [.. pieces.Take(keep), ellipsis];
        }

        // Nonzero: with a negative spacing neighbours overlap, and their overlap stays filled.
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        double x = 0;
        foreach (var piece in pieces)
        {
            group.Children.Add(piece.BuildGeometry(new System.Windows.Point(x, 0)));
            x += piece.WidthIncludingTrailingWhitespace + spacing;
        }
        return (group, new System.Windows.Size(Math.Max(0, Spaced(pieces, pieces.Count, spacing)), whole.Height));
    }

    /// <summary>The width of the first <paramref name="count"/> pieces with the spacing between them.</summary>
    private static double Spaced(List<FormattedText> pieces, int count, double spacing) =>
        pieces.Take(count).Sum(p => p.WidthIncludingTrailingWhitespace) + spacing * Math.Max(0, count - 1);

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
