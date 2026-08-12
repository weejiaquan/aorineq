using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;

namespace AorinEQ.Core;

/// <summary>Renders the <c>preview.png</c> that ships inside a shared skin zip — the single image
/// a gallery lists a skin by. It composes the skin exactly the way the OSD does (empty layer
/// everywhere except the lit bar span, full layer clipped to the fill, percent number on top) at a
/// representative fill, at the skin's OWN logical frame size and never scaled: a listing wants the
/// artwork's real pixels, and a fixed size is what makes two exports of the same skin identical.
///
/// Drawing is GDI+ rather than WPF because this is <see cref="AorinEQ.Core"/> — the same reason
/// <see cref="TrayGlyph"/> is — and because it must run without a Dispatcher. Two consequences are
/// deliberate and worth knowing when comparing a preview against the live OSD:
/// the unbold percent number is drawn Regular where WPF's is SemiBold (GDI+ has no SemiBold), and
/// a text shadow is drawn as an offset copy because GDI+ has no gaussian blur. Everything else —
/// position, alignment, size, fill geometry, outline stroke, colours, transparency — matches.
///
/// Nothing here holds a handle on the skin folder after it returns: each layer is read into memory
/// and decoded from there, because the caller zips that very folder immediately afterwards.</summary>
public static class SkinPreview
{
    /// <summary>The file name inside a skin zip. One spelling, used by the writer and by the
    /// importer that refuses to trust an incoming one.</summary>
    public const string FileName = "preview.png";

    /// <summary>The fill a listing shows: enough of the bar lit to read as "a volume bar" while
    /// still showing what the empty artwork looks like.</summary>
    public const int GalleryPercent = 60;

    /// <summary>WPF's DropShadowEffect Direction=315 in screen coordinates: down and to the
    /// right, at 45°. cos/sin of 45° are the same number.</summary>
    private const double ShadowDirectionComponent = 0.70710678118654752;

    /// <summary>Composes <paramref name="info"/> at <paramref name="percent"/> and writes it as a
    /// PNG to <paramref name="destinationPath"/>, replacing whatever was there.</summary>
    /// <exception cref="InvalidOperationException">The skin is invalid, a layer failed to decode,
    /// or the file could not be written. Callers that generate previews opportunistically catch
    /// this one type.</exception>
    public static void Write(SkinInfo info, string destinationPath, int percent = GalleryPercent)
    {
        if (!info.IsValid)
            throw new InvalidOperationException($"Cannot preview an invalid skin: {info.Error}");

        try
        {
            using var canvas = Compose(info, percent);
            // Written through our own stream so a failure is an IOException we can describe,
            // rather than GDI+'s opaque "A generic error occurred".
            using var file = File.Create(destinationPath);
            canvas.Save(file, ImageFormat.Png);
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException
            or OutOfMemoryException or IOException or UnauthorizedAccessException)
        {
            // GDI+ reports a corrupt or unsupported image as ArgumentException/OutOfMemoryException
            // — a truncated download looks exactly like that, and it is the caller's business to
            // degrade, not to crash.
            throw new InvalidOperationException(
                $"Failed to generate {FileName} for '{info.Name}': {ex.Message}", ex);
        }
    }

    private static Bitmap Compose(SkinInfo info, int percent)
    {
        var canvas = new Bitmap(info.Width, info.Height, PixelFormat.Format32bppArgb);
        try
        {
            using var g = Graphics.FromImage(canvas);
            g.InterpolationMode = InterpolationMode.NearestNeighbor; // 1:1 blit, never resample
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.SmoothingMode = SmoothingMode.AntiAlias;               // for the glyph path only

            int fillWidth = SkinMath.FillWidth(info.Width, percent, info.FillStartX, info.FillEndX);
            var whole = new Rectangle(0, 0, info.Width, info.Height);

            // Empty everywhere EXCEPT the lit bar span [fillStartX..fillWidth], mirroring
            // SkinOsdWindow/SkinComposite: decoration outside the fill range keeps showing and a
            // translucent full layer never stacks on top of the empty one.
            using (var empty = LoadFirstFrame(info.EmptyPath, info.Width, info.Height))
            using (var complement = ComplementRegion(info.FillStartX, fillWidth, info.Width, info.Height))
            {
                g.Clip = complement;
                g.CompositingMode = CompositingMode.SourceCopy; // the base layer defines the alpha
                g.DrawImage(empty, whole, whole, GraphicsUnit.Pixel);
            }

            using (var full = LoadFirstFrame(info.FullPath, info.Width, info.Height))
            {
                // SetClip(Rectangle) rather than assigning a Region: assigning COPIES, leaving the
                // Region we built to be finalized instead of disposed.
                g.SetClip(new Rectangle(0, 0, Math.Max(0, fillWidth), info.Height));
                // SourceOver, NOT SourceCopy: the full layer sits ON the empty one exactly as it
                // does in the OSD. A ranged skin's full.png is transparent outside its bar, and
                // copying would punch that transparency straight through the decoration underneath.
                g.CompositingMode = CompositingMode.SourceOver;
                g.DrawImage(full, whole, whole, GraphicsUnit.Pixel);
            }

            g.ResetClip();
            if (info.Text is { Show: true } text)
                DrawPercentText(g, text, percent);
            return canvas;
        }
        catch
        {
            canvas.Dispose(); // the caller never sees a half-composed bitmap to dispose itself
            throw;
        }
    }

    /// <summary>Everything except the filled bar span: the union of [0..barStart] and
    /// [fillWidth..width]. The GDI+ twin of <c>SkinComposite.ComplementClip</c>.</summary>
    private static Region ComplementRegion(int barStart, int fillWidth, int width, int height)
    {
        int leftEnd = Math.Clamp(barStart, 0, width);
        int rightStart = Math.Clamp(fillWidth, 0, width);
        var region = new Region();
        try
        {
            region.MakeEmpty(); // a fresh Region is INFINITE; the unions below build it up
            if (leftEnd > 0)
                region.Union(new Rectangle(0, 0, leftEnd, height));
            if (rightStart < width)
                region.Union(new Rectangle(rightStart, 0, width - rightStart, height));
            return region;
        }
        catch
        {
            region.Dispose();
            throw;
        }
    }

    /// <summary>Decodes one layer's FIRST frame into a fresh width×height bitmap. Read through a
    /// MemoryStream on purpose: <c>Image.FromFile</c> keeps the file locked for the image's
    /// lifetime, and the folder being previewed is about to be zipped (or re-saved) by the caller.
    /// A vertical sprite sheet's first frame is exactly the top width×height rectangle; a GIF
    /// decodes to its first frame by default.</summary>
    private static Bitmap LoadFirstFrame(string path, int width, int height)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path));
        using var source = Image.FromStream(stream);
        var frame = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        try
        {
            using var g = Graphics.FromImage(frame);
            g.CompositingMode = CompositingMode.SourceCopy; // copy alpha verbatim, never blend
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            var rect = new Rectangle(0, 0, width, height);
            g.DrawImage(source, rect, rect, GraphicsUnit.Pixel);
            return frame;
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    /// <summary>Draws the percent number with the skin's styling: shadow copy, then the outline
    /// stroke, then the fill — the same order WPF composites them.</summary>
    private static void DrawPercentText(Graphics g, SkinText style, int percent)
    {
        string text = percent.ToString(CultureInfo.InvariantCulture);
        using var family = ResolveFamily(style.FontFamily);
        var fontStyle = PickStyle(family, style.Bold);
        float emSize = (float)Math.Max(1.0, style.FontSize);

        // GenericTypographic measures the glyph advance without GDI's extra padding, which is
        // what WPF's FormattedText width means and so what the alignment math expects. The
        // property hands back a NEW StringFormat every call, so it is ours to dispose.
        using var format = StringFormat.GenericTypographic;
        using var font = new Font(family, emSize, fontStyle, GraphicsUnit.Pixel);
        float textWidth = g.MeasureString(text, font, PointF.Empty, format).Width;
        float left = (float)SkinMath.AlignedTextX(style.X, textWidth, style.Align);

        using var path = new GraphicsPath();
        path.AddString(text, family, (int)fontStyle, emSize, new PointF(left, style.Y), format);

        if (ParseColor(style.ShadowColor) is { } shadowColor)
        {
            // No gaussian blur in GDI+: an offset copy is the honest approximation, placed where
            // WPF's Direction=315 shadow lands.
            float offset = (float)(style.ShadowDepth * ShadowDirectionComponent);
            using var shadowPath = (GraphicsPath)path.Clone();
            using var move = new Matrix();
            move.Translate(offset, offset);
            shadowPath.Transform(move);
            using var shadowBrush = new SolidBrush(shadowColor);
            g.FillPath(shadowBrush, shadowPath);
        }

        if (ParseColor(style.OutlineColor) is { } outlineColor && style.OutlineWidth > 0)
        {
            using var pen = new Pen(outlineColor, (float)style.OutlineWidth)
            {
                LineJoin = LineJoin.Round, // sharp miters spike out of tight glyph corners
            };
            g.DrawPath(pen, path);
        }

        using var brush = new SolidBrush(ParseColor(style.Color) ?? Color.White);
        g.FillPath(brush, path);
    }

    /// <summary>The authored family when this machine has it, a generic sans-serif otherwise —
    /// a skin authored with a font the viewer lacks still gets its number.</summary>
    private static FontFamily ResolveFamily(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            try { return new FontFamily(name.Trim()); }
            catch (ArgumentException) { } // not installed: fall through
        }
        return FontFamily.GenericSansSerif;
    }

    /// <summary>Bold when asked and available, otherwise whatever the family does have. WPF's
    /// unbold baseline is SemiBold, which GDI+ cannot express, so unbold lands on Regular.</summary>
    private static FontStyle PickStyle(FontFamily family, bool bold)
    {
        var wanted = bold ? FontStyle.Bold : FontStyle.Regular;
        if (family.IsStyleAvailable(wanted)) return wanted;
        foreach (var candidate in new[] { FontStyle.Regular, FontStyle.Bold, FontStyle.Italic })
            if (family.IsStyleAvailable(candidate)) return candidate;
        return FontStyle.Regular;
    }

    /// <summary>Parses the same colour spellings the UI layer accepts — #RGB, #ARGB, #RRGGBB,
    /// #AARRGGBB and the named colours — returning null on anything else so the caller falls back
    /// instead of throwing on an author-supplied string.</summary>
    private static Color? ParseColor(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return null;

        if (text[0] == '#')
        {
            var hex = text[1..];
            if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw))
                return null;
            return hex.Length switch
            {
                // Shorthand digits double, exactly like the WPF converter: #F0C -> #FFFF00CC.
                3 => FromArgb(0xF, (raw >> 8) & 0xF, (raw >> 4) & 0xF, raw & 0xF, doubled: true),
                4 => FromArgb((raw >> 12) & 0xF, (raw >> 8) & 0xF, (raw >> 4) & 0xF, raw & 0xF, doubled: true),
                6 => FromArgb(0xFF, (raw >> 16) & 0xFF, (raw >> 8) & 0xFF, raw & 0xFF, doubled: false),
                8 => FromArgb((raw >> 24) & 0xFF, (raw >> 16) & 0xFF, (raw >> 8) & 0xFF, raw & 0xFF, doubled: false),
                _ => null,
            };
        }

        var named = Color.FromName(text);
        return named.IsKnownColor ? named : null;
    }

    private static Color FromArgb(uint a, uint r, uint g, uint b, bool doubled)
    {
        if (doubled)
        {
            a = a * 17; r = r * 17; g = g * 17; b = b * 17; // 0xF -> 0xFF
        }
        return Color.FromArgb((int)a, (int)r, (int)g, (int)b);
    }
}
