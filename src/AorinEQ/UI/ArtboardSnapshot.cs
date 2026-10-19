using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AorinEQ.Core;
using Point = System.Windows.Point;

namespace AorinEQ.UI;

/// <summary>A picture of the artboards for the eyedropper, taken once when it starts: sampling a
/// live visual on every mouse move would re-render the whole canvas each time.
///
/// It is rendered at the monitor's DPI, so the colour picked is the one the user is looking at,
/// and it is addressed in DIPs - the unit every WPF pointer position is in - so a 150 % monitor
/// cannot shift the sample. A visual is drawn - by RenderTargetBitmap.Render and by a VisualBrush
/// alike - where it sits in its PARENT, offset and render transform included, so an element not at
/// (0,0) would be sampled somewhere else. The brush's viewbox is therefore the element's own
/// rectangle in its parent, stretched back onto the element's size.</summary>
internal sealed class ArtboardSnapshot
{
    private readonly RenderTargetBitmap _bitmap;
    private readonly double _scale;

    public ArtboardSnapshot(FrameworkElement visual, double dpiScale)
    {
        _scale = dpiScale > 0 ? dpiScale : 1;
        double w = visual.ActualWidth, h = visual.ActualHeight;
        var own = new Rect(0, 0, w, h);
        var brush = new VisualBrush(visual)
        {
            Viewbox = VisualTreeHelper.GetParent(visual) is Visual parent ? visual.TransformToAncestor(parent).TransformBounds(own) : own,
            ViewboxUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Fill,
        };
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
            dc.DrawRectangle(brush, null, own);
        _bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(w * _scale)), Math.Max(1, (int)Math.Ceiling(h * _scale)),
            96 * _scale, 96 * _scale, PixelFormats.Pbgra32);
        _bitmap.Render(drawing);
        _bitmap.Freeze();
    }

    public int PixelWidth => _bitmap.PixelWidth;

    public int PixelHeight => _bitmap.PixelHeight;

    /// <summary>The colour at a DIP point, with its alpha (un-premultiplied), or null off the picture.</summary>
    public SkinColor? ColorAt(Point dip)
    {
        int x = (int)Math.Floor(dip.X * _scale), y = (int)Math.Floor(dip.Y * _scale);
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
        int cx = (int)Math.Floor(dip.X * _scale), cy = (int)Math.Floor(dip.Y * _scale);
        int left = Math.Max(0, cx - radius), top = Math.Max(0, cy - radius);
        int right = Math.Min(_bitmap.PixelWidth, cx + radius + 1), bottom = Math.Min(_bitmap.PixelHeight, cy + radius + 1);
        if (right <= left || bottom <= top) return null;
        var crop = new CroppedBitmap(_bitmap, new Int32Rect(left, top, right - left, bottom - top));
        crop.Freeze();
        return crop;
    }

    private static byte Straight(byte premultiplied, byte alpha) =>
        (byte)Math.Min(255, Math.Round(premultiplied * 255.0 / alpha));
}
