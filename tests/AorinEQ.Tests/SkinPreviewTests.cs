using System.Drawing;
using System.Security.Cryptography;
using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>The preview.png a gallery lists a skin by. Every assertion here reads real decoded
/// pixels out of the file that was written — the point of a preview is what it LOOKS like, which
/// a "did it write something" test would not catch.</summary>
public class SkinPreviewTests : IDisposable
{
    private readonly string _dir;
    private readonly ITestOutputHelper _out;

    public SkinPreviewTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), "aorineq-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>A skin whose empty layer is red and full layer is green, so a sampled pixel says
    /// exactly which layer produced it.</summary>
    private string MakeSkin(string name, int width = 300, int height = 100, string? json = null)
    {
        var folder = Path.Combine(_dir, name);
        Directory.CreateDirectory(folder);
        RealPngs.WriteSolid(Path.Combine(folder, "empty.png"), width, height, Color.Red);
        RealPngs.WriteSolid(Path.Combine(folder, "full.png"), width, height, Color.Lime);
        if (json is not null)
            File.WriteAllText(Path.Combine(folder, "skin.json"), json);
        return folder;
    }

    /// <summary>A full-layer image shaped the way a ranged skin's is: the lit bar between
    /// barStart and barEnd, fully transparent everywhere else.</summary>
    private static void WriteBarOnly(string path, int width, int height, int barStart, int barEnd, Color bar)
    {
        using var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            using var brush = new SolidBrush(bar);
            g.FillRectangle(brush, new Rectangle(barStart, 0, barEnd - barStart, height));
        }
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static Bitmap Open(string path)
    {
        // Copied off disk so the test never keeps the preview file locked.
        using var stream = new MemoryStream(File.ReadAllBytes(path));
        using var image = Image.FromStream(stream);
        return new Bitmap(image);
    }

    [Fact]
    public void Preview_is_written_at_the_skins_own_logical_size()
    {
        var folder = MakeSkin("sized", 420, 96);
        var preview = Path.Combine(_dir, "sized.png");

        SkinPreview.Write(SkinLoader.Load(folder), preview);

        using var image = Open(preview);
        _out.WriteLine($"preview: {image.Width}x{image.Height}, {new FileInfo(preview).Length} bytes");
        Assert.Equal(420, image.Width);
        Assert.Equal(96, image.Height);
        Assert.True(new FileInfo(preview).Length > 0);
    }

    [Fact]
    public void Preview_uses_the_logical_frame_size_of_a_sprite_sheet_not_the_sheet_height()
    {
        var folder = Path.Combine(_dir, "sheet");
        Directory.CreateDirectory(folder);
        RealPngs.WriteFrames(Path.Combine(folder, "empty.png"), 200, 50,
            new[] { Color.Red, Color.Blue, Color.Yellow, Color.Magenta });
        RealPngs.WriteSolid(Path.Combine(folder, "full.png"), 200, 50, Color.Lime);
        File.WriteAllText(Path.Combine(folder, "skin.json"), "{ \"emptyFrames\": 4 }");
        var preview = Path.Combine(_dir, "sheet.png");

        var info = SkinLoader.Load(folder);
        Assert.True(info.IsValid);
        SkinPreview.Write(info, preview);

        using var image = Open(preview);
        _out.WriteLine($"preview: {image.Width}x{image.Height}; sheet was 200x200 with 4 frames");
        Assert.Equal(200, image.Width);
        Assert.Equal(50, image.Height);
        // The FIRST frame is the one composed — frame 0 of the empty sheet is red, not blue.
        _out.WriteLine("right-hand pixel: " + image.GetPixel(190, 25));
        Assert.Equal(Color.Red.ToArgb(), image.GetPixel(190, 25).ToArgb());
    }

    [Fact]
    public void Preview_composes_the_fill_at_the_gallery_percent()
    {
        var folder = MakeSkin("filled", 300, 100);
        var preview = Path.Combine(_dir, "filled.png");

        SkinPreview.Write(SkinLoader.Load(folder), preview);

        using var image = Open(preview);
        int fillWidth = SkinMath.FillWidth(300, SkinPreview.GalleryPercent, 0, 300);
        _out.WriteLine($"gallery percent {SkinPreview.GalleryPercent} -> fill width {fillWidth}px of 300");
        Assert.Equal(180, fillWidth);
        // Inside the fill: the full (green) layer. Outside it: the empty (red) layer.
        _out.WriteLine($"x=10 {image.GetPixel(10, 50)}  x=170 {image.GetPixel(170, 50)}  x=190 {image.GetPixel(190, 50)}  x=290 {image.GetPixel(290, 50)}");
        Assert.Equal(Color.Lime.ToArgb(), image.GetPixel(10, 50).ToArgb());
        Assert.Equal(Color.Lime.ToArgb(), image.GetPixel(fillWidth - 1, 50).ToArgb());
        Assert.Equal(Color.Red.ToArgb(), image.GetPixel(fillWidth + 1, 50).ToArgb());
        Assert.Equal(Color.Red.ToArgb(), image.GetPixel(290, 50).ToArgb());
    }

    [Fact]
    public void Preview_honours_an_explicit_percent()
    {
        var folder = MakeSkin("explicit", 300, 100);
        var preview = Path.Combine(_dir, "explicit.png");

        SkinPreview.Write(SkinLoader.Load(folder), preview, percent: 25);

        using var image = Open(preview);
        _out.WriteLine($"25%: x=70 {image.GetPixel(70, 50)}  x=80 {image.GetPixel(80, 50)}");
        Assert.Equal(Color.Lime.ToArgb(), image.GetPixel(70, 50).ToArgb());
        Assert.Equal(Color.Red.ToArgb(), image.GetPixel(80, 50).ToArgb());
    }

    [Fact]
    public void Preview_honours_the_skins_fill_range()
    {
        // Bar lives in [100..200] of a 300-wide image; the decoration outside it is empty-layer
        // artwork that must keep showing at any percent. Shaped like a real ranged skin: full.png
        // paints ONLY the bar's lit pixels and is transparent everywhere else, so this also proves
        // the full layer is composited OVER the empty one rather than copied through it.
        var folder = Path.Combine(_dir, "ranged");
        Directory.CreateDirectory(folder);
        RealPngs.WriteSolid(Path.Combine(folder, "empty.png"), 300, 100, Color.Red);
        WriteBarOnly(Path.Combine(folder, "full.png"), 300, 100, 100, 200, Color.Lime);
        File.WriteAllText(Path.Combine(folder, "skin.json"),
            "{ \"fillStartX\": 100, \"fillEndX\": 200 }");
        var preview = Path.Combine(_dir, "ranged.png");

        var info = SkinLoader.Load(folder);
        SkinPreview.Write(info, preview);

        using var image = Open(preview);
        int fillWidth = SkinMath.FillWidth(300, SkinPreview.GalleryPercent, 100, 200);
        _out.WriteLine($"range 100..200 at {SkinPreview.GalleryPercent}% -> fill edge {fillWidth}");
        Assert.Equal(160, fillWidth);
        Assert.Equal(Color.Red.ToArgb(), image.GetPixel(50, 50).ToArgb());        // left decoration
        Assert.Equal(Color.Lime.ToArgb(), image.GetPixel(150, 50).ToArgb());      // inside the lit bar
        Assert.Equal(Color.Red.ToArgb(), image.GetPixel(170, 50).ToArgb());       // past the fill edge
        Assert.Equal(Color.Red.ToArgb(), image.GetPixel(250, 50).ToArgb());       // right decoration
    }

    [Fact]
    public void Preview_draws_the_percent_text_when_the_skin_shows_one()
    {
        var withText = MakeSkin("with-text", 300, 100,
            "{ \"percentText\": { \"show\": true, \"x\": 200, \"y\": 30, \"fontSize\": 48, \"color\": \"#FF0000FF\" } }");
        var withoutText = MakeSkin("without-text", 300, 100);
        var a = Path.Combine(_dir, "with-text.png");
        var b = Path.Combine(_dir, "without-text.png");

        SkinPreview.Write(SkinLoader.Load(withText), a);
        SkinPreview.Write(SkinLoader.Load(withoutText), b);

        using var textImage = Open(a);
        using var plainImage = Open(b);
        // Count pixels that are neither of the two layer colours: those can only be glyph pixels.
        int glyphPixels = 0, plainGlyphPixels = 0;
        for (int x = 0; x < 300; x++)
            for (int y = 0; y < 100; y++)
            {
                if (!IsLayerColour(textImage.GetPixel(x, y))) glyphPixels++;
                if (!IsLayerColour(plainImage.GetPixel(x, y))) plainGlyphPixels++;
            }
        _out.WriteLine($"non-layer pixels: with text {glyphPixels}, without text {plainGlyphPixels}");
        Assert.Equal(0, plainGlyphPixels);
        Assert.True(glyphPixels > 100, $"expected the number '60' to be drawn, found {glyphPixels} glyph pixels");

        static bool IsLayerColour(Color c) =>
            c.ToArgb() == Color.Red.ToArgb() || c.ToArgb() == Color.Lime.ToArgb();
    }

    [Fact]
    public void Preview_of_the_same_skin_is_byte_for_byte_deterministic()
    {
        // A gallery re-uploads previews; identical input has to give an identical file or every
        // re-export looks like a change.
        var folder = MakeSkin("stable", 300, 100,
            "{ \"percentText\": { \"show\": true, \"x\": 10, \"y\": 20, \"fontSize\": 32, " +
            "\"outlineColor\": \"#FF000000\", \"outlineWidth\": 2, \"shadowColor\": \"#80000000\" } }");
        var first = Path.Combine(_dir, "stable-1.png");
        var second = Path.Combine(_dir, "stable-2.png");

        var info = SkinLoader.Load(folder);
        SkinPreview.Write(info, first);
        SkinPreview.Write(SkinLoader.Load(folder), second); // reloaded, not reused

        var hashA = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(first)));
        var hashB = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(second)));
        _out.WriteLine($"sha256 a={hashA}\nsha256 b={hashB}");
        Assert.Equal(hashA, hashB);
    }

    [Fact]
    public void Preview_leaves_no_lock_on_the_skins_own_files()
    {
        // The export path zips the folder right after generating the preview: a lingering handle
        // from the decode would make that fail.
        var folder = MakeSkin("unlocked", 300, 100);
        SkinPreview.Write(SkinLoader.Load(folder), Path.Combine(_dir, "unlocked.png"));

        foreach (var file in Directory.GetFiles(folder))
            using (File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                _out.WriteLine("exclusively reopened " + Path.GetFileName(file));
        // Overwritable too, which File.Copy during a save would need.
        File.Delete(Path.Combine(folder, "empty.png"));
        _out.WriteLine("deleted empty.png with no sharing violation");
    }

    [Fact]
    public void Preview_can_be_written_over_an_existing_one()
    {
        var folder = MakeSkin("overwrite", 300, 100);
        var preview = Path.Combine(_dir, "overwrite.png");
        SkinPreview.Write(SkinLoader.Load(folder), preview, percent: 100);
        var firstLength = new FileInfo(preview).Length;

        SkinPreview.Write(SkinLoader.Load(folder), preview, percent: 0);

        using var image = Open(preview);
        _out.WriteLine($"rewrote {firstLength} -> {new FileInfo(preview).Length} bytes; x=10 is {image.GetPixel(10, 50)}");
        Assert.Equal(Color.Red.ToArgb(), image.GetPixel(10, 50).ToArgb()); // 0%: no fill anywhere
    }

    [Fact]
    public void Preview_composes_a_gif_layer()
    {
        var folder = Path.Combine(_dir, "gif-skin");
        Directory.CreateDirectory(folder);
        RealPngs.WriteGif(Path.Combine(folder, "empty.gif"), 200, 60, Color.Red);
        RealPngs.WriteGif(Path.Combine(folder, "full.gif"), 200, 60, Color.Lime);
        var preview = Path.Combine(_dir, "gif-skin.png");

        var info = SkinLoader.Load(folder);
        Assert.True(info.IsValid);
        SkinPreview.Write(info, preview);

        using var image = Open(preview);
        _out.WriteLine($"gif preview {image.Width}x{image.Height}; x=10 {image.GetPixel(10, 30)} x=190 {image.GetPixel(190, 30)}");
        Assert.Equal(200, image.Width);
        Assert.Equal(60, image.Height);
        Assert.Equal(Color.Lime.ToArgb(), image.GetPixel(10, 30).ToArgb());
        Assert.Equal(Color.Red.ToArgb(), image.GetPixel(190, 30).ToArgb());
    }

    [Fact]
    public void Preview_keeps_transparency_from_the_artwork()
    {
        var folder = Path.Combine(_dir, "transparent");
        Directory.CreateDirectory(folder);
        RealPngs.WriteSolid(Path.Combine(folder, "empty.png"), 100, 40, Color.Transparent);
        RealPngs.WriteSolid(Path.Combine(folder, "full.png"), 100, 40, Color.Transparent);
        var preview = Path.Combine(_dir, "transparent.png");

        SkinPreview.Write(SkinLoader.Load(folder), preview);

        using var image = Open(preview);
        _out.WriteLine("alpha at 10,20: " + image.GetPixel(10, 20).A);
        Assert.Equal(0, image.GetPixel(10, 20).A); // a shaped skin must not gain a black box
    }

    [Fact]
    public void Preview_of_an_invalid_skin_is_refused()
    {
        var folder = Path.Combine(_dir, "broken");
        Directory.CreateDirectory(folder);
        RealPngs.WriteSolid(Path.Combine(folder, "empty.png"), 100, 40, Color.Red); // no full layer

        var info = SkinLoader.Load(folder);
        var ex = Assert.Throws<InvalidOperationException>(
            () => SkinPreview.Write(info, Path.Combine(_dir, "broken.png")));
        _out.WriteLine("refused: " + ex.Message);
        Assert.False(File.Exists(Path.Combine(_dir, "broken.png")));
    }

    [Fact]
    public void Preview_of_undecodable_artwork_fails_as_InvalidOperationException()
    {
        // A truncated download: the PNG signature and IHDR are intact, so SkinLoader (which only
        // reads headers) accepts the skin, and the failure only happens at decode time. It must
        // surface as the one type callers degrade on, not as a raw GDI+ exception.
        var folder = Path.Combine(_dir, "corrupt");
        Directory.CreateDirectory(folder);
        TestPngs.WriteHeaderOnly(Path.Combine(folder, "empty.png"), 300, 100);
        TestPngs.WriteHeaderOnly(Path.Combine(folder, "full.png"), 300, 100);

        var info = SkinLoader.Load(folder);
        Assert.True(info.IsValid); // the loader only reads headers
        var ex = Record.Exception(() => SkinPreview.Write(info, Path.Combine(_dir, "corrupt.png")));
        _out.WriteLine("decode failure surfaced as: " + (ex?.GetType().Name ?? "<none>") + " " + ex?.Message);
        Assert.IsType<InvalidOperationException>(ex);
    }

    [Fact]
    public void Preview_with_an_uninstalled_font_still_renders_the_number()
    {
        var folder = MakeSkin("odd-font", 300, 100,
            "{ \"percentText\": { \"show\": true, \"x\": 20, \"y\": 20, \"fontSize\": 40, " +
            "\"fontFamily\": \"No Such Font Installed Anywhere\", \"color\": \"#FF0000FF\" } }");
        var preview = Path.Combine(_dir, "odd-font.png");

        SkinPreview.Write(SkinLoader.Load(folder), preview);

        using var image = Open(preview);
        int glyphPixels = 0;
        for (int x = 0; x < 300; x++)
            for (int y = 0; y < 100; y++)
            {
                var c = image.GetPixel(x, y);
                if (c.ToArgb() != Color.Red.ToArgb() && c.ToArgb() != Color.Lime.ToArgb()) glyphPixels++;
            }
        _out.WriteLine($"glyph pixels with a missing font: {glyphPixels}");
        Assert.True(glyphPixels > 100, "a missing font must fall back, not silently drop the number");
    }

    [Theory]
    [InlineData("left")]
    [InlineData("center")]
    [InlineData("right")]
    public void Preview_text_alignment_moves_the_number_the_way_the_OSD_does(string align)
    {
        var folder = MakeSkin("align-" + align, 300, 100,
            "{ \"percentText\": { \"show\": true, \"x\": 150, \"y\": 20, \"fontSize\": 40, " +
            $"\"color\": \"#FF0000FF\", \"align\": \"{align}\" }} }}");
        var preview = Path.Combine(_dir, "align-" + align + ".png");

        SkinPreview.Write(SkinLoader.Load(folder), preview);

        using var image = Open(preview);
        int minX = int.MaxValue, maxX = int.MinValue;
        for (int x = 0; x < 300; x++)
            for (int y = 0; y < 100; y++)
            {
                var c = image.GetPixel(x, y);
                if (c.ToArgb() == Color.Red.ToArgb() || c.ToArgb() == Color.Lime.ToArgb()) continue;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
            }
        _out.WriteLine($"align={align}: glyph ink spans x {minX}..{maxX} around the anchor x=150");
        Assert.True(minX < maxX, "the number must actually be drawn");
        switch (align)
        {
            case "left": Assert.True(minX >= 148, $"left-aligned ink should start at the anchor, started at {minX}"); break;
            case "right": Assert.True(maxX <= 152, $"right-aligned ink should end at the anchor, ended at {maxX}"); break;
            default: Assert.InRange((minX + maxX) / 2, 135, 165); break;
        }
    }
}
