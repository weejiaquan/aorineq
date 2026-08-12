using System.Drawing;
using System.Drawing.Imaging;

namespace AorinEQ.Tests;

/// <summary>Writes REAL, decodable image files. <see cref="TestPngs"/> writes header-only PNGs,
/// which is all <c>PngHeader</c>/<c>SkinLoader</c> need — but anything that actually renders a
/// skin (preview generation) has to decode pixels, so those tests use these instead. Every image
/// is a flat colour or a stack of flat colours, so a composed result can be checked by sampling
/// individual pixels rather than by eyeballing a screenshot.</summary>
internal static class RealPngs
{
    /// <summary>A single flat-colour PNG.</summary>
    public static void WriteSolid(string path, int width, int height, Color color) =>
        WriteFrames(path, width, height, new[] { color });

    /// <summary>A sprite sheet: one flat-colour frame of width×height per colour, stacked
    /// vertically the way <c>SkinLoader</c> reads sheets.</summary>
    public static void WriteFrames(string path, int width, int frameHeight, Color[] frameColors)
    {
        using var bitmap = new Bitmap(width, frameHeight * frameColors.Length, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            for (int i = 0; i < frameColors.Length; i++)
            {
                using var brush = new SolidBrush(frameColors[i]);
                g.FillRectangle(brush, new Rectangle(0, i * frameHeight, width, frameHeight));
            }
        }
        bitmap.Save(path, ImageFormat.Png);
    }

    /// <summary>Two vertical halves, so a composed image can prove WHICH source it sampled from
    /// at a given x as well as which layer.</summary>
    public static void WriteHalves(string path, int width, int height, Color left, Color right)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            using var leftBrush = new SolidBrush(left);
            using var rightBrush = new SolidBrush(right);
            g.FillRectangle(leftBrush, new Rectangle(0, 0, width / 2, height));
            g.FillRectangle(rightBrush, new Rectangle(width / 2, 0, width - width / 2, height));
        }
        bitmap.Save(path, ImageFormat.Png);
    }

    /// <summary>A real, decodable single-frame GIF of a flat colour.</summary>
    public static void WriteGif(string path, int width, int height, Color color)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            using var brush = new SolidBrush(color);
            g.FillRectangle(brush, new Rectangle(0, 0, width, height));
        }
        bitmap.Save(path, ImageFormat.Gif);
    }
}
