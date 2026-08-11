using ApoVolume.Core;
using Xunit;
using Xunit.Abstractions;

namespace ApoVolume.Tests;

public class SkinWriterTests : IDisposable
{
    private readonly string _dir;      // temp root: contains "src" (source images) and "skins"
    private readonly string _root;     // skins root passed to Save
    private readonly string _emptySrc;
    private readonly string _fullSrc;
    private readonly ITestOutputHelper _out;

    public SkinWriterTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), "apo-volume-tests-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_dir, "skins");
        var src = Path.Combine(_dir, "src");
        Directory.CreateDirectory(src);
        _emptySrc = Path.Combine(src, "my-empty.png");
        _fullSrc = Path.Combine(src, "my-full.png");
        TestPngs.Write(_emptySrc, 300, 100);
        TestPngs.Write(_fullSrc, 300, 100);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Save_roundtrips_through_SkinLoader()
    {
        var folder = SkinWriter.Save(_root, "my-skin", _emptySrc, _fullSrc, new SkinConfig(new SkinText(true, 10, 5), 1.5));
        var info = SkinLoader.Load(folder);
        _out.WriteLine($"loaded: valid={info.IsValid} err={info.Error} {info.Width}x{info.Height} scale={info.Scale} text={info.Text}");
        Assert.True(info.IsValid);
        Assert.Equal(300, info.Width);
        Assert.Equal(100, info.Height);
        Assert.Equal(1.5, info.Scale);
        Assert.Equal(new SkinText(true, 10, 5), info.Text);
    }

    [Fact]
    public void Save_with_defaults_omits_and_removes_stale_skin_json()
    {
        SkinWriter.Save(_root, "my-skin", _emptySrc, _fullSrc, new SkinConfig(new SkinText(true, 10, 5), 1.5));
        Assert.True(File.Exists(Path.Combine(_root, "my-skin", "skin.json")));

        var folder = SkinWriter.Save(_root, "my-skin", _emptySrc, _fullSrc, new SkinConfig(null, 1.0));
        _out.WriteLine("second save with defaults; skin.json present: " + File.Exists(Path.Combine(folder, "skin.json")));
        Assert.False(File.Exists(Path.Combine(folder, "skin.json")));
        Assert.True(SkinLoader.Load(folder).IsValid);
    }

    [Fact]
    public void Save_in_place_with_destination_paths_as_source_keeps_images()
    {
        var folder = SkinWriter.Save(_root, "my-skin", _emptySrc, _fullSrc, new SkinConfig(null, 1.0));
        var destEmpty = Path.Combine(folder, "empty.png");
        var destFull = Path.Combine(folder, "full.png");

        // Editing an existing skin without replacing its images: sources ARE the destinations.
        var again = SkinWriter.Save(_root, "my-skin", destEmpty, destFull, new SkinConfig(new SkinText(true, 1, 2), 2.0));
        var info = SkinLoader.Load(again);
        _out.WriteLine($"in-place re-save: valid={info.IsValid} scale={info.Scale}");
        Assert.True(info.IsValid);
        Assert.Equal(2.0, info.Scale);
    }

    [Fact]
    public void Save_overwrites_existing_folder_images()
    {
        SkinWriter.Save(_root, "my-skin", _emptySrc, _fullSrc, new SkinConfig(null, 1.0));
        var biggerEmpty = Path.Combine(_dir, "src", "big-empty.png");
        var biggerFull = Path.Combine(_dir, "src", "big-full.png");
        TestPngs.Write(biggerEmpty, 500, 200);
        TestPngs.Write(biggerFull, 500, 200);

        var folder = SkinWriter.Save(_root, "my-skin", biggerEmpty, biggerFull, new SkinConfig(null, 1.0));
        var info = SkinLoader.Load(folder);
        _out.WriteLine($"after overwrite: {info.Width}x{info.Height}");
        Assert.Equal(500, info.Width);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("bad/name", false)]
    [InlineData("bad\\name", false)]
    [InlineData("bad:name", false)]
    [InlineData("con", false)]
    [InlineData("COM1", false)]
    [InlineData("NUL.txt", false)]
    [InlineData("com1.png", false)]
    [InlineData("trailing.", false)]
    [InlineData("ok-name", true)]
    [InlineData("My Skin 2", true)]
    public void ValidateName_matrix(string name, bool expectValid)
    {
        var error = SkinWriter.ValidateName(name);
        _out.WriteLine($"'{name}' -> {(error ?? "<valid>")}");
        Assert.Equal(expectValid, error is null);
    }

    [Fact]
    public void Save_with_invalid_name_throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => SkinWriter.Save(_root, "bad/name", _emptySrc, _fullSrc, new SkinConfig(null, 1.0)));
        _out.WriteLine("rejected: " + ex.Message);
    }

    [Fact]
    public void Save_gif_source_lands_as_gif_and_removes_stale_png_variant()
    {
        // First save with PNG sources.
        var folder = SkinWriter.Save(_root, "my-skin", _emptySrc, _fullSrc, new SkinConfig(null, 1.0));
        Assert.True(File.Exists(Path.Combine(folder, "full.png")));

        // Replace the full layer with a GIF: full.gif must appear AND full.png must go away,
        // or the loader's png-over-gif precedence would keep showing the old artwork.
        var gifSrc = Path.Combine(_dir, "src", "anim.gif");
        TestPngs.WriteGif(gifSrc, 300, 100);
        SkinWriter.Save(_root, "my-skin", _emptySrc, gifSrc, new SkinConfig(null, 1.0));

        _out.WriteLine($"full.gif={File.Exists(Path.Combine(folder, "full.gif"))} full.png={File.Exists(Path.Combine(folder, "full.png"))}");
        Assert.True(File.Exists(Path.Combine(folder, "full.gif")));
        Assert.False(File.Exists(Path.Combine(folder, "full.png")));

        var info = SkinLoader.Load(folder);
        Assert.True(info.IsValid);
        Assert.True(info.FullIsGif);
    }

    [Fact]
    public void Save_writes_animation_fields_and_roundtrips()
    {
        var sheetFull = Path.Combine(_dir, "src", "sheet-full.png");
        TestPngs.Write(sheetFull, 300, 800); // 8 frames of 100
        var folder = SkinWriter.Save(_root, "anim-skin", _emptySrc, sheetFull,
            new SkinConfig(null, 1.0, Fps: 12, EmptyFrames: 1, FullFrames: 8));

        var info = SkinLoader.Load(folder);
        _out.WriteLine($"roundtrip: valid={info.IsValid} fps={info.Fps} ff={info.FullFrames} logical={info.Width}x{info.Height} err={info.Error}");
        Assert.True(info.IsValid);
        Assert.Equal(12.0, info.Fps);
        Assert.Equal(8, info.FullFrames);
        Assert.Equal(100, info.Height); // logical
    }

    [Fact]
    public void Save_fill_range_roundtrips_and_null_range_is_omitted()
    {
        var wideEmpty = Path.Combine(_dir, "src", "wide-empty.png");
        var wideFull = Path.Combine(_dir, "src", "wide-full.png");
        TestPngs.Write(wideEmpty, 800, 100);
        TestPngs.Write(wideFull, 800, 100);

        var folder = SkinWriter.Save(_root, "ranged", wideEmpty, wideFull,
            new SkinConfig(null, 1.0, FillStartX: 120, FillEndX: 680));
        var info = SkinLoader.Load(folder);
        _out.WriteLine($"roundtrip range: {info.FillStartX}..{info.FillEndX}");
        Assert.True(info.IsValid);
        Assert.Equal(120, info.FillStartX);
        Assert.Equal(680, info.FillEndX);

        // Null range with another non-default field: json written, but no fill keys inside.
        var folder2 = SkinWriter.Save(_root, "unranged", wideEmpty, wideFull,
            new SkinConfig(null, 1.5));
        var json = File.ReadAllText(Path.Combine(folder2, "skin.json"));
        _out.WriteLine("json without range: " + json);
        Assert.DoesNotContain("fillStartX", json);
        Assert.Equal(800, SkinLoader.Load(folder2).FillEndX); // default = full width
    }

    [Fact]
    public void Save_text_styling_roundtrips_and_plain_text_omits_style_fields()
    {
        var styled = new SkinText(true, 10, 5,
            Color: "#FF00FF00", FontFamily: "Consolas", FontSize: 28, Bold: false,
            OutlineColor: "#FF000000", OutlineWidth: 3,
            ShadowColor: "#80000000", ShadowBlur: 6, ShadowDepth: 4);
        var folder = SkinWriter.Save(_root, "styled", _emptySrc, _fullSrc, new SkinConfig(styled, 1.0));
        var t = SkinLoader.Load(folder).Text;
        _out.WriteLine($"roundtrip: {t}");
        Assert.Equal(styled, t);

        // A plain positioned number writes just show/x/y — no styling keys.
        var plainFolder = SkinWriter.Save(_root, "plain", _emptySrc, _fullSrc,
            new SkinConfig(new SkinText(true, 12, 8), 1.0));
        var json = File.ReadAllText(Path.Combine(plainFolder, "skin.json"));
        _out.WriteLine("plain json: " + json);
        Assert.DoesNotContain("color", json);
        Assert.DoesNotContain("fontFamily", json);
        Assert.DoesNotContain("outlineColor", json);
        Assert.DoesNotContain("shadowColor", json);
        var back = SkinLoader.Load(plainFolder).Text;
        Assert.Equal(new SkinText(true, 12, 8), back); // defaults reconstruct the plain text
    }

    [Fact]
    public void Save_align_center_roundtrips_and_left_is_omitted()
    {
        var folder = SkinWriter.Save(_root, "aligned", _emptySrc, _fullSrc,
            new SkinConfig(new SkinText(true, 150, 5, Align: "center"), 1.0));
        var json = File.ReadAllText(Path.Combine(folder, "skin.json"));
        _out.WriteLine("center json: " + json);
        Assert.Contains("align", json);
        Assert.Equal("center", SkinLoader.Load(folder).Text!.Align);

        // Default left writes the historical plain {show,x,y} shape — no align key at all.
        var plainFolder = SkinWriter.Save(_root, "plain-align", _emptySrc, _fullSrc,
            new SkinConfig(new SkinText(true, 10, 5), 1.0));
        var plainJson = File.ReadAllText(Path.Combine(plainFolder, "skin.json"));
        _out.WriteLine("left json: " + plainJson);
        Assert.DoesNotContain("align", plainJson);
        Assert.Equal("left", SkinLoader.Load(plainFolder).Text!.Align);
    }

    [Fact]
    public void Save_muted_layer_roundtrips_and_clearing_removes_it()
    {
        var mutedSrc = Path.Combine(_dir, "src", "my-muted.png");
        TestPngs.Write(mutedSrc, 300, 100);

        var folder = SkinWriter.Save(_root, "with-muted", _emptySrc, _fullSrc,
            new SkinConfig(null, 1.0), mutedSrc);
        var info = SkinLoader.Load(folder);
        _out.WriteLine($"saved with muted: has={info.HasMuted} path={info.MutedPath}");
        Assert.True(info.HasMuted);
        Assert.True(File.Exists(Path.Combine(folder, "muted.png")));

        // Re-save without a muted source: the layer is cleared, stale file removed.
        SkinWriter.Save(_root, "with-muted", _emptySrc, _fullSrc, new SkinConfig(null, 1.0));
        var cleared = SkinLoader.Load(folder);
        _out.WriteLine($"after clear: has={cleared.HasMuted} muted.png={File.Exists(Path.Combine(folder, "muted.png"))}");
        Assert.False(cleared.HasMuted);
        Assert.False(File.Exists(Path.Combine(folder, "muted.png")));
    }

    [Fact]
    public void Save_muted_gif_lands_as_gif_and_removes_stale_png_variant()
    {
        var mutedPng = Path.Combine(_dir, "src", "muted-v1.png");
        TestPngs.Write(mutedPng, 300, 100);
        var folder = SkinWriter.Save(_root, "muted-fmt", _emptySrc, _fullSrc,
            new SkinConfig(null, 1.0), mutedPng);
        Assert.True(File.Exists(Path.Combine(folder, "muted.png")));

        var mutedGif = Path.Combine(_dir, "src", "muted-v2.gif");
        TestPngs.WriteGif(mutedGif, 300, 100);
        SkinWriter.Save(_root, "muted-fmt", _emptySrc, _fullSrc, new SkinConfig(null, 1.0), mutedGif);
        _out.WriteLine($"muted.gif={File.Exists(Path.Combine(folder, "muted.gif"))} muted.png={File.Exists(Path.Combine(folder, "muted.png"))}");
        Assert.True(File.Exists(Path.Combine(folder, "muted.gif")));
        Assert.False(File.Exists(Path.Combine(folder, "muted.png")));
        Assert.True(SkinLoader.Load(folder).MutedIsGif);
    }

    [Fact]
    public void Save_mutedDim_non_default_roundtrips_and_default_is_omitted()
    {
        var folder = SkinWriter.Save(_root, "dimmed", _emptySrc, _fullSrc,
            new SkinConfig(null, 1.0, MutedDim: 0.25));
        var json = File.ReadAllText(Path.Combine(folder, "skin.json"));
        _out.WriteLine("dim json: " + json);
        Assert.Contains("mutedDim", json);
        Assert.Equal(0.25, SkinLoader.Load(folder).MutedDim);

        // Default 0.6 with another non-default field: no mutedDim key written.
        var folder2 = SkinWriter.Save(_root, "undimmed", _emptySrc, _fullSrc,
            new SkinConfig(null, 1.5));
        var json2 = File.ReadAllText(Path.Combine(folder2, "skin.json"));
        _out.WriteLine("default-dim json: " + json2);
        Assert.DoesNotContain("mutedDim", json2);
        Assert.Equal(0.6, SkinLoader.Load(folder2).MutedDim);
    }

    [Fact]
    public void Save_mutedFrames_roundtrips_for_a_muted_sheet()
    {
        var mutedSheet = Path.Combine(_dir, "src", "muted-sheet.png");
        TestPngs.Write(mutedSheet, 300, 400); // 4 frames of 100
        var folder = SkinWriter.Save(_root, "muted-anim", _emptySrc, _fullSrc,
            new SkinConfig(null, 1.0, MutedFrames: 4), mutedSheet);

        var info = SkinLoader.Load(folder);
        _out.WriteLine($"roundtrip: valid={info.IsValid} mutedFrames={info.MutedFrames} err={info.Error}");
        Assert.True(info.IsValid);
        Assert.Equal(4, info.MutedFrames);
    }

    [Fact]
    public void Save_with_all_defaults_omits_skin_json_even_with_animation_defaults()
    {
        var folder = SkinWriter.Save(_root, "plain", _emptySrc, _fullSrc,
            new SkinConfig(null, 1.0, Fps: 10, EmptyFrames: 1, FullFrames: 1));
        _out.WriteLine("skin.json present: " + File.Exists(Path.Combine(folder, "skin.json")));
        Assert.False(File.Exists(Path.Combine(folder, "skin.json")));
    }

    [Fact]
    public void Save_name_is_trimmed()
    {
        var folder = SkinWriter.Save(_root, "  padded  ", _emptySrc, _fullSrc, new SkinConfig(null, 1.0));
        _out.WriteLine("folder: " + folder);
        Assert.Equal("padded", new DirectoryInfo(folder).Name);
    }
}
