using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AorinEQ.Core;
using Point = System.Windows.Point;

namespace AorinEQ.UI;

/// <summary>A picture of the artboards for the eyedropper, taken when it starts and again after
/// anything on the canvas changes: sampling a live visual on every mouse move would re-render the
/// whole canvas each time.
///
/// It is rendered at the screen's own pixel density - the monitor's DPI scale times the canvas
/// zoom - so each of its pixels is one device pixel of the canvas as shown, and the colour picked
/// is the one the user is looking at: at 150 % and zoom 4 an art pixel is six of them, drawn with
/// the artboards' own scaling mode. It is addressed in the element's DIPs - the unit every WPF
/// pointer position is in - and its pixel grid starts at the element's origin, as the zoomed
/// art's does. At that density the whole canvas would be huge at 800 %, so only the area asked
/// for (the part on screen) is rendered.
///
/// A visual is drawn - by RenderTargetBitmap.Render and by a VisualBrush alike - where it sits in
/// its PARENT, offset and render transform included, so an element not at (0,0) would be sampled
/// somewhere else. The brush's viewbox is therefore the area's rectangle in the parent, stretched
/// back onto the area's size.</summary>
internal sealed class ArtboardSnapshot
{
    private readonly RenderTargetBitmap _bitmap;
    private readonly double _scale;
    private readonly int _left, _top;

    /// <param name="area">The part of <paramref name="visual"/> to render, in its DIPs; clipped
    /// to the visual.</param>
    public ArtboardSnapshot(FrameworkElement visual, double dpiScale, double zoom, Rect area)
    {
        _scale = (dpiScale > 0 ? dpiScale : 1) * (zoom > 0 ? zoom : 1);
        area.Intersect(new Rect(0, 0, visual.ActualWidth, visual.ActualHeight));
        if (area.IsEmpty) area = new Rect(0, 0, 0, 0);
        // Out to whole device pixels of the element's own grid.
        _left = (int)Math.Floor(area.X * _scale);
        _top = (int)Math.Floor(area.Y * _scale);
        int width = Math.Max(1, (int)Math.Ceiling(area.Right * _scale) - _left);
        int height = Math.Max(1, (int)Math.Ceiling(area.Bottom * _scale) - _top);
        var source = new Rect(_left / _scale, _top / _scale, width / _scale, height / _scale);
        var brush = new VisualBrush(visual)
        {
            Viewbox = VisualTreeHelper.GetParent(visual) is Visual parent ? visual.TransformToAncestor(parent).TransformBounds(source) : source,
            ViewboxUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Fill,
        };
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
            dc.DrawRectangle(brush, null, new Rect(source.Size));
        _bitmap = new RenderTargetBitmap(width, height, 96 * _scale, 96 * _scale, PixelFormats.Pbgra32);
        _bitmap.Render(drawing);
        _bitmap.Freeze();
    }

    public int PixelWidth => _bitmap.PixelWidth;

    public int PixelHeight => _bitmap.PixelHeight;

    /// <summary>The colour of the device pixel under a DIP point, with its alpha (un-premultiplied),
    /// or null off the picture.</summary>
    public SkinColor? ColorAt(Point dip)
    {
        var (x, y) = DevicePixel(dip);
        if (x < 0 || y < 0 || x >= _bitmap.PixelWidth || y >= _bitmap.PixelHeight) return null;
        var bgra = new byte[4];
        _bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), bgra, 4, 0);
        byte a = bgra[3];
        return a == 0
            ? new SkinColor(0, 0, 0, 0)
            : new SkinColor(a, Straight(bgra[2], a), Straight(bgra[1], a), Straight(bgra[0], a));
    }

    /// <summary>The device pixels within <paramref name="radius"/> of a DIP point, for the magnifier.</summary>
    public BitmapSource? Region(Point dip, int radius)
    {
        var (cx, cy) = DevicePixel(dip);
        int left = Math.Max(0, cx - radius), top = Math.Max(0, cy - radius);
        int right = Math.Min(_bitmap.PixelWidth, cx + radius + 1), bottom = Math.Min(_bitmap.PixelHeight, cy + radius + 1);
        if (right <= left || bottom <= top) return null;
        var crop = new CroppedBitmap(_bitmap, new Int32Rect(left, top, right - left, bottom - top));
        crop.Freeze();
        return crop;
    }

    private (int X, int Y) DevicePixel(Point dip) =>
        ((int)Math.Floor(dip.X * _scale) - _left, (int)Math.Floor(dip.Y * _scale) - _top);

    private static byte Straight(byte premultiplied, byte alpha) =>
        (byte)Math.Min(255, Math.Round(premultiplied * 255.0 / alpha));
}
