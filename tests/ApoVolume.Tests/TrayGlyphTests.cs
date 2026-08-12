using System.Drawing;
using ApoVolume.Core;

namespace ApoVolume.Tests;

/// <summary>The tray glyph is the only part of the app the user sees without opening anything, so
/// both halves of it are pinned here: the volume→arc-count mapping (a pure function, mirroring how
/// Windows' own volume icon steps) and the drawn pixels (the geometry the user approved from the
/// prototype contact sheet).
///
/// The drawing assertions probe the 32x32 design grid the glyph is authored on, scaled up to a
/// large bitmap so a single design unit is many pixels and a probe can't land on an antialiased
/// edge by accident. They are deliberately geometric rather than a golden-image comparison: a
/// golden image would break on any GDI+ rasteriser change while saying nothing about whether the
/// speaker, the arcs, or the mute cross are actually where they belong.</summary>
public class TrayGlyphTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public TrayGlyphTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    /// <summary>Every threshold boundary named by the design: silent, the three bands, and both
    /// ends of the scale. 33/34 and 66/67 are the two places the icon changes mid-scale, so both
    /// sides of each are asserted.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(33, 1)]
    [InlineData(34, 2)]
    [InlineData(66, 2)]
    [InlineData(67, 3)]
    [InlineData(100, 3)]
    public void ArcCountStepsAtTheDesignedBoundaries(int percent, int expected)
    {
        _out.WriteLine($"{percent}% -> {TrayGlyph.ArcCount(percent)} arcs (expected {expected})");
        Assert.Equal(expected, TrayGlyph.ArcCount(percent));
    }

    /// <summary>Percent comes from device state that can be out of range (a device reporting
    /// something odd, or arithmetic on a step that overshoots), and an out-of-range arc count
    /// would throw inside the renderer. Clamping happens here so the icon degrades to "silent" or
    /// "full" instead.</summary>
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(-100, 0)]
    [InlineData(101, 3)]
    [InlineData(int.MinValue, 0)]
    [InlineData(int.MaxValue, 3)]
    public void ArcCountClampsOutsideZeroToOneHundred(int percent, int expected)
    {
        _out.WriteLine($"{percent}% -> {TrayGlyph.ArcCount(percent)} arcs (expected {expected})");
        Assert.Equal(expected, TrayGlyph.ArcCount(percent));
    }

    /// <summary>The count never exceeds what the glyph can draw, at any input.</summary>
    [Fact]
    public void ArcCountNeverLeavesTheDrawableRange()
    {
        for (int percent = -50; percent <= 150; percent++)
        {
            int arcs = TrayGlyph.ArcCount(percent);
            Assert.InRange(arcs, 0, TrayGlyph.MaxArcs);
        }
    }

    /// <summary>Monotonic: turning the volume up never removes an arc.</summary>
    [Fact]
    public void ArcCountRisesMonotonicallyWithVolume()
    {
        int previous = 0;
        for (int percent = 0; percent <= 100; percent++)
        {
            int arcs = TrayGlyph.ArcCount(percent);
            Assert.True(arcs >= previous, $"{percent}% dropped from {previous} to {arcs} arcs");
            previous = arcs;
        }
        Assert.Equal(TrayGlyph.MaxArcs, previous);
    }

    // ---- drawing ----------------------------------------------------------------------------

    /// <summary>Design-grid probe points, in 32x32 units. The speaker body and cone are always
    /// drawn; each arc's outermost point sits on the +x axis through the arc centre (15+2, 16),
    /// at half its diameter (8/15/22 design units); the mute cross's two strokes meet at (23, 16).</summary>
    private const float BodyX = 6.5f, BodyY = 16f;
    private const float ConeX = 14f, ConeY = 16f;
    private const float Arc1X = 21f, Arc2X = 24.5f, Arc3X = 28f, ArcY = 16f;
    private const float CrossX = 23f, CrossY = 16f;

    private const int Probe = 256; // 8 px per design unit — probes can't hit an antialiased edge

    private static Bitmap DrawProbe(int arcs, bool muted) =>
        TrayGlyph.Draw(arcs, muted, Color.White, Probe);

    private static bool Opaque(Bitmap bmp, float designX, float designY)
    {
        float u = bmp.Width / 32f;
        return bmp.GetPixel((int)(designX * u), (int)(designY * u)).A > 128;
    }

    private static int InkPixels(Bitmap bmp)
    {
        int ink = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y).A > 0) ink++;
        return ink;
    }

    /// <summary>The speaker itself is the constant part of the glyph — present in every state,
    /// including muted and silent.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    public void SpeakerBodyAndConeAreDrawnInEveryState(int arcs, bool muted)
    {
        using var bmp = DrawProbe(arcs, muted);
        Assert.True(Opaque(bmp, BodyX, BodyY), "speaker body missing");
        Assert.True(Opaque(bmp, ConeX, ConeY), "speaker cone missing");
    }

    /// <summary>The whole point of the release: the arc count on screen is the arc count asked
    /// for. Each arc is probed at its own radius, so a glyph that drew three arcs for every level
    /// (or one fat one) fails.</summary>
    [Theory]
    [InlineData(0, false, false, false)]
    [InlineData(1, true, false, false)]
    [InlineData(2, true, true, false)]
    [InlineData(3, true, true, true)]
    public void ArcsAppearOneRadiusAtATime(int arcs, bool first, bool second, bool third)
    {
        using var bmp = DrawProbe(arcs, muted: false);
        _out.WriteLine($"arcs={arcs}: inner={Opaque(bmp, Arc1X, ArcY)} "
            + $"middle={Opaque(bmp, Arc2X, ArcY)} outer={Opaque(bmp, Arc3X, ArcY)}");
        Assert.Equal(first, Opaque(bmp, Arc1X, ArcY));
        Assert.Equal(second, Opaque(bmp, Arc2X, ArcY));
        Assert.Equal(third, Opaque(bmp, Arc3X, ArcY));
    }

    /// <summary>Muted is a cross where the arcs would be. Only the outer arc's radius is probed
    /// for absence: the cross deliberately occupies the same region as the inner two, so a probe
    /// there would say nothing. That the arcs are gone entirely is what
    /// <see cref="MutedIgnoresTheArcCount"/> proves.</summary>
    [Fact]
    public void MutedDrawsTheCrossOverWhereTheArcsWouldBe()
    {
        using var bmp = DrawProbe(0, muted: true);
        Assert.True(Opaque(bmp, CrossX, CrossY), "mute cross missing");
        Assert.False(Opaque(bmp, Arc3X, ArcY), "the outer arc is still drawn while muted");
    }

    /// <summary>Mute outranks the volume level, pixel for pixel: the renderer folds every muted
    /// state onto one cache entry, which is only sound if the arc count genuinely cannot reach the
    /// glyph while muted.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MutedIgnoresTheArcCount(int arcs)
    {
        using var silent = DrawProbe(0, muted: true);
        using var loud = DrawProbe(arcs, muted: true);

        int differing = 0;
        for (int y = 0; y < silent.Height; y++)
            for (int x = 0; x < silent.Width; x++)
                if (silent.GetPixel(x, y).ToArgb() != loud.GetPixel(x, y).ToArgb()) differing++;

        _out.WriteLine($"muted at {arcs} arcs: {differing} pixels differ from muted at 0 arcs");
        Assert.Equal(0, differing);
        Assert.True(Opaque(loud, CrossX, CrossY));
    }

    /// <summary>More volume is more ink — the states are visually distinct at a glance, which is
    /// the only way a 16px monochrome glyph communicates anything.</summary>
    [Fact]
    public void EachStateDrawsMoreInkThanTheOneBelowIt()
    {
        var counts = new int[TrayGlyph.MaxArcs + 1];
        for (int arcs = 0; arcs <= TrayGlyph.MaxArcs; arcs++)
        {
            using var bmp = DrawProbe(arcs, muted: false);
            counts[arcs] = InkPixels(bmp);
            _out.WriteLine($"arcs={arcs}: {counts[arcs]} ink pixels");
        }
        for (int arcs = 1; arcs <= TrayGlyph.MaxArcs; arcs++)
            Assert.True(counts[arcs] > counts[arcs - 1],
                $"{arcs} arcs drew {counts[arcs]} px, not more than {counts[arcs - 1]} px");

        using var muted = DrawProbe(0, muted: true);
        int mutedInk = InkPixels(muted);
        _out.WriteLine($"muted: {mutedInk} ink pixels");
        Assert.True(mutedInk > counts[0], "the mute cross adds no ink over the silent speaker");
    }

    /// <summary>Theme-awareness is a contrast requirement, not a colour preference: the glyph must
    /// be light enough to read on a dark taskbar and dark enough to read on a light one. Asserted
    /// on the drawn pixels rather than on the returned Color so a drawing path that ignored the
    /// colour (or blended it away) is caught.</summary>
    [Theory]
    [InlineData(false, true)]  // dark taskbar -> light glyph
    [InlineData(true, false)]  // light taskbar -> dark glyph
    public void GlyphColourContrastsWithTheTaskbarItSitsOn(bool lightTaskbar, bool expectLightGlyph)
    {
        var colour = TrayGlyph.GlyphColor(lightTaskbar);
        using var bmp = TrayGlyph.Draw(3, muted: false, colour, Probe);
        var pixel = bmp.GetPixel((int)(BodyX * Probe / 32f), (int)(BodyY * Probe / 32f));

        double luminance = (0.2126 * pixel.R) + (0.7152 * pixel.G) + (0.0722 * pixel.B);
        _out.WriteLine($"lightTaskbar={lightTaskbar}: body pixel {pixel}, luminance {luminance:F1}");
        Assert.Equal(255, pixel.A);
        if (expectLightGlyph) Assert.True(luminance > 200, $"glyph too dark for a dark taskbar ({luminance:F1})");
        else Assert.True(luminance < 60, $"glyph too light for a light taskbar ({luminance:F1})");
    }

    /// <summary>Every size the shell can ask for renders at exactly that size — the glyph is
    /// authored on a grid and scaled, never letterboxed or cropped.</summary>
    [Theory]
    [InlineData(16)]   // 100% DPI
    [InlineData(20)]   // 125%
    [InlineData(24)]   // 150%
    [InlineData(32)]   // 200%
    public void DrawsAtTheRequestedPixelSizeWithVisibleInk(int size)
    {
        using var bmp = TrayGlyph.Draw(3, muted: false, Color.White, size);
        int ink = InkPixels(bmp);
        _out.WriteLine($"{size}px: {bmp.Width}x{bmp.Height}, {ink} ink pixels");
        Assert.Equal(size, bmp.Width);
        Assert.Equal(size, bmp.Height);
        Assert.True(ink > size, $"only {ink} ink pixels at {size}px — the glyph scaled away");
    }

    /// <summary>At tray sizes the arcs are barely a pixel thick and two apart, so their bounding
    /// boxes are snapped to the pixel grid — without that, every stroke is split across two pixel
    /// columns at partial alpha and the three rings render as one grey smudge. The observable
    /// consequence, and the thing that regresses if the snapping is ever dropped, is that each arc
    /// puts at least one FULLY opaque pixel on screen: measured on the sub-pixel version, 16px and
    /// 20px produce no fully opaque arc pixel at all for one or two arcs.
    ///
    /// Only the region right of the speaker cone is counted, so the (always solid) body and cone
    /// can't satisfy it.</summary>
    [Theory]
    [InlineData(16, 1)]
    [InlineData(16, 2)]
    [InlineData(16, 3)]
    [InlineData(20, 1)]
    [InlineData(20, 2)]
    [InlineData(20, 3)]
    public void ArcsLandOnThePixelGridAtTraySizes(int size, int arcs)
    {
        using var bmp = TrayGlyph.Draw(arcs, muted: false, Color.White, size);
        int coneEdge = (int)Math.Ceiling(15.0 * size / 32.0);

        int solid = 0;
        for (int y = 0; y < size; y++)
            for (int x = coneEdge; x < size; x++)
                if (bmp.GetPixel(x, y).A == 255) solid++;

        _out.WriteLine($"{size}px, {arcs} arcs: {solid} fully opaque pixels right of x={coneEdge}");
        Assert.True(solid >= arcs,
            $"{size}px drew {arcs} arcs but only {solid} fully opaque pixels — the arcs are off-grid and will smear");
    }

    /// <summary>The glyph is transparent everywhere it isn't drawn: the taskbar shows through,
    /// and the corners of the design grid are never touched.</summary>
    [Fact]
    public void BackgroundStaysTransparent()
    {
        using var bmp = DrawProbe(3, muted: false);
        Assert.Equal(0, bmp.GetPixel(0, 0).A);
        Assert.Equal(0, bmp.GetPixel(bmp.Width - 1, 0).A);
        Assert.Equal(0, bmp.GetPixel(0, bmp.Height - 1).A);
        Assert.Equal(0, bmp.GetPixel(bmp.Width - 1, bmp.Height - 1).A);
        Assert.True(InkPixels(bmp) < bmp.Width * bmp.Height, "the whole bitmap is ink");
    }

    /// <summary>Fail fast on arguments the caller can only produce by bypassing
    /// <see cref="TrayGlyph.ArcCount"/> — a silently clamped arc count would hide the bug.</summary>
    [Theory]
    [InlineData(-1, 16)]
    [InlineData(4, 16)]
    [InlineData(0, 0)]
    [InlineData(0, -16)]
    public void DrawRejectsImpossibleArguments(int arcs, int size)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TrayGlyph.Draw(arcs, false, Color.White, size));
    }
}
