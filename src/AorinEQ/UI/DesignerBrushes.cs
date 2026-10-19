using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace AorinEQ.UI;

/// <summary>The checkerboard everything transparent in the designer sits on - the artboards, the
/// colour swatches, the picker's opacity ramp - so translucency reads the same everywhere. Frozen,
/// so one instance serves every thread and every window.</summary>
internal static class DesignerBrushes
{
    public static DrawingBrush Checkerboard { get; } = CreateCheckerboard();

    private static DrawingBrush CreateCheckerboard()
    {
        var squares = new GeometryGroup();
        squares.Children.Add(new RectangleGeometry(new Rect(0, 0, 8, 8)));
        squares.Children.Add(new RectangleGeometry(new Rect(8, 8, 8, 8)));
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)), null,
            new RectangleGeometry(new Rect(0, 0, 16, 16))));
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)), null, squares));
        var brush = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 16, 16),
            ViewportUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return brush;
    }
}
