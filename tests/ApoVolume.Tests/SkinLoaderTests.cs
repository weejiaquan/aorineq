using ApoVolume.Core;
using Xunit;
using Xunit.Abstractions;

namespace ApoVolume.Tests;

public class SkinLoaderTests : IDisposable
{
    private readonly string _dir;
    private readonly ITestOutputHelper _out;

    public SkinLoaderTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), "apo-volume-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // Writes a minimal but REAL PNG (signature + IHDR + zero-data IDAT + IEND) with the
    // given dimensions. Enough for header parsing; not a renderable image (tests that
    // need pixels use synthetic alpha arrays instead, never decode).
    private static void WritePng(string path, int width, int height)
    {
        using var fs = File.Create(path);
        void W(byte[] b) => fs.Write(b, 0, b.Length);
        void BE(int v) => W(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
        uint Crc(byte[] data)
        {
            uint[] table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            uint crc = 0xFFFFFFFF;
            foreach (var b in data) crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFF;
        }
        void Chunk(string type, byte[] data)
        {
            BE(data.Length);
            var typeAndData = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            W(typeAndData);
            BE((int)Crc(typeAndData));
        }
        W(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        ihdr[0] = (byte)(width >> 24); ihdr[1] = (byte)(width >> 16); ihdr[2] = (byte)(width >> 8); ihdr[3] = (byte)width;
        ihdr[4] = (byte)(height >> 24); ihdr[5] = (byte)(height >> 16); ihdr[6] = (byte)(height >> 8); ihdr[7] = (byte)height;
        ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
        Chunk("IHDR", ihdr);
        Chunk("IDAT", new byte[] { 0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01 });
        Chunk("IEND", Array.Empty<byte>());
    }

    private string NewSkinFolder(string name)
    {
        var folder = Path.Combine(_dir, name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public void Load_valid_skin_returns_populated_info()
    {
        var folder = NewSkinFolder("dark-pill");
        WritePng(Path.Combine(folder, "empty.png"), 300, 100);
        WritePng(Path.Combine(folder, "full.png"), 300, 100);

        var info = SkinLoader.Load(folder);
        _out.WriteLine($"loaded: Name={info.Name} Width={info.Width} Height={info.Height} Scale={info.Scale} Text={info.Text} Error={info.Error ?? "<none>"}");

        Assert.True(info.IsValid);
        Assert.Null(info.Error);
        Assert.Equal("dark-pill", info.Name);
        Assert.Equal(300, info.Width);
        Assert.Equal(100, info.Height);
        Assert.Equal(1.0, info.Scale);
        Assert.Null(info.Text);
        Assert.Equal(folder, info.Folder);
        Assert.Equal(Path.Combine(folder, "empty.png"), info.EmptyPath);
        Assert.Equal(Path.Combine(folder, "full.png"), info.FullPath);
    }

    [Fact]
    public void Load_missing_full_png_sets_error()
    {
        var folder = NewSkinFolder("no-full");
        WritePng(Path.Combine(folder, "empty.png"), 300, 100);

        var info = SkinLoader.Load(folder);
        _out.WriteLine($"missing full.png error: {info.Error}");

        Assert.False(info.IsValid);
        Assert.NotNull(info.Error);
        Assert.Contains("full.png", info.Error);
    }

    [Fact]
    public void Load_missing_empty_png_sets_error()
    {
        var folder = NewSkinFolder("no-empty");
        WritePng(Path.Combine(folder, "full.png"), 300, 100);

        var info = SkinLoader.Load(folder);
        _out.WriteLine($"missing empty.png error: {info.Error}");

        Assert.False(info.IsValid);
        Assert.NotNull(info.Error);
        Assert.Contains("empty.png", info.Error);
    }

    [Fact]
    public void Load_dimension_mismatch_names_both_sizes()
    {
        var folder = NewSkinFolder("mismatch");
        WritePng(Path.Combine(folder, "empty.png"), 300, 120);
        WritePng(Path.Combine(folder, "full.png"), 300, 100);

        var info = SkinLoader.Load(folder);
        _out.WriteLine($"dimension mismatch error: {info.Error}");

        Assert.False(info.IsValid);
        Assert.NotNull(info.Error);
        // must name both sizes
        Assert.Contains("300", info.Error);
        Assert.Contains("100", info.Error);
        Assert.Contains("120", info.Error);
    }

    [Fact]
    public void Load_corrupt_skin_json_sets_error()
    {
        var folder = NewSkinFolder("corrupt-json");
        WritePng(Path.Combine(folder, "empty.png"), 300, 100);
        WritePng(Path.Combine(folder, "full.png"), 300, 100);
        File.WriteAllText(Path.Combine(folder, "skin.json"), "{ not valid json !!!");

        var info = SkinLoader.Load(folder);
        _out.WriteLine($"corrupt skin.json error: {info.Error}");

        Assert.False(info.IsValid);
        Assert.NotNull(info.Error);
    }

    [Theory]
    [InlineData(0.1, 0.25)]   // below min, clamps up
    [InlineData(0.25, 0.25)]  // exact min
    [InlineData(2.0, 2.0)]    // within range
    [InlineData(4.0, 4.0)]    // exact max
    [InlineData(10.0, 4.0)]   // above max, clamps down
    public void Load_scale_is_clamped_0_25_to_4(double rawScale, double expectedScale)
    {
        var folder = NewSkinFolder("scale-" + rawScale.ToString(System.Globalization.CultureInfo.InvariantCulture).Replace('.', '_'));
        WritePng(Path.Combine(folder, "empty.png"), 300, 100);
        WritePng(Path.Combine(folder, "full.png"), 300, 100);
        File.WriteAllText(Path.Combine(folder, "skin.json"),
            $"{{ \"scale\": {rawScale.ToString(System.Globalization.CultureInfo.InvariantCulture)} }}");

        var info = SkinLoader.Load(folder);
        _out.WriteLine($"rawScale={rawScale} -> Scale={info.Scale} (expected {expectedScale}), Error={info.Error ?? "<none>"}");

        Assert.True(info.IsValid);
        Assert.Equal(expectedScale, info.Scale);
    }

    [Fact]
    public void Load_missing_skin_json_defaults_scale_to_1_and_null_text()
    {
        var folder = NewSkinFolder("no-json");
        WritePng(Path.Combine(folder, "empty.png"), 300, 100);
        WritePng(Path.Combine(folder, "full.png"), 300, 100);

        var info = SkinLoader.Load(folder);
        _out.WriteLine($"no skin.json: Scale={info.Scale} Text={info.Text}");

        Assert.True(info.IsValid);
        Assert.Equal(1.0, info.Scale);
        Assert.Null(info.Text);
    }

    [Fact]
    public void Load_parses_percentText_case_insensitively()
    {
        var folder = NewSkinFolder("percent-text");
        WritePng(Path.Combine(folder, "empty.png"), 300, 100);
        WritePng(Path.Combine(folder, "full.png"), 300, 100);
        // Mixed-case keys to exercise case-insensitive parsing.
        File.WriteAllText(Path.Combine(folder, "skin.json"),
            "{ \"PercentText\": { \"Show\": true, \"X\": 10, \"Y\": 20 }, \"Scale\": 1.5 }");

        var info = SkinLoader.Load(folder);
        _out.WriteLine($"parsed text: {info.Text}, scale: {info.Scale}");

        Assert.True(info.IsValid);
        Assert.NotNull(info.Text);
        Assert.True(info.Text!.Show);
        Assert.Equal(10, info.Text.X);
        Assert.Equal(20, info.Text.Y);
        Assert.Equal(1.5, info.Scale);
    }

    [Fact]
    public void Load_gif_layers_resolve_when_png_absent()
    {
        var folder = NewSkinFolder("gif-skin");
        TestPngs.WriteGif(Path.Combine(folder, "empty.gif"), 300, 100);
        TestPngs.WriteGif(Path.Combine(folder, "full.gif"), 300, 100);

        var info = SkinLoader.Load(folder);
        _out.WriteLine($"gif skin: valid={info.IsValid} {info.Width}x{info.Height} emptyGif={info.EmptyIsGif} fullGif={info.FullIsGif} err={info.Error}");

        Assert.True(info.IsValid);
        Assert.Equal(300, info.Width);
        Assert.Equal(100, info.Height);
        Assert.True(info.EmptyIsGif);
        Assert.True(info.FullIsGif);
        Assert.EndsWith("empty.gif", info.EmptyPath);
        Assert.EndsWith("full.gif", info.FullPath);
    }

    [Fact]
    public void Load_png_takes_precedence_over_gif()
    {
        var folder = NewSkinFolder("both-formats");
        WritePng(Path.Combine(folder, "empty.png"), 300, 100);
        TestPngs.WriteGif(Path.Combine(folder, "empty.gif"), 999, 999); // must be ignored
        WritePng(Path.Combine(folder, "full.png"), 300, 100);

        var info = SkinLoader.Load(folder);
        _out.WriteLine($"precedence: {info.EmptyPath} valid={info.IsValid}");

        Assert.True(info.IsValid);
        Assert.EndsWith("empty.png", info.EmptyPath);
        Assert.False(info.EmptyIsGif);
    }

    [Fact]
    public void Load_sprite_sheet_uses_logical_frame_size()
    {
        var folder = NewSkinFolder("sheet");
        WritePng(Path.Combine(folder, "empty.png"), 300, 100);      // static, 1 frame
        WritePng(Path.Combine(folder, "full.png"), 300, 800);       // 8 frames of 100
        File.WriteAllText(Path.Combine(folder, "skin.json"), "{ \"fullFrames\": 8, \"fps\": 12 }");

        var info = SkinLoader.Load(folder);
        _out.WriteLine($"sheet: valid={info.IsValid} logical={info.Width}x{info.Height} fullFrames={info.FullFrames} fps={info.Fps} err={info.Error}");

        Assert.True(info.IsValid);
        Assert.Equal(300, info.Width);
        Assert.Equal(100, info.Height);   // logical, not the 800px sheet height
        Assert.Equal(8, info.FullFrames);
        Assert.Equal(1, info.EmptyFrames);
        Assert.Equal(12.0, info.Fps);
    }

    [Fact]
    public void Load_sheet_height_not_divisible_by_frames_sets_error()
    {
        var folder = NewSkinFolder("bad-sheet");
        WritePng(Path.Combine(folder, "empty.png"), 300, 100);
        WritePng(Path.Combine(folder, "full.png"), 300, 790); // not divisible by 8
        File.WriteAllText(Path.Combine(folder, "skin.json"), "{ \"fullFrames\": 8 }");

        var info = SkinLoader.Load(folder);
        _out.WriteLine($"bad sheet error: {info.Error}");

        Assert.False(info.IsValid);
        Assert.Contains("divisible", info.Error);
    }

    [Fact]
    public void Load_logical_size_mismatch_between_static_and_gif_sets_error()
    {
        var folder = NewSkinFolder("logical-mismatch");
        WritePng(Path.Combine(folder, "empty.png"), 300, 100);
        TestPngs.WriteGif(Path.Combine(folder, "full.gif"), 300, 120);

        var info = SkinLoader.Load(folder);
        _out.WriteLine($"logical mismatch error: {info.Error}");

        Assert.False(info.IsValid);
        Assert.Contains("120", info.Error);
        Assert.Contains("100", info.Error);
    }

    [Theory]
    [InlineData(0.5, 1.0)]    // below min, clamps up
    [InlineData(24.0, 24.0)]  // within range
    [InlineData(240.0, 60.0)] // above max, clamps down
    public void Load_fps_is_clamped_1_to_60(double rawFps, double expected)
    {
        var folder = NewSkinFolder("fps-" + rawFps.ToString(System.Globalization.CultureInfo.InvariantCulture).Replace('.', '_'));
        WritePng(Path.Combine(folder, "empty.png"), 300, 100);
        WritePng(Path.Combine(folder, "full.png"), 300, 100);
        File.WriteAllText(Path.Combine(folder, "skin.json"),
            $"{{ \"fps\": {rawFps.ToString(System.Globalization.CultureInfo.InvariantCulture)} }}");

        var info = SkinLoader.Load(folder);
        _out.WriteLine($"rawFps={rawFps} -> Fps={info.Fps} (expected {expected})");

        Assert.True(info.IsValid);
        Assert.Equal(expected, info.Fps);
    }

    [Fact]
    public void Load_defaults_fps_10_and_single_frames()
    {
        var folder = NewSkinFolder("defaults");
        WritePng(Path.Combine(folder, "empty.png"), 300, 100);
        WritePng(Path.Combine(folder, "full.png"), 300, 100);

        var info = SkinLoader.Load(folder);
        _out.WriteLine($"defaults: fps={info.Fps} ef={info.EmptyFrames} ff={info.FullFrames}");

        Assert.Equal(10.0, info.Fps);
        Assert.Equal(1, info.EmptyFrames);
        Assert.Equal(1, info.FullFrames);
    }

    [Fact]
    public void Load_missing_folder_sets_error_and_never_throws()
    {
        var folder = Path.Combine(_dir, "does-not-exist");

        var ex = Record.Exception(() => SkinLoader.Load(folder));
        var info = SkinLoader.Load(folder);
        _out.WriteLine($"missing folder: exception={(ex?.ToString() ?? "<none>")}, error={info.Error}");

        Assert.Null(ex);
        Assert.False(info.IsValid);
        Assert.NotNull(info.Error);
    }

    [Fact]
    public void Scan_skips_nothing_but_lists_invalid_skins_with_their_errors()
    {
        var root = NewSkinFolder("skins-root");
        var good = Path.Combine(root, "good");
        Directory.CreateDirectory(good);
        WritePng(Path.Combine(good, "empty.png"), 300, 100);
        WritePng(Path.Combine(good, "full.png"), 300, 100);

        var bad = Path.Combine(root, "bad");
        Directory.CreateDirectory(bad);
        WritePng(Path.Combine(bad, "empty.png"), 300, 100);
        // full.png intentionally missing

        var results = SkinLoader.Scan(root);
        _out.WriteLine("scan results:");
        foreach (var r in results)
            _out.WriteLine($"  {r.Name}: valid={r.IsValid} error={r.Error ?? "<none>"}");

        Assert.Equal(2, results.Count);
        var goodInfo = Assert.Single(results, r => r.Name == "good");
        var badInfo = Assert.Single(results, r => r.Name == "bad");
        Assert.True(goodInfo.IsValid);
        Assert.False(badInfo.IsValid);
        Assert.NotNull(badInfo.Error);
    }

    [Fact]
    public void Scan_returns_empty_list_when_root_missing()
    {
        var root = Path.Combine(_dir, "no-such-root");

        var results = SkinLoader.Scan(root);
        _out.WriteLine($"scan of missing root '{root}' returned {results.Count} results");

        Assert.Empty(results);
    }
}
