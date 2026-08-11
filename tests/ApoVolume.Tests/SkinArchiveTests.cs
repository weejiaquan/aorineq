using System.IO.Compression;
using ApoVolume.Core;
using Xunit;
using Xunit.Abstractions;

namespace ApoVolume.Tests;

public class SkinArchiveTests : IDisposable
{
    private readonly string _dir;
    private readonly string _root;      // skins root for imports
    private readonly ITestOutputHelper _out;

    public SkinArchiveTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), "apo-volume-tests-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_dir, "skins");
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string MakeSkinFolder(string name, bool withJson = true, bool gifFull = false)
    {
        var folder = Path.Combine(_dir, name);
        Directory.CreateDirectory(folder);
        TestPngs.Write(Path.Combine(folder, "empty.png"), 300, 100);
        if (gifFull)
            TestPngs.WriteGif(Path.Combine(folder, "full.gif"), 300, 100);
        else
            TestPngs.Write(Path.Combine(folder, "full.png"), 300, 100);
        if (withJson)
            File.WriteAllText(Path.Combine(folder, "skin.json"),
                "{ \"percentText\": { \"show\": true, \"x\": 5, \"y\": 6 }, \"scale\": 1.5 }");
        return folder;
    }

    [Fact]
    public void Export_then_import_roundtrips_including_gif_and_json()
    {
        var skin = MakeSkinFolder("shareme", withJson: true, gifFull: true);
        var zip = Path.Combine(_dir, "shareme.zip");

        SkinArchive.Export(skin, zip);
        Assert.True(File.Exists(zip));

        var imported = SkinArchive.Import(zip, _root, "imported");
        var info = SkinLoader.Load(imported);
        _out.WriteLine($"imported: valid={info.IsValid} fullGif={info.FullIsGif} scale={info.Scale} text={info.Text} err={info.Error}");

        Assert.True(info.IsValid);
        Assert.True(info.FullIsGif);
        Assert.Equal(1.5, info.Scale);
        Assert.Equal(new SkinText(true, 5, 6), info.Text);
    }

    [Fact]
    public void Import_accepts_files_nested_one_folder_deep()
    {
        // People zip the FOLDER rather than its contents: entries look like "my-skin/empty.png".
        var skin = MakeSkinFolder("nested-src", withJson: false);
        var zip = Path.Combine(_dir, "nested.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(Path.Combine(skin, "empty.png"), "my-skin/empty.png");
            archive.CreateEntryFromFile(Path.Combine(skin, "full.png"), "my-skin/full.png");
        }

        var imported = SkinArchive.Import(zip, _root, "nested");
        _out.WriteLine("imported from nested layout: " + imported);
        Assert.True(SkinLoader.Load(imported).IsValid);
    }

    [Fact]
    public void Import_ignores_traversal_entries_and_nothing_escapes_the_root()
    {
        var skin = MakeSkinFolder("evil-src", withJson: false);
        var zip = Path.Combine(_dir, "evil.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(Path.Combine(skin, "empty.png"), "empty.png");
            archive.CreateEntryFromFile(Path.Combine(skin, "full.png"), "full.png");
            // Hostile entries: traversal name, deep nesting, unknown file.
            archive.CreateEntryFromFile(Path.Combine(skin, "empty.png"), "../../evil.png");
            archive.CreateEntryFromFile(Path.Combine(skin, "empty.png"), "a/b/c/empty.png");
            archive.CreateEntryFromFile(Path.Combine(skin, "empty.png"), "malware.exe");
        }

        var imported = SkinArchive.Import(zip, _root, "evil-test");
        _out.WriteLine("imported: " + imported);

        Assert.True(SkinLoader.Load(imported).IsValid);
        // Nothing outside the skins root, nothing but whitelisted names inside the skin folder.
        Assert.False(File.Exists(Path.Combine(_dir, "evil.png")));
        Assert.False(File.Exists(Path.Combine(_root, "evil.png")));
        var files = Directory.GetFiles(imported).Select(Path.GetFileName).ToArray();
        _out.WriteLine("files in imported folder: " + string.Join(", ", files));
        Assert.DoesNotContain("malware.exe", files);
        Assert.Equal(2, files.Length);
    }

    [Fact]
    public void Import_of_incomplete_zip_throws_and_cleans_up()
    {
        var skin = MakeSkinFolder("incomplete-src", withJson: false);
        var zip = Path.Combine(_dir, "incomplete.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(Path.Combine(skin, "empty.png"), "empty.png");
            // no full layer at all
        }

        var ex = Assert.Throws<InvalidOperationException>(() => SkinArchive.Import(zip, _root, "broken"));
        _out.WriteLine("rejected: " + ex.Message);
        Assert.Contains("full", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(_root, "broken"))); // half-import removed
    }

    [Fact]
    public void Failed_overwrite_import_leaves_existing_skin_untouched()
    {
        // An existing, working skin...
        var existingZipSrc = MakeSkinFolder("existing-src", withJson: true);
        var goodZip = Path.Combine(_dir, "good.zip");
        SkinArchive.Export(existingZipSrc, goodZip);
        var existing = SkinArchive.Import(goodZip, _root, "target");
        Assert.True(SkinLoader.Load(existing).IsValid);

        // ...must survive a failed overwrite-import of a broken zip byte-for-byte.
        var badZip = Path.Combine(_dir, "bad.zip");
        using (var archive = ZipFile.Open(badZip, ZipArchiveMode.Create))
            archive.CreateEntryFromFile(Path.Combine(existingZipSrc, "empty.png"), "empty.png"); // no full layer

        var ex = Assert.Throws<InvalidOperationException>(() => SkinArchive.Import(badZip, _root, "target"));
        _out.WriteLine("failed import: " + ex.Message);

        var info = SkinLoader.Load(existing);
        Assert.True(info.IsValid); // still a complete, valid skin
        Assert.True(File.Exists(Path.Combine(existing, "skin.json")));
        // No staging debris left behind either.
        Assert.Empty(Directory.GetDirectories(_root).Where(d => Path.GetFileName(d)!.StartsWith(".import-")));
    }

    [Fact]
    public void Overwrite_import_fully_replaces_no_stale_files_survive()
    {
        // Existing PNG-based skin with a skin.json...
        var pngSrc = MakeSkinFolder("png-src", withJson: true);
        var pngZip = Path.Combine(_dir, "png.zip");
        SkinArchive.Export(pngSrc, pngZip);
        SkinArchive.Import(pngZip, _root, "target");

        // ...overwritten by a GIF-based skin WITHOUT json: the old full.png and skin.json must be
        // gone, or the loader's png-over-gif precedence would keep rendering the old artwork.
        var gifSrc = MakeSkinFolder("gif-src", withJson: false, gifFull: true);
        var gifZip = Path.Combine(_dir, "gif.zip");
        SkinArchive.Export(gifSrc, gifZip);
        var folder = SkinArchive.Import(gifZip, _root, "target");

        var files = Directory.GetFiles(folder).Select(Path.GetFileName).OrderBy(f => f).ToArray();
        _out.WriteLine("files after overwrite: " + string.Join(", ", files));
        Assert.Equal(new[] { "empty.png", "full.gif" }, files);
        var info = SkinLoader.Load(folder);
        Assert.True(info.IsValid);
        Assert.True(info.FullIsGif);
        Assert.Equal(1.0, info.Scale); // old skin.json's 1.5 must not survive
    }

    [Fact]
    public void Import_with_invalid_name_throws()
    {
        var skin = MakeSkinFolder("name-src", withJson: false);
        var zip = Path.Combine(_dir, "name.zip");
        SkinArchive.Export(skin, zip);
        Assert.Throws<ArgumentException>(() => SkinArchive.Import(zip, _root, "bad/name"));
    }

    [Fact]
    public void Export_of_invalid_skin_throws()
    {
        var folder = Path.Combine(_dir, "invalid-skin");
        Directory.CreateDirectory(folder);
        TestPngs.Write(Path.Combine(folder, "empty.png"), 300, 100); // no full layer

        var ex = Assert.Throws<InvalidOperationException>(
            () => SkinArchive.Export(folder, Path.Combine(_dir, "nope.zip")));
        _out.WriteLine("rejected: " + ex.Message);
    }

    [Fact]
    public void DefaultName_is_zip_stem()
    {
        Assert.Equal("cool-skin", SkinArchive.DefaultName(@"C:\Downloads\cool-skin.zip"));
    }
}
