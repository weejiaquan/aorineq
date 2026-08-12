using System.Drawing;
using System.Drawing.Drawing2D;

namespace AorinEQ.Core;

/// <summary>The app icon's art: the tray's speaker on a filled graphite tile. Drawn here rather
/// than authored in an image editor so tray and taskbar cannot drift apart — <see cref="Draw"/>
/// calls <see cref="TrayGlyph.Draw"/> for the speaker itself, at the "high" three-arc state, so the
/// two identities are literally the same geometry.
///
/// WHY A TILE AND NOT JUST THE GLYPH. The tray glyph is monochrome ink on transparency, which works
/// because the app knows the one background it will ever sit on (the taskbar, whose theme it reads).
/// The app icon has no such luxury: Explorer's white list view, a black alt-tab overlay and an
/// arbitrary wallpaper behind a desktop shortcut are all in scope, and a bare monochrome glyph
/// disappears into at least one of them. An opaque tile makes its own background — the graphite
/// fill separates it from a light desktop, the edge highlight separates it from a dark one, and the
/// near-white speaker only ever has to contrast with the tile.
///
/// The palette is deliberately NEUTRAL GRAPHITE. The orange brand square this replaces is retired;
/// nothing here should reintroduce a brand hue.
///
/// Every coordinate is expressed in the same 32x32 grid <see cref="TrayGlyph"/> is authored on and
/// multiplied by <c>size / 32</c>, so one authored geometry serves 16px and 256px alike.
///
/// This type only draws; <see cref="IcoWriter"/> packs the frames into the shipped .ico. The two
/// are run by <c>tools/AppIconGen</c>, and the shipped file is REGENERATED, never edited — from the
/// repo root:
///
///     dotnet run --project tools/AppIconGen -- src/AorinEQ/AorinEQ.ico
///
/// The output is deterministic, so an unchanged art file regenerates byte for byte and a changed
/// one shows up as a diff.</summary>
public static class AppIconArt
{
    /// <summary>The sizes the shipped .ico carries, ascending: 16 title bar, 24/32 taskbar and
    /// alt-tab at common DPIs, 48/64/256 Explorer views. Windows picks the nearest frame at or
    /// above what it needs, so a missing size is a fuzzy icon rather than a missing one — which is
    /// exactly why the list is stated once, here, and read by the generator.</summary>
    public static IReadOnlyList<int> FrameSizes { get; } = [16, 24, 32, 48, 64, 256];

    /// <summary>The grid the geometry below is authored on — <see cref="TrayGlyph"/>'s, so the two
    /// files' constants mean the same thing.</summary>
    private const float DesignGrid = 32f;

    /// <summary>Corner radius, ~18% of the tile: the Fluent app-tile idiom. Enough that the corners
    /// read as rounded at 16px, not so much that the tile becomes a circle at 256px.</summary>
    private const float CornerRadius = 5.76f;

    /// <summary>The edge highlight's width. One design unit is 8px at 256 and rounds up to a whole
    /// pixel at 16 (see <see cref="Draw"/>) — below a whole pixel the highlight is a half-alpha
    /// smear that does not separate the tile from anything.</summary>
    private const float EdgeWidth = 1f;

    /// <summary>How much of the tile the speaker spans, in design units. The remaining ~2.5 units a
    /// side become the tile margin: <see cref="TrayGlyph"/>'s own geometry already leaves 4 units of
    /// padding inside its grid, so the visible margin lands near 18% — the same proportion as the
    /// corner radius, which is what makes the tile look drawn rather than assembled.</summary>
    private const float GlyphExtent = 27f;

    /// <summary>The tile: a vertical graphite gradient. Light at the top, as if lit from above, so
    /// the tile has a range of luminances rather than one flat value — the top edge is what a dark
    /// desktop sees, the body is what a light one sees.</summary>
    private static readonly Color TileTop = Color.FromArgb(255, 86, 93, 102);
    private static readonly Color TileBottom = Color.FromArgb(255, 42, 46, 52);

    /// <summary>The rim. The single reason this icon is legible on a black background: a graphite
    /// tile alone contrasts about 1.7:1 with a dark wallpaper, which is no silhouette at all.</summary>
    private static readonly Color TileEdge = Color.FromArgb(255, 110, 119, 131);

    /// <summary>The speaker. Near-white rather than pure white, matching
    /// <see cref="TrayGlyph"/>'s reasoning about ink that reads as a hole beside the shell's own
    /// glyphs — and it is the only near-white in the art, which is how the tests find it.</summary>
    private static readonly Color GlyphInk = Color.FromArgb(255, 245, 247, 250);

    /// <summary>Draws one app-icon frame into a fresh 32-bit bitmap of <paramref name="sizePx"/>
    /// square. The caller owns the bitmap.
    ///
    /// Each frame is drawn at ITS OWN size — never rendered large and resampled down. The speaker's
    /// arcs are barely a pixel thick at 16px and <see cref="TrayGlyph"/> snaps them to the pixel
    /// grid to keep them apart; scaling a 256px render would blend that hinting away and the three
    /// arcs would land as one grey smudge.</summary>
    public static Bitmap Draw(int sizePx)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sizePx, 1);

        var bmp = new Bitmap(sizePx, sizePx, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            float u = sizePx / DesignGrid;
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // GDI+ samples at pixel CORNERS by default. Two things go wrong at icon sizes because
            // of it: antialiased coverage is biased half a pixel down and right (at 16px the tile's
            // bottom-right corner comes back 56% opaque while its top-left is empty — the same
            // rounded corner, rendered two different ways), and even a 1:1 DrawImage lands between
            // pixels and gets resampled, which would blend away exactly the pixel hinting the next
            // paragraph exists to preserve. Half puts the sample point at the pixel centre, which
            // makes the four corners agree and the glyph blit exact.
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.Clear(Color.Transparent);

            // The rim is stroked ON the tile's outline, so the outline is inset by half its width
            // and the stroke lands entirely inside the frame instead of half off the edge.
            float edge = MathF.Max(1f, EdgeWidth * u);
            var tile = new RectangleF(edge / 2f, edge / 2f, sizePx - edge, sizePx - edge);

            using (var path = RoundedRect(tile, CornerRadius * u))
            {
                // The gradient spans the whole frame rather than the (inset) tile, so the two are
                // not one pixel out of step. TileFlipXY suppresses GDI+'s wrap artefact on the
                // first and last row of a gradient's own bounds.
                using (var fill = new LinearGradientBrush(
                    new RectangleF(0f, 0f, sizePx, sizePx), TileTop, TileBottom, LinearGradientMode.Vertical)
                    { WrapMode = WrapMode.TileFlipXY })
                {
                    g.FillPath(fill, path);
                }

                using var pen = new Pen(TileEdge, edge);
                g.DrawPath(pen, path);
            }

            int glyphPx = GlyphSizePx(sizePx);
            int offset = (sizePx - glyphPx) / 2;
            using var glyph = TrayGlyph.Draw(TrayGlyph.MaxArcs, muted: false, GlyphInk, glyphPx);
            g.DrawImageUnscaled(glyph, offset, offset);

            return bmp;
        }
        catch
        {
            bmp.Dispose();
            throw;
        }
    }

    /// <summary>The speaker's size inside a tile of <paramref name="sizePx"/>. Forced to leave an
    /// EVEN margin so the glyph is centred exactly: at 32px the natural rounding gives 27, which
    /// would sit 2px from one side and 3px from the other — a visible list at the size Windows uses
    /// most. Clamped to at least one pixel so absurd sizes fail in the caller's arguments rather
    /// than inside GDI+.</summary>
    private static int GlyphSizePx(int sizePx)
    {
        int glyphPx = (int)MathF.Round(GlyphExtent * sizePx / DesignGrid);
        if (((sizePx - glyphPx) & 1) != 0) glyphPx--;
        return Math.Max(1, glyphPx);
    }

    /// <summary>A rounded rectangle as a closed path — the tile's outline, used to fill and to
    /// stroke, so the gradient and the rim cannot disagree about where the edge is.</summary>
    private static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        float diameter = MathF.Min(radius, MathF.Min(bounds.Width, bounds.Height) / 2f) * 2f;
        var path = new GraphicsPath();
        try
        {
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180f, 90f);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270f, 90f);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0f, 90f);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90f, 90f);
            path.CloseFigure();
            return path;
        }
        catch
        {
            path.Dispose();
            throw;
        }
    }
}
