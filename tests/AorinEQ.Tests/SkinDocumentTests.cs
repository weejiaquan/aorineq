using System.Drawing;
using AorinEQ.Core;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>The designer's document is only honest if it describes the skin the loader would
/// load: every shape of skin here is loaded by the real SkinLoader and compared field for field
/// with what the document derives on its own.</summary>
public class SkinDocumentTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aorineq-document-" + Guid.NewGuid().ToString("N"));
    private readonly ImageProbe _probe = new();

    public SkinDocumentTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Folder(string name)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private void AssertDerivesWhatTheLoaderLoads(string folder)
    {
        var loaded = SkinLoader.Load(folder);
        Assert.True(loaded.IsValid, loaded.Error);
        var derived = SkinDocument.FromInfo(loaded).ToInfo(_probe);
        _out.WriteLine($"loader : {loaded}");
        _out.WriteLine($"derived: {derived}");
        Assert.Equal(loaded, derived);
    }

    [Fact]
    public void Every_shipped_skin_round_trips_through_the_document()
    {
        DefaultSkins.EnsureInstalled(_root);
        foreach (var style in SkinArt.All)
            AssertDerivesWhatTheLoaderLoads(Path.Combine(_root, style));
    }

    [Fact]
    public void Gif_layers_round_trip_through_the_document()
    {
        var folder = Folder("gifs");
        RealPngs.WriteGif(Path.Combine(folder, "empty.gif"), 200, 40, Color.Red);
        RealPngs.WriteGif(Path.Combine(folder, "full.gif"), 200, 40, Color.Lime);
        File.WriteAllText(Path.Combine(folder, "skin.json"), """{"emptyFrames":3,"fps":20}""");
        AssertDerivesWhatTheLoaderLoads(folder);
    }

    [Fact]
    public void Sprite_sheets_and_a_muted_sheet_round_trip_through_the_document()
    {
        var folder = Folder("sheets");
        TestPngs.Write(Path.Combine(folder, "empty.png"), 300, 80);
        TestPngs.Write(Path.Combine(folder, "full.png"), 300, 80);
        TestPngs.Write(Path.Combine(folder, "muted.png"), 300, 40);
        File.WriteAllText(Path.Combine(folder, "skin.json"),
            """{"emptyFrames":4,"fullFrames":4,"mutedFrames":2,"fps":12,"fillStartX":20,"fillEndX":280,"mutedDim":0.3}""");
        AssertDerivesWhatTheLoaderLoads(folder);
    }

    [Fact]
    public void Notice_art_and_a_notice_turned_off_round_trip_through_the_document()
    {
        var art = Folder("notice-art");
        TestPngs.Write(Path.Combine(art, "empty.png"), 300, 40);
        TestPngs.Write(Path.Combine(art, "full.png"), 300, 40);
        TestPngs.Write(Path.Combine(art, "notice.png"), 280, 96);
        File.WriteAllText(Path.Combine(art, "skin.json"),
            """{"notice":{"frames":2,"text":{"x":60,"y":12,"fontSize":15,"align":"center"},"textWidth":180,"glyph":{"x":20,"y":10,"size":20}}}""");
        AssertDerivesWhatTheLoaderLoads(art);

        var off = Folder("notice-off");
        TestPngs.Write(Path.Combine(off, "empty.png"), 300, 40);
        TestPngs.Write(Path.Combine(off, "full.png"), 300, 40);
        File.WriteAllText(Path.Combine(off, "skin.json"), """{"notice":false}""");
        AssertDerivesWhatTheLoaderLoads(off);
        Assert.Null(SkinDocument.FromInfo(SkinLoader.Load(off)).Config.Notice);

        // A plate keeps the text position and glyph X the loader reads for it (the writer writes
        // them back since Task 3), so the document must not zero them.
        var plate = Folder("notice-plate");
        TestPngs.Write(Path.Combine(plate, "empty.png"), 300, 40);
        TestPngs.Write(Path.Combine(plate, "full.png"), 300, 40);
        File.WriteAllText(Path.Combine(plate, "skin.json"), """{"notice":{"text":{"x":60,"y":4},"glyph":{"x":20}}}""");
        AssertDerivesWhatTheLoaderLoads(plate);
    }

    /// <summary>A strip or notice whose art is on disk but cannot be loaded is not "no strip": the
    /// document keeps the loader's reason, derives the same SkinInfo the loader returned, and
    /// names it as an issue - which blocks Save, because saving would delete that art.</summary>
    [Fact]
    public void Art_the_loader_could_not_load_is_kept_as_a_blocking_issue()
    {
        var folder = Folder("unloadable");
        TestPngs.Write(Path.Combine(folder, "empty.png"), 300, 40);
        TestPngs.Write(Path.Combine(folder, "full.png"), 300, 40);
        TestPngs.Write(Path.Combine(folder, "airplay-empty.png"), 300, 30);
        TestPngs.Write(Path.Combine(folder, "airplay-full.png"), 280, 30);
        TestPngs.Write(Path.Combine(folder, "notice.png"), 300, 50);
        File.WriteAllText(Path.Combine(folder, "skin.json"), """{"notice":{"frames":3}}""");
        var loaded = SkinLoader.Load(folder);
        _out.WriteLine($"loader: airPlayError={loaded.AirPlayError ?? "(none)"} noticeError={loaded.NoticeError ?? "(none)"}");
        Assert.NotNull(loaded.AirPlayError);
        Assert.NotNull(loaded.NoticeError);

        AssertDerivesWhatTheLoaderLoads(folder);
        var doc = SkinDocument.FromInfo(loaded);
        var issues = doc.Issues(_probe);
        _out.WriteLine($"document: airPlayError={doc.AirPlayError ?? "(none)"} noticeError={doc.NoticeError ?? "(none)"}");
        _out.WriteLine(string.Join(Environment.NewLine, issues));
        Assert.Equal((loaded.AirPlayError, loaded.NoticeError), (doc.AirPlayError, doc.NoticeError));
        Assert.Null(doc.Config.AirPlay);
        Assert.Null(doc.Config.Notice);
        Assert.Contains(new SkinIssue(SkinLayer.AirPlay, SkinIssueKind.AirPlayUnloadable, [loaded.AirPlayError!]), issues);
        Assert.Contains(new SkinIssue(SkinLayer.Notice, SkinIssueKind.NoticeUnloadable, [loaded.NoticeError!]), issues);
    }

    /// <summary>The document's reasons for an unusable skin or strip are the loader's, word for
    /// word, because both go through the same checks.</summary>
    [Fact]
    public void The_document_gives_the_loaders_own_reasons()
    {
        var mismatch = Folder("mismatch");
        TestPngs.Write(Path.Combine(mismatch, "empty.png"), 300, 40);
        TestPngs.Write(Path.Combine(mismatch, "full.png"), 200, 40);
        var inverted = Folder("inverted");
        TestPngs.Write(Path.Combine(inverted, "empty.png"), 300, 40);
        TestPngs.Write(Path.Combine(inverted, "full.png"), 300, 40);
        File.WriteAllText(Path.Combine(inverted, "skin.json"), """{"fillStartX":200,"fillEndX":100}""");
        var strip = Folder("strip-inverted");
        TestPngs.Write(Path.Combine(strip, "empty.png"), 300, 40);
        TestPngs.Write(Path.Combine(strip, "full.png"), 300, 40);
        TestPngs.Write(Path.Combine(strip, "airplay-empty.png"), 300, 30);
        TestPngs.Write(Path.Combine(strip, "airplay-full.png"), 300, 30);
        File.WriteAllText(Path.Combine(strip, "skin.json"), """{"airplay":{"fillStartX":250,"fillEndX":20}}""");
        var config = new SkinConfig(null, 1.0, Meta: SkinMeta.None);
        SkinDocument Doc(string folder, SkinConfig c) =>
            new(Path.GetFileName(folder), Path.Combine(folder, "empty.png"), Path.Combine(folder, "full.png"), null, c);

        var loaderMismatch = SkinLoader.Load(mismatch).Error;
        var loaderInverted = SkinLoader.Load(inverted).Error;
        var loaderStrip = SkinLoader.Load(strip).AirPlayError;
        var docMismatch = Doc(mismatch, config).ToInfo(_probe).Error;
        var docInverted = Doc(inverted, config with { FillStartX = 200, FillEndX = 100 }).ToInfo(_probe).Error;
        var docStrip = Doc(strip, config with
        {
            AirPlay = new SkinAirPlay(Path.Combine(strip, "airplay-empty.png"), Path.Combine(strip, "airplay-full.png"),
                300, 30, false, false, 1, 1, 250, 20, null, null, new SkinHitRect(0, 0, 300, 30)),
        }).ToInfo(_probe).AirPlayError;

        _out.WriteLine($"size : loader '{loaderMismatch}' / document '{docMismatch}'");
        _out.WriteLine($"fill : loader '{loaderInverted}' / document '{docInverted}'");
        _out.WriteLine($"strip: loader '{loaderStrip}' / document '{docStrip}'");
        Assert.Equal("full.png frame is 200×40 but empty.png frame is 300×40", loaderMismatch);
        Assert.Equal(loaderMismatch, docMismatch);
        Assert.Equal("fillStartX (200) must be less than fillEndX (100)", loaderInverted);
        Assert.Equal(loaderInverted, docInverted);
        Assert.Equal("airplay fillStartX (250) must be less than fillEndX (20)", loaderStrip);
        Assert.Equal(loaderStrip, docStrip);
    }

    [Fact]
    public void A_new_document_is_named_and_has_no_art_yet()
    {
        var doc = SkinDocument.New("Untitled skin");
        var issues = doc.Issues(_probe);
        _out.WriteLine($"{doc}");
        _out.WriteLine(string.Join(Environment.NewLine, issues));
        Assert.Equal("Untitled skin", doc.Name);
        Assert.Equal("", doc.EmptyPath);
        Assert.Equal(SkinMeta.None, doc.Config.Meta);
        Assert.Contains(new SkinIssue(SkinLayer.VolumeEmpty, SkinIssueKind.MissingArt, []), issues);
        Assert.Contains(new SkinIssue(SkinLayer.VolumeFull, SkinIssueKind.MissingArt, []), issues);
        Assert.False(doc.ToInfo(_probe).IsValid);
    }

    /// <summary>Review focus: missing or odd art sizes - a 7 px sheet declared as 2 frames, a 1x1
    /// muted layer, a mismatched strip, a corrupt notice image, art deleted while open.</summary>
    [Fact]
    public void Issues_name_every_problem_with_its_layer()
    {
        var folder = Folder("broken");
        var empty = Path.Combine(folder, "e.png");
        var full = Path.Combine(folder, "f.png");
        var muted = Path.Combine(folder, "m.png");
        var corrupt = Path.Combine(folder, "corrupt.png");
        var stripEmpty = Path.Combine(folder, "ae.png");
        var stripFull = Path.Combine(folder, "af.png");
        TestPngs.Write(empty, 300, 7);
        TestPngs.Write(full, 200, 40);
        TestPngs.Write(muted, 1, 1);
        File.WriteAllBytes(corrupt, new byte[30]);
        TestPngs.Write(stripEmpty, 300, 30);
        TestPngs.Write(stripFull, 280, 30);

        var doc = new SkinDocument("CON", empty, full, muted,
            new SkinConfig(null, 1.0, EmptyFrames: 2, Meta: SkinMeta.None,
                Notice: SkinLoader.DefaultNotice with { ArtPath = corrupt },
                AirPlay: new SkinAirPlay(stripEmpty, stripFull, 300, 30, false, false, 1, 1, 0, 300,
                    null, null, new SkinHitRect(0, 0, 300, 30))));
        var issues = doc.Issues(_probe);
        _out.WriteLine(string.Join(Environment.NewLine, issues));

        Assert.Contains(new SkinIssue(SkinLayer.SkinInfo, SkinIssueKind.InvalidName, [SkinWriter.ValidateName("CON")!]), issues);
        Assert.Contains(new SkinIssue(SkinLayer.VolumeEmpty, SkinIssueKind.FramesDoNotDivide, ["e.png", "7", "2"]), issues);
        Assert.Contains(new SkinIssue(SkinLayer.AirPlayFull, SkinIssueKind.SizeMismatch, ["af.png", "280", "30", "300", "30"]), issues);
        Assert.Contains(new SkinIssue(SkinLayer.NoticeArt, SkinIssueKind.ArtUnreadable, ["corrupt.png"]), issues);
        Assert.DoesNotContain(issues, i => i.Kind == SkinIssueKind.FillRangeInverted);
        // The empty layer is unusable, so nothing is measured against it: one broken file, one issue.
        Assert.DoesNotContain(issues, i => i.Layer is SkinLayer.VolumeFull or SkinLayer.VolumeMuted);

        var sheetFixed = doc with { Config = doc.Config with { EmptyFrames = 1 } };
        var second = sheetFixed.Issues(_probe);
        _out.WriteLine("-- with the sheet fixed --");
        _out.WriteLine(string.Join(Environment.NewLine, second));
        Assert.Contains(new SkinIssue(SkinLayer.VolumeFull, SkinIssueKind.SizeMismatch, ["f.png", "200", "40", "300", "7"]), second);
        Assert.Contains(new SkinIssue(SkinLayer.VolumeMuted, SkinIssueKind.SizeMismatch, ["m.png", "1", "1", "300", "7"]), second);

        var inverted = sheetFixed with
        {
            Config = sheetFixed.Config with
            {
                FillStartX = 200, FillEndX = 100,
                AirPlay = sheetFixed.Config.AirPlay! with { FullPath = stripEmpty, FillStartX = 200, FillEndX = 100 },
            },
        };
        var third = inverted.Issues(_probe);
        _out.WriteLine("-- with both fill ranges inverted --");
        _out.WriteLine(string.Join(Environment.NewLine, third));
        Assert.Contains(new SkinIssue(SkinLayer.VolumeBar, SkinIssueKind.FillRangeInverted, ["200", "100"]), third);
        Assert.Contains(new SkinIssue(SkinLayer.AirPlay, SkinIssueKind.FillRangeInverted, ["200", "100"]), third);

        File.Delete(stripFull);
        var deleted = sheetFixed.Issues(_probe);
        _out.WriteLine("-- with af.png deleted from disk --");
        _out.WriteLine(string.Join(Environment.NewLine, deleted));
        Assert.Contains(new SkinIssue(SkinLayer.AirPlayFull, SkinIssueKind.ArtUnreadable, ["af.png"]), deleted);
    }

    [Fact]
    public void A_valid_skin_has_no_issues_and_every_message_resolves()
    {
        DefaultSkins.EnsureInstalled(_root);
        var doc = SkinDocument.FromInfo(SkinLoader.Load(Path.Combine(_root, SkinArt.Default)));
        Assert.Empty(doc.Issues(_probe));

        foreach (var kind in Enum.GetValues<SkinIssueKind>())
        {
            var message = new SkinIssue(SkinLayer.SkinInfo, kind, ["a", "1", "2", "3", "4"]).Message;
            _out.WriteLine($"{kind}: {message}");
            Assert.NotEqual(SkinIssue.MessageKeyFor(kind), message);
        }
    }

    [Fact]
    public void Save_config_carries_the_geometry_the_strip_art_has_now()
    {
        var folder = Folder("stale");
        TestPngs.Write(Path.Combine(folder, "ae.png"), 320, 30);
        TestPngs.Write(Path.Combine(folder, "af.png"), 320, 30);
        var stale = new SkinAirPlay(Path.Combine(folder, "ae.png"), Path.Combine(folder, "af.png"), 300, 30,
            false, false, 1, 1, 10, 290, null, null, new SkinHitRect(0, 0, 300, 30));
        var doc = SkinDocument.New("x") with { Config = new SkinConfig(null, 1.0, Meta: SkinMeta.None, AirPlay: stale) };

        var saved = doc.SaveConfig(_probe).AirPlay;
        _out.WriteLine($"stale {stale}");
        _out.WriteLine($"saved {saved}");
        // A different width resets the fill range, and a whole-strip dropdown stays whole.
        Assert.Equal(stale with { Width = 320, FillStartX = 0, FillEndX = 320, DropdownHit = new SkinHitRect(0, 0, 320, 30) }, saved);
    }

    [Fact]
    public void The_strip_and_notice_still_derive_while_the_volume_art_is_missing()
    {
        var folder = Folder("strip-only");
        TestPngs.Write(Path.Combine(folder, "ae.png"), 300, 30);
        TestPngs.Write(Path.Combine(folder, "af.png"), 300, 30);
        var strip = new SkinAirPlay(Path.Combine(folder, "ae.png"), Path.Combine(folder, "af.png"), 300, 30,
            false, false, 1, 1, 0, 300, null, null, new SkinHitRect(0, 0, 300, 30));
        var doc = SkinDocument.New("x") with
        {
            Config = new SkinConfig(null, 1.0, Meta: SkinMeta.None, Notice: SkinLoader.DefaultNotice, AirPlay: strip),
        };

        var info = doc.ToInfo(_probe);
        _out.WriteLine($"{info}");
        Assert.False(info.IsValid);
        Assert.Equal(strip, info.AirPlay);
        Assert.Equal(SkinLoader.DefaultNotice, info.Notice);
    }
}
