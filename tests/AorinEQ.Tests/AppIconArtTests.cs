using System.Drawing;
using AorinEQ.Core;

namespace AorinEQ.Tests;

/// <summary>The app icon's art. Unlike the tray glyph — monochrome ink on transparency, drawn to sit
/// on a taskbar whose theme the app already knows — the app icon lands on backgrounds nobody can
/// predict: Explorer's white list view, a black alt-tab overlay, an arbitrary wallpaper behind a
/// desktop shortcut. That is why it is a FILLED tile rather than a bare glyph, and the assertions
/// below are the legibility contract that follows from it:
///
///  - the tile is opaque where it covers, so no background can wash the art out;
///  - its corners are NOT (it is a rounded tile — square corners would be a different icon);
///  - the speaker reads against the tile, and the tile reads against both a white and a black
///    desktop, measured as WCAG contrast ratios rather than eyeballed colour constants;
///  - the glyph is composited at each frame's OWN size, never rendered large and scaled down, which
///    is what keeps <see cref="TrayGlyph"/>'s pixel hinting alive at 16px.
///
/// Like <see cref="TrayGlyphTests"/> these are geometric and photometric, not golden images: a
/// golden image would break on any GDI+ rasteriser change while saying nothing about whether the
/// icon is actually legible.</summary>
public class AppIconArtTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public AppIconArtTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    /// <summary>The two ends of the range the shell asks for. 16 is where every pixel decision
    /// shows, 256 is where the art has to still look deliberate rather than blown up.</summary>
    public static TheoryData<int> BothExtremes => new() { 16, 256 };

    /// <summary>Relative luminance, WCAG 2.x. Used for contrast ratios below rather than a naive
    /// channel average, because "legible" is a contrast question and contrast is defined on this.</summary>
    private static double Luminance(Color c)
    {
        static double Channel(int v)
        {
            double s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }

    private static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(48)]
    [InlineData(64)]
    [InlineData(256)]
    public void DrawsASquareFrameAtTheRequestedPixelSize(int size)
    {
        using var bmp = AppIconArt.Draw(size);
        _out.WriteLine($"{size}px requested -> {bmp.Width}x{bmp.Height}");
        Assert.Equal(size, bmp.Width);
        Assert.Equal(size, bmp.Height);
    }

    /// <summary>The tile is opaque art, not a tinted overlay: the centre is fully covered in every
    /// frame. <c>AppIconTests.EveryFrameDecodesToRealPixels</c> asserts the same thing on the
    /// shipped file, and would silently pass on an all-transparent frame if this did not hold.</summary>
    [Theory]
    [MemberData(nameof(BothExtremes))]
    public void TheTileIsOpaqueAtItsCentre(int size)
    {
        using var bmp = AppIconArt.Draw(size);
        var centre = bmp.GetPixel(size / 2, size / 2);
        _out.WriteLine($"{size}px centre = {centre}");
        Assert.Equal(255, centre.A);
    }

    /// <summary>The corners prove the rounding happened. A square tile would put opaque ink in all
    /// four extreme pixels; a rounded one leaves them empty, which is the whole visual difference
    /// between a Fluent app tile and a coloured rectangle.</summary>
    [Theory]
    [MemberData(nameof(BothExtremes))]
    public void TheCornersAreTransparentBecauseTheTileIsRounded(int size)
    {
        using var bmp = AppIconArt.Draw(size);
        (int X, int Y)[] corners =
            [(0, 0), (size - 1, 0), (0, size - 1), (size - 1, size - 1)];

        foreach (var (x, y) in corners)
        {
            var pixel = bmp.GetPixel(x, y);
            _out.WriteLine($"{size}px corner ({x},{y}) = {pixel}");
            Assert.Equal(0, pixel.A);
        }
    }

    /// <summary>The speaker is actually on the tile. Probed by colour, not position: the glyph is
    /// the only near-white thing in the art (the tile's own lightest colour is its edge highlight,
    /// far below this threshold), so counting near-white pixels inside the tile distinguishes
    /// "the glyph was composited" from "the tile was drawn and nothing else".</summary>
    [Theory]
    [MemberData(nameof(BothExtremes))]
    public void TheSpeakerGlyphIsCompositedOntoTheTile(int size)
    {
        using var bmp = AppIconArt.Draw(size);

        // Inset past the edge highlight so only the tile's interior counts.
        int inset = Math.Max(1, size / 8);
        int nearWhite = 0;
        for (int y = inset; y < size - inset; y++)
            for (int x = inset; x < size - inset; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (p.R >= 220 && p.G >= 220 && p.B >= 220) nearWhite++;
            }

        _out.WriteLine($"{size}px: {nearWhite} near-white pixels inside the tile "
            + $"({inset}..{size - inset})");
        Assert.True(nearWhite > 0, $"no glyph ink found inside the {size}px tile");
    }

    /// <summary>The glyph is centred: each half of the tile carries a comparable share of the ink.
    /// A glyph pinned to a corner, or one drawn at the wrong offset, lands lopsided here.
    ///
    /// The tolerance is generous on purpose — the speaker's arcs put more ink right of centre than
    /// the body puts left, by design — but a glyph off by more than a fifth of the tile is not
    /// centred by any reading.</summary>
    [Theory]
    [MemberData(nameof(BothExtremes))]
    public void TheGlyphSitsInTheMiddleOfTheTile(int size)
    {
        using var bmp = AppIconArt.Draw(size);

        double sumX = 0, sumY = 0;
        int ink = 0;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (p.R < 220 || p.G < 220 || p.B < 220) continue;
                sumX += x; sumY += y; ink++;
            }

        Assert.True(ink > 0, "no glyph ink at all");
        double cx = sumX / ink, cy = sumY / ink;
        _out.WriteLine($"{size}px: glyph centroid ({cx:F1},{cy:F1}) of {ink} px, tile centre {size / 2.0:F1}");
        Assert.InRange(cx, size * 0.3, size * 0.7);
        Assert.InRange(cy, size * 0.3, size * 0.7);
    }

    /// <summary>Legibility, as three contrast ratios rather than an opinion about colours.
    ///
    /// The glyph must read on the tile (4.5:1 is WCAG AA for body text, and a 16px speaker is at
    /// least that demanding). The tile's fill must read against a WHITE desktop, and the tile's own
    /// lightest pixel — its edge highlight, which exists for exactly this reason — must read
    /// against a BLACK one. Without the highlight a graphite tile on a dark wallpaper is a
    /// silhouette with no silhouette, which is the failure this pins down.
    ///
    /// The rim is measured only in the tile's PERIMETER band, where the glyph never reaches.
    /// Excluding near-white pixels is not enough: the speaker's antialiased fringe produces plenty
    /// of light-but-not-white pixels, and a rim probe that finds one is measuring the speaker's
    /// contrast with black, which says nothing about whether the tile's outline is visible.</summary>
    [Theory]
    [MemberData(nameof(BothExtremes))]
    public void TheTileReadsAgainstBothALightAndADarkDesktop(int size)
    {
        using var bmp = AppIconArt.Draw(size);

        var fill = bmp.GetPixel(size / 2, (int)(size * 0.75)); // below the glyph, pure tile
        int band = Math.Max(1, size / 16); // ~2 design units: the rim's width plus its antialiasing
        Color rim = Color.Black, glyph = Color.Black;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (p.A != 255) continue;
                if (Luminance(p) > Luminance(glyph)) glyph = p;

                bool onPerimeter = x < band || y < band || x >= size - band || y >= size - band;
                if (onPerimeter && Luminance(p) > Luminance(rim)) rim = p;
            }

        double onTile = Contrast(glyph, fill);
        double onWhite = Contrast(fill, Color.White);
        double onBlack = Contrast(rim, Color.Black);
        _out.WriteLine($"{size}px: fill {fill}, glyph {glyph}, rim {rim}");
        _out.WriteLine($"{size}px: glyph/tile {onTile:F2}:1, tile/white {onWhite:F2}:1, "
            + $"rim/black {onBlack:F2}:1");

        Assert.True(onTile >= 4.5, $"the speaker does not read on its own tile ({onTile:F2}:1)");
        Assert.True(onWhite >= 3.0, $"the tile vanishes on a light desktop ({onWhite:F2}:1)");
        Assert.True(onBlack >= 3.0, $"the tile vanishes on a dark desktop ({onBlack:F2}:1)");
    }

    /// <summary>The reason each frame is drawn at its own size instead of rendering 256 once and
    /// scaling down. <see cref="TrayGlyph"/> snaps its arcs to the pixel grid at the size it is
    /// asked for; resampling a large render throws that away and the three arcs smear into one grey
    /// blob at 16px.
    ///
    /// The observable consequence is that the arc region right of the speaker cone contains pixels
    /// of the EXACT glyph colour — a fully opaque stroke core. Any downscale blends every one of
    /// them with the tile beneath, so this is zero on a resampled icon and non-zero on a correctly
    /// composited one. Measured only right of the cone so the (always solid) speaker body cannot
    /// satisfy it.</summary>
    [Fact]
    public void TheGlyphIsCompositedAtNativeSizeSoItsArcsStayOnThePixelGrid()
    {
        const int Size = 16;
        using var bmp = AppIconArt.Draw(Size);

        // The glyph occupies the tile's interior; its cone ends 15/32 of the way across it.
        int coneEdge = (int)Math.Ceiling(Size * 0.5);

        // The ink is the lightest colour in the frame: a fully struck glyph pixel, with nothing of
        // the tile blended into it. Anything partially covered lands darker.
        Color glyph = Color.Black;
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (Luminance(p) > Luminance(glyph)) glyph = p;
            }

        int cores = 0;
        for (int y = 0; y < Size; y++)
            for (int x = coneEdge; x < Size; x++)
                if (bmp.GetPixel(x, y).ToArgb() == glyph.ToArgb()) cores++;

        _out.WriteLine($"16px: {cores} pixels of exact glyph colour {glyph} right of x={coneEdge}");
        Assert.True(cores > 0,
            "no fully struck arc pixel at 16px — the glyph was resampled and the arcs will smear");
    }

    /// <summary>The frame list is the icon's contract with the shell, and the generator reads it
    /// from here. Ascending, distinct, and inside what an .ico can address.</summary>
    [Fact]
    public void FrameSizesAreAscendingDistinctAndEncodable()
    {
        var sizes = AppIconArt.FrameSizes;
        _out.WriteLine($"frame sizes: {string.Join(", ", sizes)}");

        Assert.NotEmpty(sizes);
        Assert.Equal(sizes.Distinct().Count(), sizes.Count);
        for (int i = 1; i < sizes.Count; i++)
            Assert.True(sizes[i] > sizes[i - 1], $"{sizes[i]} does not follow {sizes[i - 1]}");
        Assert.All(sizes, s => Assert.InRange(s, 1, 256));
    }

    /// <summary>A size of zero or less has no frame to draw, and a caller that computed one has a
    /// bug worth surfacing rather than a blank icon worth shipping.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void DrawRejectsSizesItCannotProduce(int size) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => AppIconArt.Draw(size));
}
