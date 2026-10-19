using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace AorinEQ.Tests;

/// <summary>How wide a line of text's ink is when WPF lays it out with a typeface given outright -
/// the oracle the renderer's output is checked against, built without the renderer, so a renderer
/// that drops a style's weight or slant cannot agree with it by construction.</summary>
internal static class TextInk
{
    public static double Width(string family, double size, FontWeight weight, FontStyle style, string text, double pixelsPerDip)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface(new System.Windows.Media.FontFamily(family), style, weight, FontStretches.Normal),
            size, Brushes.Black, pixelsPerDip);
        return formatted.BuildGeometry(new System.Windows.Point(0, 0)).Bounds.Width;
    }
}
