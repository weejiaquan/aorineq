using System.IO.Compression;
using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

public class SkinArchiveTests : IDisposable
{
    private readonly string _dir;
    private readonly string _root;      // skins root for imports
    private readonly ITestOutputHelper _out;

    public SkinArchiveTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), "aorineq-tests-" + Guid.NewGuid().ToString("N"));
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
    public void Overwrite_import_that_fails_at_replace_time_preserves_the_existing_skin()
    {
        // Existing valid skin...
        var src = MakeSkinFolder("lock-src", withJson: true);
        var zip = Path.Combine(_dir, "lock.zip");
        SkinArchive.Export(src, zip);
        var existing = SkinArchive.Import(zip, _root, "target");

        // ...with an open handle inside it: the rename-aside step must fail, which has to throw
        // WITHOUT destroying the existing skin (the pre-fix code deleted it before installing).
        using (File.Open(Path.Combine(existing, "empty.png"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = Assert.Throws<InvalidOperationException>(() => SkinArchive.Import(zip, _root, "target"));
            _out.WriteLine("replace-time failure surfaced: " + ex.Message);
        }

        var info = SkinLoader.Load(existing);
        _out.WriteLine($"existing skin after failed replace: valid={info.IsValid}");
        Assert.True(info.IsValid); // byte-for-byte survivor
        Assert.True(File.Exists(Path.Combine(existing, "skin.json")));
        // No staging or backup debris.
        Assert.Empty(Directory.GetDirectories(_root).Where(d =>
        {
            var n = Path.GetFileName(d)!;
            return n.StartsWith(".import-") || n.StartsWith(".backup-");
        }));
    }

    [Fact]
    public void Export_then_import_carries_the_muted_png_layer()
    {
        var skin = MakeSkinFolder("muted-share", withJson: false);
        TestPngs.Write(Path.Combine(skin, "muted.png"), 300, 100);
        var zip = Path.Combine(_dir, "muted-share.zip");

        SkinArchive.Export(skin, zip);
        var imported = SkinArchive.Import(zip, _root, "muted-imported");
        var info = SkinLoader.Load(imported);
        _out.WriteLine($"imported: valid={info.IsValid} hasMuted={info.HasMuted} path={info.MutedPath}");
        Assert.True(info.IsValid);
        Assert.True(info.HasMuted);
        Assert.True(File.Exists(Path.Combine(imported, "muted.png")));
    }

    [Fact]
    public void Import_accepts_muted_gif_from_the_whitelist()
    {
        var skin = MakeSkinFolder("muted-gif-src", withJson: false);
        TestPngs.WriteGif(Path.Combine(skin, "muted.gif"), 300, 100);
        var zip = Path.Combine(_dir, "muted-gif.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(Path.Combine(skin, "empty.png"), "empty.png");
            archive.CreateEntryFromFile(Path.Combine(skin, "full.png"), "full.png");
            archive.CreateEntryFromFile(Path.Combine(skin, "muted.gif"), "muted.gif");
        }

        var imported = SkinArchive.Import(zip, _root, "muted-gif-imported");
        var info = SkinLoader.Load(imported);
        _out.WriteLine($"imported: valid={info.IsValid} hasMuted={info.HasMuted} gif={info.MutedIsGif}");
        Assert.True(info.IsValid);
        Assert.True(info.HasMuted);
        Assert.True(info.MutedIsGif);
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

    // ---------------- metadata + preview.png (v3.2.0) ----------------

    /// <summary>A skin whose layers are REAL decodable images, so exporting it can actually
    /// generate a preview.</summary>
    private string MakeRenderableSkinFolder(string name, string? json = null)
    {
        var folder = Path.Combine(_dir, name);
        Directory.CreateDirectory(folder);
        RealPngs.WriteSolid(Path.Combine(folder, "empty.png"), 300, 100, System.Drawing.Color.Red);
        RealPngs.WriteSolid(Path.Combine(folder, "full.png"), 300, 100, System.Drawing.Color.Lime);
        if (json is not null)
            File.WriteAllText(Path.Combine(folder, "skin.json"), json);
        return folder;
    }

    private static string[] EntryNames(string zip)
    {
        using var archive = ZipFile.OpenRead(zip);
        return archive.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public void Export_includes_a_generated_preview_png()
    {
        var skin = MakeRenderableSkinFolder("previewed");
        var zip = Path.Combine(_dir, "previewed.zip");

        SkinArchive.Export(skin, zip);

        var names = EntryNames(zip);
        _out.WriteLine("entries: " + string.Join(", ", names));
        Assert.Contains(SkinPreview.FileName, names);

        // The entry is the real composed image at the skin's logical size, not a placeholder.
        using var archive = ZipFile.OpenRead(zip);
        using var entryStream = archive.GetEntry(SkinPreview.FileName)!.Open();
        using var buffer = new MemoryStream();
        entryStream.CopyTo(buffer);
        buffer.Position = 0;
        using var image = System.Drawing.Image.FromStream(buffer);
        _out.WriteLine($"preview entry: {image.Width}x{image.Height}, {buffer.Length} bytes");
        Assert.Equal(300, image.Width);
        Assert.Equal(100, image.Height);
    }

    [Fact]
    public void Export_does_not_leave_a_preview_in_the_skin_folder()
    {
        // The preview belongs to the ZIP, not to the user's skins folder.
        var skin = MakeRenderableSkinFolder("clean-folder");
        SkinArchive.Export(skin, Path.Combine(_dir, "clean-folder.zip"));

        var files = Directory.GetFiles(skin).Select(Path.GetFileName).OrderBy(f => f).ToArray();
        _out.WriteLine("skin folder after export: " + string.Join(", ", files));
        Assert.Equal(new[] { "empty.png", "full.png" }, files);
    }

    [Fact]
    public void Export_still_succeeds_when_the_artwork_cannot_be_decoded()
    {
        // Header-only PNGs: a valid skin as far as the loader is concerned, undecodable in fact.
        // Losing the thumbnail is acceptable; refusing to share the skin is not.
        var skin = Path.Combine(_dir, "undecodable");
        Directory.CreateDirectory(skin);
        TestPngs.WriteHeaderOnly(Path.Combine(skin, "empty.png"), 300, 100);
        TestPngs.WriteHeaderOnly(Path.Combine(skin, "full.png"), 300, 100);
        var zip = Path.Combine(_dir, "undecodable.zip");

        SkinArchive.Export(skin, zip);

        var names = EntryNames(zip);
        _out.WriteLine("entries: " + string.Join(", ", names));
        Assert.Equal(new[] { "empty.png", "full.png" }, names);
        Assert.True(SkinLoader.Load(SkinArchive.Import(zip, _root, "undecodable-in")).IsValid);
    }

    [Fact]
    public void Import_never_writes_a_bundled_preview_to_disk()
    {
        // A shared zip is attacker-controlled. Its preview.png is a claim about the skin, not the
        // skin — the gallery renders it, this app must not keep it and must never let it decide
        // what the user thinks they installed.
        var skin = MakeRenderableSkinFolder("hostile-src");
        var zip = Path.Combine(_dir, "hostile.zip");
        var hostilePreview = Path.Combine(_dir, "hostile-preview.png");
        RealPngs.WriteSolid(hostilePreview, 4000, 4000, System.Drawing.Color.Magenta);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(Path.Combine(skin, "empty.png"), "empty.png");
            archive.CreateEntryFromFile(Path.Combine(skin, "full.png"), "full.png");
            archive.CreateEntryFromFile(hostilePreview, SkinPreview.FileName);
            archive.CreateEntryFromFile(hostilePreview, "nested/" + SkinPreview.FileName);
            archive.CreateEntryFromFile(hostilePreview, "PREVIEW.PNG");
        }

        var imported = SkinArchive.Import(zip, _root, "hostile-in");

        var files = Directory.GetFiles(imported).Select(Path.GetFileName).OrderBy(f => f).ToArray();
        _out.WriteLine("installed files: " + string.Join(", ", files));
        Assert.Equal(new[] { "empty.png", "full.png" }, files);
        Assert.False(File.Exists(Path.Combine(imported, SkinPreview.FileName)));
        Assert.True(SkinLoader.Load(imported).IsValid);
    }

    [Fact]
    public void A_re_export_regenerates_the_preview_from_the_installed_artwork()
    {
        // Round trip: whatever preview a zip claimed, the one WE ship is composed from the pixels
        // that actually got installed.
        var skin = MakeRenderableSkinFolder("regen-src");
        var firstZip = Path.Combine(_dir, "regen-1.zip");
        SkinArchive.Export(skin, firstZip);
        var imported = SkinArchive.Import(firstZip, _root, "regen-in");

        var secondZip = Path.Combine(_dir, "regen-2.zip");
        SkinArchive.Export(imported, secondZip);

        static byte[] PreviewBytes(string zip)
        {
            using var archive = ZipFile.OpenRead(zip);
            using var stream = archive.GetEntry(SkinPreview.FileName)!.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }

        var a = PreviewBytes(firstZip);
        var b = PreviewBytes(secondZip);
        _out.WriteLine($"preview bytes: first {a.Length}, re-export {b.Length}");
        Assert.Equal(a, b); // same artwork in, same thumbnail out
    }

    [Fact]
    public void A_failed_export_leaves_the_previous_zip_intact()
    {
        // Exporting over an existing zip must not destroy it before the replacement exists. The
        // pre-3.2 code deleted the destination first, so a failure at write time left the user
        // with neither the old archive nor a new one.
        var skin = MakeRenderableSkinFolder("replace-src");
        var zip = Path.Combine(_dir, "replace.zip");
        SkinArchive.Export(skin, zip);
        var original = File.ReadAllBytes(zip);
        _out.WriteLine($"existing zip: {original.Length} bytes");

        // Hold the destination open exclusively: the replace step must fail.
        using (File.Open(zip, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = Assert.Throws<InvalidOperationException>(() => SkinArchive.Export(skin, zip));
            _out.WriteLine("export failure surfaced: " + ex.Message);
        }

        var after = File.ReadAllBytes(zip);
        _out.WriteLine($"zip after the failed export: {after.Length} bytes");
        Assert.Equal(original, after);
        // ...and no scratch archive left lying beside it.
        var strays = Directory.GetFiles(_dir, "*.tmp").Concat(Directory.GetFiles(_dir, ".aorineq-*")).ToArray();
        _out.WriteLine("stray files: " + string.Join(", ", strays.Select(Path.GetFileName)));
        Assert.Empty(strays);
    }

    [Fact]
    public void Export_over_an_existing_zip_replaces_it_completely()
    {
        var first = MakeRenderableSkinFolder("v1-src");
        var zip = Path.Combine(_dir, "versioned.zip");
        TestPngs.WriteGif(Path.Combine(first, "muted.gif"), 300, 100);
        SkinArchive.Export(first, zip);
        Assert.Contains("muted.gif", EntryNames(zip));

        // A second export of a skin WITHOUT the muted layer must not leave the old entry behind.
        var second = MakeRenderableSkinFolder("v2-src");
        SkinArchive.Export(second, zip);

        var names = EntryNames(zip);
        _out.WriteLine("entries after replace: " + string.Join(", ", names));
        Assert.DoesNotContain("muted.gif", names);
        Assert.Equal(new[] { "empty.png", "full.png", SkinPreview.FileName }.OrderBy(n => n, StringComparer.Ordinal),
            names);
    }

    [Fact]
    public void Export_then_import_carries_the_authorship_metadata()
    {
        var meta = SkinMeta.Create("Neon Bar", "Ada Lovelace", "A glowing bar.", "1.2",
            new[] { "neon", "bar" }, "https://example.com/skins/neon");
        var src = Path.Combine(_dir, "meta-src");
        Directory.CreateDirectory(src);
        RealPngs.WriteSolid(Path.Combine(src, "empty.png"), 300, 100, System.Drawing.Color.Red);
        RealPngs.WriteSolid(Path.Combine(src, "full.png"), 300, 100, System.Drawing.Color.Lime);
        var authored = SkinWriter.Save(_dir, "meta-authored",
            Path.Combine(src, "empty.png"), Path.Combine(src, "full.png"),
            new SkinConfig(new SkinText(true, 10, 5), 1.5, Meta: meta));
        var zip = Path.Combine(_dir, "meta.zip");

        SkinArchive.Export(authored, zip);
        var imported = SkinArchive.Import(zip, _root, "meta-in");

        var info = SkinLoader.Load(imported);
        _out.WriteLine($"imported: valid={info.IsValid} meta={info.Meta} scale={info.Scale}");
        Assert.True(info.IsValid);
        Assert.Equal(meta, info.Meta);
        Assert.Equal(1.5, info.Scale);
        Assert.Equal(new SkinText(true, 10, 5), info.Text);
        Assert.Equal("Neon Bar — by Ada Lovelace", info.DisplayLabel);
    }

    [Fact]
    public void Import_of_a_zip_with_hostile_metadata_normalizes_it()
    {
        // skin.json travels verbatim inside the zip, so the loader is the only thing standing
        // between an attacker's credit line and the picker that displays it.
        var skin = MakeRenderableSkinFolder("hostile-meta-src", """
            {
              "author": "Ada\u202Egnp.exe",
              "sourceUrl": "javascript:alert(1)",
              "tags": ["ok", 42, "ok"]
            }
            """);
        var zip = Path.Combine(_dir, "hostile-meta.zip");
        SkinArchive.Export(skin, zip);

        var info = SkinLoader.Load(SkinArchive.Import(zip, _root, "hostile-meta-in"));
        _out.WriteLine($"imported meta: author='{info.Meta.Author}' url={info.Meta.SourceUrl ?? "<dropped>"} tags={string.Join("|", info.Meta.Tags)}");
        Assert.Equal("Adagnp.exe", info.Meta.Author);
        Assert.Null(info.Meta.SourceUrl);
        Assert.Equal(new[] { "ok" }, info.Meta.Tags);
    }
}
