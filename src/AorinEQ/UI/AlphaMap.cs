using System.Windows.Media;
using System.Windows.Media.Imaging;
using AorinEQ.Core;

namespace AorinEQ.UI;

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
        PixelWidth = source.PixelWidth;
        PixelHeight = source.PixelHeight;
        _alpha = new byte[PixelWidth * PixelHeight];
        AccumulateMax(source, _alpha, PixelWidth, PixelHeight);
    }

    private AlphaMap(byte[] alpha, int width, int height)
    {
        _alpha = alpha;
        PixelWidth = width;
        PixelHeight = height;
    }

    /// <summary>Builds ONE map holding the union (per-pixel max alpha) of every source — used for
    /// animated skins so the hit shape covers all frames of both layers while memory stays a
    /// single byte-per-pixel array regardless of frame count. All sources must share pixel
    /// dimensions (guaranteed upstream by the loader's logical-frame-size validation).</summary>
    public static AlphaMap Union(IEnumerable<BitmapSource> sources)
    {
        byte[]? alpha = null;
        int width = 0, height = 0;
        foreach (var source in sources)
        {
            if (alpha is null)
            {
                width = source.PixelWidth;
                height = source.PixelHeight;
                alpha = new byte[width * height];
            }
            AccumulateMax(source, alpha, width, height);
        }
        if (alpha is null)
            throw new ArgumentException("At least one source is required.", nameof(sources));
        return new AlphaMap(alpha, width, height);
    }

    /// <summary>Max-combines a source's alpha channel into <paramref name="alpha"/>. Sources
    /// smaller than the map (defensive; upstream validation should prevent it) only cover their
    /// own extent.</summary>
    private static void AccumulateMax(BitmapSource source, byte[] alpha, int width, int height)
    {
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        int srcWidth = Math.Min(bgra.PixelWidth, width);
        int srcHeight = Math.Min(bgra.PixelHeight, height);
        int stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);

        for (int y = 0; y < srcHeight; y++)
        {
            int rowOffset = y * stride;
            int rowBase = y * width;
            for (int x = 0; x < srcWidth; x++)
            {
                byte a = pixels[rowOffset + x * 4 + 3]; // B,G,R,A -> alpha is byte 3
                if (a > alpha[rowBase + x]) alpha[rowBase + x] = a;
            }
        }
    }

    /// <summary>Whether the pixel at (x, y) counts as opaque/hit-testable. Out-of-bounds coordinates are never opaque.</summary>
    public bool IsOpaque(int x, int y)
    {
        if (x < 0 || y < 0 || x >= PixelWidth || y >= PixelHeight) return false;
        return SkinMath.IsOpaque(_alpha[y * PixelWidth + x]);
    }
}
