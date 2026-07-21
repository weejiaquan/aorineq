using System.Windows.Media;
using System.Windows.Media.Imaging;
using ApoVolume.Core;

namespace ApoVolume.UI;

/// <summary>Per-pixel alpha lookup for hit-testing a skin image. Built once per skin load: decodes
/// the source into Bgra32 (converting first if necessary), copies its pixels once, and retains
/// only the alpha channel + dimensions — the color channels are never needed for hit-testing.</summary>
public sealed class AlphaMap
{
    private readonly byte[] _alpha; // one byte per pixel, row-major (y * PixelWidth + x)

    public int PixelWidth { get; }
    public int PixelHeight { get; }

    public AlphaMap(BitmapSource source)
    {
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        PixelWidth = bgra.PixelWidth;
        PixelHeight = bgra.PixelHeight;

        int stride = PixelWidth * 4;
        var pixels = new byte[stride * PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);

        _alpha = new byte[PixelWidth * PixelHeight];
        for (int y = 0; y < PixelHeight; y++)
        {
            int rowOffset = y * stride;
            int rowBase = y * PixelWidth;
            for (int x = 0; x < PixelWidth; x++)
                _alpha[rowBase + x] = pixels[rowOffset + x * 4 + 3]; // B,G,R,A -> alpha is byte 3
        }
    }

    /// <summary>Whether the pixel at (x, y) counts as opaque/hit-testable. Out-of-bounds coordinates are never opaque.</summary>
    public bool IsOpaque(int x, int y)
    {
        if (x < 0 || y < 0 || x >= PixelWidth || y >= PixelHeight) return false;
        return SkinMath.IsOpaque(_alpha[y * PixelWidth + x]);
    }
}
