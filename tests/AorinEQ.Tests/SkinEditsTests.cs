using AorinEQ.Core;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>One test per edit. The contract every edit keeps is checked by AssertRoundTrips: the
/// document after the edit is exactly the document a real save and a real reload give back, so
/// nothing the designer shows can be something the saved skin will not load as.</summary>
public class SkinEditsTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aorineq-edits-" + Guid.NewGuid().ToString("N"));
    private readonly ImageProbe _probe = new();

    public SkinEditsTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(Skins);
        Directory.CreateDirectory(Path.Combine(_root, "src"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Skins => Path.Combine(_root, "skins");

    private string Src(string name, int width, int height)
    {
        var path = Path.Combine(_root, "src", name);
        if (ImageProbe.IsGif(path)) TestPngs.WriteGif(path, width, height);
        else TestPngs.Write(path, width, height);
        return path;
    }

    /// <summary>A skin on disk, loaded as a document - saving it in place keeps every path.</summary>
    private SkinDocument Loaded(string name, string json = "{}", bool strip = false, bool noticeArt = false)
    {
        var folder = Path.Combine(Skins, name);
        Directory.CreateDirectory(folder);
        TestPngs.Write(Path.Combine(folder, "empty.png"), 300, 40);
        TestPngs.Write(Path.Combine(folder, "full.png"), 300, 40);
        if (strip)
        {
            TestPngs.Write(Path.Combine(folder, "airplay-empty.png"), 300, 30);
            TestPngs.Write(Path.Combine(folder, "airplay-full.png"), 300, 30);
        }
        if (noticeArt) TestPngs.Write(Path.Combine(folder, "notice.png"), 280, 48);
        File.WriteAllText(Path.Combine(folder, "skin.json"), json);
        var info = SkinLoader.Load(folder);
        Assert.True(info.IsValid, info.Error);
        return SkinDocument.FromInfo(info);
    }

    private static SkinDocument Relocated(SkinDocument d, string folder)
    {
        string In(string source, string layer) => Path.Combine(folder, layer + (ImageProbe.IsGif(source) ? ".gif" : ".png"));
        var c = d.Config;
        return d with
        {
            EmptyPath = In(d.EmptyPath, "empty"),
            FullPath = In(d.FullPath, "full"),
            MutedPath = d.MutedPath is null ? null : In(d.MutedPath, "muted"),
            Config = c with
            {
                AirPlay = c.AirPlay is { } a
                    ? a with { EmptyPath = In(a.EmptyPath, "airplay-empty"), FullPath = In(a.FullPath, "airplay-full") }
                    : null,
                Notice = c.Notice is { HasArt: true } n ? n with { ArtPath = In(n.ArtPath!, "notice") } : c.Notice,
            },
        };
    }

    private void AssertRoundTrips(SkinDocument edited)
    {
        var folder = SkinWriter.Save(Skins, edited.Name, edited.EmptyPath, edited.FullPath,
            edited.SaveConfig(_probe), edited.MutedPath);
        _out.WriteLine("skin.json: " + (File.Exists(Path.Combine(folder, "skin.json"))
            ? File.ReadAllText(Path.Combine(folder, "skin.json")) : "(none)"));
        var reloaded = SkinDocument.FromInfo(SkinLoader.Load(folder));
        var expected = Relocated(edited, folder);
        _out.WriteLine($"edited   {expected}");
        _out.WriteLine($"reloaded {reloaded}");
        Assert.Equal(expected, reloaded);
    }

    [Fact]
    public void SetName_renames_and_changes_nothing_else()
    {
        var doc = Loaded("a", """{"scale":1.5}""");
        var next = SkinEdits.SetName(doc, "b");
        _out.WriteLine($"expected {doc with { Name = "b" }}");
        _out.WriteLine($"actual   {next}");
        Assert.Equal(doc with { Name = "b" }, next);
        AssertRoundTrips(next);
    }

    [Fact]
    public void SetScale_and_SetFps_clamp_like_the_loader()
    {
        var doc = Loaded("scale");
        var scaled = SkinEdits.SetScale(doc, 9);
        var fps = SkinEdits.SetFps(scaled, 0);
        _out.WriteLine($"scale 9 -> {scaled.Config.Scale}; fps 0 -> {fps.Config.Fps}");
        Assert.Equal(4.0, scaled.Config.Scale);
        Assert.Equal(1.0, fps.Config.Fps);
        AssertRoundTrips(fps);
    }

    [Fact]
    public void SetMutedDim_and_SetMeta_round_trip()
    {
        var doc = Loaded("dim");
        var next = SkinEdits.SetMeta(SkinEdits.SetMutedDim(doc, -1),
            SkinMeta.Create("Title", "Me", "About it", "2", ["pink"], "https://example.com/x"));
        _out.WriteLine($"dim -1 -> {next.Config.MutedDim}; meta {next.Config.Meta}");
        Assert.Equal(0.0, next.Config.MutedDim);
        AssertRoundTrips(next);
    }

    [Fact]
    public void New_empty_art_keeps_the_fill_range_unless_its_width_changes()
    {
        var doc = Loaded("fill", """{"fillStartX":10,"fillEndX":290}""");
        var sameWidth = SkinEdits.SetArt(doc, SkinLayer.VolumeEmpty, Src("e300.png", 300, 40), 1, _probe);
        var wider = SkinEdits.SetArt(SkinEdits.SetArt(doc, SkinLayer.VolumeEmpty, Src("e320.png", 320, 40), 1, _probe),
            SkinLayer.VolumeFull, Src("f320.png", 320, 40), 1, _probe);

        _out.WriteLine($"same width: {sameWidth.Config.FillStartX}..{sameWidth.Config.FillEndX}");
        _out.WriteLine($"wider     : {wider.Config.FillStartX?.ToString() ?? "null"}..{wider.Config.FillEndX?.ToString() ?? "null"}");
        Assert.Equal((10, 290), (sameWidth.Config.FillStartX, sameWidth.Config.FillEndX));
        Assert.Null(wider.Config.FillStartX);
        Assert.Null(wider.Config.FillEndX);
        AssertRoundTrips(sameWidth);
        AssertRoundTrips(wider);
    }

    [Fact]
    public void Gif_art_is_always_one_declared_frame()
    {
        var doc = Loaded("gif");
        var next = SkinEdits.SetArt(doc, SkinLayer.VolumeFull, Src("full.gif", 300, 40), 5, _probe);
        _out.WriteLine($"FullFrames {next.Config.FullFrames} FullPath {next.FullPath}");
        Assert.Equal(1, next.Config.FullFrames);
        AssertRoundTrips(next);
    }

    [Fact]
    public void Muted_art_can_be_set_and_cleared()
    {
        var doc = Loaded("muted");
        var set = SkinEdits.SetArt(doc, SkinLayer.VolumeMuted, Src("m.png", 300, 80), 2, _probe);
        var cleared = SkinEdits.ClearArt(set, SkinLayer.VolumeMuted, _probe);
        _out.WriteLine($"set: {set.MutedPath} x{set.Config.MutedFrames}; cleared: {cleared.MutedPath ?? "(null)"} x{cleared.Config.MutedFrames}");
        Assert.Equal(2, set.Config.MutedFrames);
        Assert.Null(cleared.MutedPath);
        Assert.Equal(1, cleared.Config.MutedFrames);
        AssertRoundTrips(set);
        AssertRoundTrips(cleared);
    }

    [Fact]
    public void Required_art_cannot_be_cleared()
    {
        var doc = Loaded("required");
        var empty = SkinEdits.ClearArt(doc, SkinLayer.VolumeEmpty, _probe);
        var stripFull = SkinEdits.ClearArt(doc, SkinLayer.AirPlayFull, _probe);
        _out.WriteLine($"expected unchanged {doc}");
        _out.WriteLine($"clear empty      -> {empty}");
        _out.WriteLine($"clear strip full -> {stripFull}");
        Assert.Equal(doc, empty);
        Assert.Equal(doc, stripFull);
    }

    [Fact]
    public void New_strip_art_remeasures_the_strip()
    {
        var doc = Loaded("strip", """{"airplay":{"fillStartX":10,"fillEndX":290}}""", strip: true);
        var next = SkinEdits.SetArt(SkinEdits.SetArt(doc, SkinLayer.AirPlayEmpty, Src("ae.png", 320, 60), 2, _probe),
            SkinLayer.AirPlayFull, Src("af.png", 320, 30), 1, _probe);
        var a = next.Config.AirPlay!;
        _out.WriteLine($"{a}");
        Assert.Equal((320, 30, 2, 0, 320), (a.Width, a.Height, a.EmptyFrames, a.FillStartX, a.FillEndX));
        Assert.Equal(new SkinHitRect(0, 0, 320, 30), a.DropdownHit);
        AssertRoundTrips(next);
    }

    [Fact]
    public void New_notice_art_is_placed_where_the_loader_would_place_it()
    {
        var doc = Loaded("notice", """{"notice":{"background":"#FF102030"}}""");
        var art = Src("n.png", 260, 50);
        var next = SkinEdits.SetArt(doc, SkinLayer.NoticeArt, art, 1, _probe);
        var expected = SkinLoader.PlaceNoticeArt(SkinLoader.ClampNotice(doc.Config.Notice!), art, false, 1, 260, 50,
            null, null, null, null, null);
        _out.WriteLine($"placed {next.Config.Notice}");
        Assert.Equal(expected, next.Config.Notice);
        Assert.Equal((48, 15, 16, 16, 200), (next.Config.Notice!.Text.X, next.Config.Notice.Text.Y,
            next.Config.Notice.Glyph.X, next.Config.Notice.Glyph.Y, next.Config.Notice.TextWidth));
        AssertRoundTrips(next);
    }

    /// <summary>Notice art swapped between a sheet and a GIF keeps ArtIsGif and ArtFrames in step,
    /// so Issues and PlaceNotice probe the same frame count.</summary>
    [Fact]
    public void Swapping_notice_art_between_png_and_gif_keeps_its_kind_and_frames_in_step()
    {
        var doc = Loaded("swap", """{"notice":{}}""");
        var sheet = SkinEdits.SetArt(doc, SkinLayer.NoticeArt, Src("n2.png", 260, 100), 2, _probe);
        var gif = SkinEdits.SetArt(sheet, SkinLayer.NoticeArt, Src("n.gif", 260, 50), 2, _probe);
        var back = SkinEdits.SetArt(gif, SkinLayer.NoticeArt, Src("n3.png", 260, 150), 3, _probe);
        foreach (var (what, d) in new[] { ("sheet", sheet), ("gif", gif), ("back", back) })
            _out.WriteLine($"{what}: isGif={d.Config.Notice!.ArtIsGif} frames={d.Config.Notice.ArtFrames} " +
                           $"{d.Config.Notice.Width}x{d.Config.Notice.Height} issues={d.Issues(_probe).Count}");
        _out.WriteLine("expected sheet (false,2,50), gif (true,1,50), back (false,3,50), no issues");
        Assert.Equal((false, 2, 50), (sheet.Config.Notice!.ArtIsGif, sheet.Config.Notice.ArtFrames, sheet.Config.Notice.Height));
        Assert.Equal((true, 1, 50), (gif.Config.Notice!.ArtIsGif, gif.Config.Notice.ArtFrames, gif.Config.Notice.Height));
        Assert.Equal((false, 3, 50), (back.Config.Notice!.ArtIsGif, back.Config.Notice.ArtFrames, back.Config.Notice.Height));
        Assert.Empty(gif.Issues(_probe));
        Assert.Empty(back.Issues(_probe));
        AssertRoundTrips(gif);
        AssertRoundTrips(back);
    }

    [Fact]
    public void SetFrames_changes_sheets_and_leaves_gifs_alone()
    {
        var doc = SkinEdits.SetArt(SkinEdits.SetArt(Loaded("frames"), SkinLayer.VolumeEmpty, Src("e.png", 300, 80), 2, _probe),
            SkinLayer.VolumeFull, Src("f.png", 300, 80), 2, _probe);
        var four = SkinEdits.SetFrames(SkinEdits.SetFrames(doc, SkinLayer.VolumeEmpty, 4, _probe), SkinLayer.VolumeFull, 4, _probe);
        var gif = SkinEdits.SetArt(four, SkinLayer.VolumeMuted, Src("m.gif", 300, 20), 1, _probe);
        var gifFrames = SkinEdits.SetFrames(gif, SkinLayer.VolumeMuted, 3, _probe);

        _out.WriteLine($"empty x{four.Config.EmptyFrames} full x{four.Config.FullFrames}; muted gif x{gifFrames.Config.MutedFrames}");
        Assert.Equal((4, 4), (four.Config.EmptyFrames, four.Config.FullFrames));
        Assert.Equal(gif, gifFrames);
        Assert.Equal(20, four.ToInfo(_probe).Height);
        AssertRoundTrips(four);
    }

    [Fact]
    public void SetFrames_on_strip_and_notice_art_remeasures_them()
    {
        var strip = SkinEdits.SetFrames(SkinEdits.SetFrames(Loaded("sf", "{}", strip: true), SkinLayer.AirPlayEmpty, 2, _probe),
            SkinLayer.AirPlayFull, 2, _probe);
        var notice = SkinEdits.SetFrames(Loaded("nf", """{"notice":{}}""", noticeArt: true), SkinLayer.NoticeArt, 2, _probe);
        _out.WriteLine($"strip {strip.Config.AirPlay}; notice {notice.Config.Notice}");
        Assert.Equal(15, strip.Config.AirPlay!.Height);
        Assert.Equal((2, 24), (notice.Config.Notice!.ArtFrames, notice.Config.Notice.Height));
        AssertRoundTrips(strip);
        AssertRoundTrips(notice);
    }

    [Fact]
    public void Clearing_notice_art_returns_to_a_plate()
    {
        var doc = Loaded("plate", """{"notice":{"background":"#FF102030","text":{"x":60}}}""", noticeArt: true);
        var plate = SkinEdits.ClearArt(doc, SkinLayer.NoticeArt, _probe);
        _out.WriteLine($"art notice {doc.Config.Notice}");
        _out.WriteLine($"plate      {plate.Config.Notice}; expected text (0,0), glyph (16,0): positions measured on art mean nothing on a plate");
        Assert.False(plate.Config.Notice!.HasArt);
        Assert.Equal("#FF102030", plate.Config.Notice.Background);
        Assert.Equal((0, 0, 16, 0), (plate.Config.Notice.Text.X, plate.Config.Notice.Text.Y,
            plate.Config.Notice.Glyph.X, plate.Config.Notice.Glyph.Y));
        AssertRoundTrips(plate);
    }

    [Fact]
    public void SetFillRange_clamps_and_a_full_width_range_is_no_range()
    {
        var doc = Loaded("range", "{}", strip: true);
        var clamped = SkinEdits.SetFillRange(doc, SkinLayer.VolumeBar, -20, 999, _probe);
        var custom = SkinEdits.SetFillRange(doc, SkinLayer.VolumeBar, 12, 280, _probe);
        var strip = SkinEdits.SetFillRange(doc, SkinLayer.AirPlay, 8, 9999, _probe);
        _out.WriteLine($"clamped {clamped.Config.FillStartX?.ToString() ?? "null"}..{clamped.Config.FillEndX?.ToString() ?? "null"}; " +
                       $"custom {custom.Config.FillStartX}..{custom.Config.FillEndX}; strip {strip.Config.AirPlay!.FillStartX}..{strip.Config.AirPlay.FillEndX}");
        Assert.Equal(doc, clamped);
        Assert.Equal((12, 280), (custom.Config.FillStartX, custom.Config.FillEndX));
        Assert.Equal((8, 300), (strip.Config.AirPlay.FillStartX, strip.Config.AirPlay.FillEndX));
        AssertRoundTrips(custom);
        AssertRoundTrips(strip);
    }

    [Fact]
    public void SetText_keeps_a_styled_hidden_number_and_drops_a_plain_one()
    {
        var doc = Loaded("text", "{}", strip: true);
        var styled = SkinEdits.SetText(doc, SkinLayer.PercentText, new SkinText(false, 20, 4, FontSize: 30), _probe);
        var plain = SkinEdits.SetText(styled, SkinLayer.PercentText, SkinText.Hidden, _probe);
        var name = SkinEdits.SetText(doc, SkinLayer.AirPlayName, new SkinText(true, 12, 3, FontSize: 1), _probe);
        _out.WriteLine($"styled {styled.Config.Text}; plain {plain.Config.Text?.ToString() ?? "(null)"}; name {name.Config.AirPlay!.Name}");
        Assert.Equal(new SkinText(false, 20, 4, FontSize: 30), styled.Config.Text);
        Assert.Null(plain.Config.Text);
        Assert.Equal(new SkinText(true, 12, 3, FontSize: 4), name.Config.AirPlay.Name);
        AssertRoundTrips(styled);
        AssertRoundTrips(name);
    }

    [Fact]
    public void Notice_text_keeps_its_styling_clamped_and_is_always_shown()
    {
        var doc = Loaded("ntext", """{"notice":{}}""");
        var next = SkinEdits.SetText(doc, SkinLayer.NoticeText,
            new SkinText(false, 5, 6, FontSize: 15, OutlineColor: "#FF000000", OutlineWidth: 30, Align: "RIGHT"), _probe);
        // A plate keeps its text position (the writer writes it, the loader reads it back).
        var expected = SkinLoader.DefaultNotice.Text with
        {
            X = 5, Y = 6, FontSize = 15, OutlineColor = "#FF000000", OutlineWidth = SkinClamps.MaxOutlineWidth, Align = "right",
        };
        _out.WriteLine($"expected {expected}");
        _out.WriteLine($"actual   {next.Config.Notice!.Text}");
        Assert.Equal(expected, next.Config.Notice.Text);
        AssertRoundTrips(next);
    }

    /// <summary>Weight, italic, letter spacing, outline and shadow reach every text the same way:
    /// clamped as the loader clamps them, and saved and reloaded as they are.</summary>
    [Theory]
    [InlineData(SkinLayer.PercentText)]
    [InlineData(SkinLayer.AirPlayName)]
    [InlineData(SkinLayer.AirPlayLevel)]
    [InlineData(SkinLayer.NoticeText)]
    public void SetText_carries_every_type_field_on_every_text(SkinLayer layer)
    {
        var doc = Loaded("style-" + layer, """{"notice":{}}""", strip: true, noticeArt: true);
        var start = SkinEdits.TextOf(doc, layer) ?? SkinEdits.HiddenTextOf(layer) with { Show = true, X = 4, Y = 2 };
        var next = SkinEdits.SetText(doc, layer, start with
        {
            Weight = 449, Italic = true, LetterSpacing = 250,
            OutlineColor = "#FF112233", OutlineWidth = 1.5, ShadowColor = "#80445566", ShadowBlur = 7, ShadowDepth = 99,
        }, _probe);
        var got = SkinEdits.TextOf(next, layer)!;
        _out.WriteLine($"{layer}: from {start}");
        _out.WriteLine($"{layer}: to   {got}");
        _out.WriteLine("expected weight 400 (449 rounded), italic, spacing 100 (clamped), outline #FF112233 1.5, shadow #80445566 blur 7 depth 50 (clamped)");
        Assert.Equal((400, true, 100.0), (got.Weight, got.Italic, got.LetterSpacing));
        Assert.Equal(("#FF112233", 1.5), (got.OutlineColor, got.OutlineWidth));
        Assert.Equal(("#80445566", 7.0, 50.0), (got.ShadowColor, got.ShadowBlur, got.ShadowDepth));
        AssertRoundTrips(next);
    }

    /// <summary>A hidden text carrying nothing is no text - and "nothing" includes the text's own
    /// default weight, which for the AirPlay labels is Normal, not the number's SemiBold.</summary>
    [Fact]
    public void A_hidden_text_at_its_own_default_weight_is_no_text()
    {
        var doc = Loaded("hidden", """{"percentText":{"show":true,"x":3,"y":4},"airplay":{"nameText":{"show":true,"x":3,"y":4}}}""", strip: true);
        var r = (Number: SkinEdits.SetText(doc, SkinLayer.PercentText, SkinEdits.HiddenTextOf(SkinLayer.PercentText), _probe).Config.Text,
            Label: SkinEdits.SetText(doc, SkinLayer.AirPlayName, SkinEdits.HiddenTextOf(SkinLayer.AirPlayName), _probe).Config.AirPlay!.Name,
            LabelAtNumberWeight: SkinEdits.SetText(doc, SkinLayer.AirPlayName, SkinText.Hidden, _probe).Config.AirPlay!.Name);
        _out.WriteLine($"HiddenTextOf: number {SkinEdits.HiddenTextOf(SkinLayer.PercentText)}, label {SkinEdits.HiddenTextOf(SkinLayer.AirPlayLevel)}");
        _out.WriteLine($"expected (null, null, a hidden label at 600); actual {r}");
        Assert.Equal(SkinText.Hidden, SkinEdits.HiddenTextOf(SkinLayer.PercentText));
        Assert.Equal(SkinText.HiddenLabel, SkinEdits.HiddenTextOf(SkinLayer.AirPlayName));
        Assert.Equal(SkinText.HiddenLabel, SkinEdits.HiddenTextOf(SkinLayer.AirPlayLevel));
        Assert.Null(r.Number);
        Assert.Null(r.Label);
        Assert.Equal(SkinText.Hidden, r.LabelAtNumberWeight); // 600 is not a label's default: it is kept
        AssertRoundTrips(SkinEdits.SetText(doc, SkinLayer.AirPlayName, SkinText.Hidden, _probe));
    }

    [Fact]
    public void TextOf_reads_each_text_layer()
    {
        var doc = SkinEdits.SetText(Loaded("of", """{"percentText":{"show":true,"x":3,"y":4},"notice":{}}""", strip: true),
            SkinLayer.AirPlayLevel, new SkinText(true, 7, 8), _probe);
        foreach (var layer in new[] { SkinLayer.PercentText, SkinLayer.AirPlayName, SkinLayer.AirPlayLevel, SkinLayer.NoticeText, SkinLayer.VolumeEmpty })
            _out.WriteLine($"{layer}: {SkinEdits.TextOf(doc, layer)?.ToString() ?? "(none)"}");
        _out.WriteLine("expected PercentText (3,4), AirPlayName none, AirPlayLevel (7,8), NoticeText the default plate text, VolumeEmpty none");
        Assert.Equal(new SkinText(true, 3, 4), SkinEdits.TextOf(doc, SkinLayer.PercentText));
        Assert.Null(SkinEdits.TextOf(doc, SkinLayer.AirPlayName));
        Assert.Equal(new SkinText(true, 7, 8), SkinEdits.TextOf(doc, SkinLayer.AirPlayLevel));
        Assert.Equal(SkinLoader.DefaultNotice.Text, SkinEdits.TextOf(doc, SkinLayer.NoticeText));
        Assert.Null(SkinEdits.TextOf(doc, SkinLayer.VolumeEmpty));
    }

    /// <summary>Review focus: a drag far past the artboard at any zoom ends at its edge.</summary>
    [Fact]
    public void Moving_text_off_the_artboard_stops_at_its_edge()
    {
        var doc = Loaded("move", """{"percentText":{"show":true,"x":10,"y":5},"airplay":{"nameText":{"show":true,"x":20,"y":2}}}""", strip: true);
        var far = SkinEdits.MoveText(doc, SkinLayer.PercentText, 5000, 5000, _probe);
        var back = SkinEdits.MoveText(far, SkinLayer.PercentText, -9999, -9999, _probe);
        var name = SkinEdits.MoveText(doc, SkinLayer.AirPlayName, 5000, 5000, _probe);
        _out.WriteLine($"far {far.Config.Text}; back {back.Config.Text}; name {name.Config.AirPlay!.Name}");
        Assert.Equal((300, 40), (far.Config.Text!.X, far.Config.Text.Y));
        Assert.Equal((0, 0), (back.Config.Text!.X, back.Config.Text.Y));
        Assert.Equal((300, 30), (name.Config.AirPlay.Name!.X, name.Config.AirPlay.Name.Y));
        Assert.Equal(doc, SkinEdits.MoveText(doc, SkinLayer.AirPlayLevel, 5, 5, _probe)); // no level text: nothing to move
        AssertRoundTrips(far);
    }

    /// <summary>A text on a board with no art to measure it by (none chosen yet, or deleted while
    /// open) stays where it is: clamping it into a 0x0 board would throw its position away.</summary>
    [Fact]
    public void Moving_text_on_a_board_with_no_art_changes_nothing()
    {
        var blank = SkinEdits.SetText(SkinDocument.New("blank"), SkinLayer.PercentText, new SkinText(true, 10, 5), _probe);
        var deleted = Loaded("gone", """{"percentText":{"show":true,"x":10,"y":5}}""");
        File.Delete(deleted.EmptyPath);
        var movedBlank = SkinEdits.MoveText(blank, SkinLayer.PercentText, 3, 3, _probe);
        var movedDeleted = SkinEdits.MoveText(deleted, SkinLayer.PercentText, 3, 3, _probe);
        _out.WriteLine($"expected (10,5) both; no art {movedBlank.Config.Text}; art deleted {movedDeleted.Config.Text}");
        Assert.Equal(blank, movedBlank);
        Assert.Equal(deleted, movedDeleted);
    }

    [Fact]
    public void AddAirPlay_measures_the_strip_and_places_both_labels_and_RemoveAirPlay_takes_it_away()
    {
        var doc = Loaded("add");
        var added = SkinEdits.AddAirPlay(doc, Src("ae.png", 300, 30), Src("af.png", 300, 30), _probe);
        var a = added.Config.AirPlay!;
        _out.WriteLine($"{a}");
        Assert.Equal((300, 30, 0, 300), (a.Width, a.Height, a.FillStartX, a.FillEndX));
        Assert.Equal(new SkinHitRect(0, 0, 300, 30), a.DropdownHit);
        // The labels start at their own default weight, Normal, as the loader reads a label without one.
        Assert.Equal(new SkinText(true, 10, 5, Weight: SkinClamps.DefaultLabelWeight), a.Name);
        Assert.Equal(new SkinText(true, 290, 5, Weight: SkinClamps.DefaultLabelWeight, Align: "right"), a.Percent);
        Assert.Null(a.ConnectHit);
        AssertRoundTrips(added);
        var removed = SkinEdits.RemoveAirPlay(added);
        Assert.Null(removed.Config.AirPlay);
        AssertRoundTrips(removed);
    }

    [Fact]
    public void SetHit_clamps_regions_and_a_null_dropdown_is_the_whole_strip()
    {
        var doc = SkinEdits.AddPowerArea(Loaded("hits", "{}", strip: true));
        _out.WriteLine($"power area added: {doc.Config.AirPlay!.ConnectHit}");
        Assert.Equal(new SkinHitRect(270, 0, 30, 30), doc.Config.AirPlay.ConnectHit);
        Assert.Equal(doc, SkinEdits.AddPowerArea(doc)); // already has one

        var dropdown = SkinEdits.SetHit(doc, SkinLayer.AirPlayDropdown, new SkinHitRect(250, 0, 90, 99));
        var whole = SkinEdits.SetHit(dropdown, SkinLayer.AirPlayDropdown, null);
        var gone = SkinEdits.SetHit(doc, SkinLayer.AirPlayPower, new SkinHitRect(300, 0, 10, 10));
        _out.WriteLine($"dropdown {dropdown.Config.AirPlay!.DropdownHit}; whole {whole.Config.AirPlay!.DropdownHit}; power {gone.Config.AirPlay!.ConnectHit?.ToString() ?? "(none)"}");
        Assert.Equal(new SkinHitRect(250, 0, 50, 30), dropdown.Config.AirPlay.DropdownHit);
        Assert.Equal(new SkinHitRect(0, 0, 300, 30), whole.Config.AirPlay.DropdownHit);
        Assert.Null(gone.Config.AirPlay.ConnectHit);
        AssertRoundTrips(dropdown);
    }

    [Fact]
    public void SetConnectColor_treats_blank_as_none()
    {
        var doc = Loaded("connect", "{}", strip: true);
        var lit = SkinEdits.SetConnectColor(doc, "#FF4CC2FF");
        var none = SkinEdits.SetConnectColor(lit, "   ");
        _out.WriteLine($"expected #FF4CC2FF then none; actual {lit.Config.AirPlay!.ConnectColor} then {none.Config.AirPlay!.ConnectColor ?? "(none)"}");
        Assert.Equal("#FF4CC2FF", lit.Config.AirPlay!.ConnectColor);
        Assert.Null(none.Config.AirPlay!.ConnectColor);
        AssertRoundTrips(lit);
    }

    [Fact]
    public void The_notice_can_be_switched_on_and_off()
    {
        var doc = Loaded("onoff");
        var on = SkinEdits.SetNoticeEnabled(doc, true);
        var off = SkinEdits.SetNoticeEnabled(on, false);
        _out.WriteLine($"expected on = {SkinLoader.DefaultNotice}, off = none");
        _out.WriteLine($"actual   on = {on.Config.Notice?.ToString() ?? "(none)"}, off = {off.Config.Notice?.ToString() ?? "(none)"}");
        Assert.Equal(SkinLoader.DefaultNotice, on.Config.Notice);
        Assert.Null(off.Config.Notice);
        AssertRoundTrips(on);
    }

    [Fact]
    public void SetNotice_clamps_the_plate()
    {
        var doc = SkinEdits.SetNoticeEnabled(Loaded("clamp"), true);
        var next = SkinEdits.SetNotice(doc, doc.Config.Notice! with { CornerRadius = 500, PadX = -3 }, _probe);
        _out.WriteLine($"{next.Config.Notice}");
        Assert.Equal((64.0, 0.0), (next.Config.Notice!.CornerRadius, next.Config.Notice.PadX));
        AssertRoundTrips(next);
    }

    /// <summary>Notice art that cannot be read still gets its plate clamped, so no edit can store a
    /// value the loader would never give back while the art is missing.</summary>
    [Fact]
    public void A_notice_on_missing_art_is_still_clamped()
    {
        var doc = SkinEdits.SetArt(SkinEdits.SetNoticeEnabled(Loaded("missing"), true), SkinLayer.NoticeArt,
            Src("gone.png", 260, 50), 1, _probe);
        File.Delete(doc.Config.Notice!.ArtPath!);
        var notice = SkinEdits.SetNotice(doc, doc.Config.Notice with { CornerRadius = 500 }, _probe);
        var text = SkinEdits.SetText(notice, SkinLayer.NoticeText, new SkinText(true, 48, 15, FontSize: double.NaN), _probe);
        _out.WriteLine($"expected corner 64, font 14; actual corner {text.Config.Notice!.CornerRadius}, font {text.Config.Notice.Text.FontSize}");
        Assert.Equal((64.0, SkinClamps.DefaultFontSize), (text.Config.Notice.CornerRadius, text.Config.Notice.Text.FontSize));
        Assert.True(text.Config.Notice.HasArt);
    }

    [Fact]
    public void A_border_switched_on_starts_from_the_plate_made_opaque()
    {
        var doc = SkinEdits.SetNoticeEnabled(Loaded("border"), true);
        var on = SkinEdits.SetNoticeBorder(doc, true);
        var off = SkinEdits.SetNoticeBorder(on, false);
        _out.WriteLine($"on {on.Config.Notice!.Border}; off {off.Config.Notice!.Border ?? "(none)"}");
        Assert.Equal("#FF202020", on.Config.Notice.Border);
        Assert.Null(off.Config.Notice.Border);
        AssertRoundTrips(on);
    }

    [Fact]
    public void The_icon_can_follow_the_text_colour_again()
    {
        var doc = SkinEdits.SetText(SkinEdits.SetNoticeEnabled(Loaded("glyph"), true), SkinLayer.NoticeText,
            SkinLoader.DefaultNotice.Text with { Color = "#FF00FF00" }, _probe);
        var own = SkinEdits.SetNoticeGlyphFollowsText(doc, false);
        var follows = SkinEdits.SetNoticeGlyphFollowsText(own, true);
        _out.WriteLine($"expected own #FF00FF00, following none; actual own {own.Config.Notice!.Glyph.Color ?? "(none)"}, " +
                       $"following {follows.Config.Notice!.Glyph.Color ?? "(none)"}");
        Assert.Equal("#FF00FF00", own.Config.Notice!.Glyph.Color);
        Assert.Null(follows.Config.Notice!.Glyph.Color);
        AssertRoundTrips(own);
    }

    [Fact]
    public void Notice_text_and_icon_move_and_resize_inside_the_art()
    {
        var doc = Loaded("nmove", """{"notice":{}}""", noticeArt: true);
        var text = SkinEdits.MoveNoticeElement(doc, SkinLayer.NoticeText, 9999, -9999);
        var glyph = SkinEdits.MoveNoticeElement(doc, SkinLayer.NoticeGlyph, -5, 3);
        var wide = SkinEdits.SetNoticeTextWidth(doc, 9999);
        var n = doc.Config.Notice!;
        _out.WriteLine($"start {n}");
        _out.WriteLine($"text {text.Config.Notice}; glyph {glyph.Config.Notice}; wide {wide.Config.Notice}");
        Assert.Equal((280, 0, 0), (text.Config.Notice!.Text.X, text.Config.Notice.Text.Y, text.Config.Notice.TextWidth));
        Assert.Equal((n.Glyph.X - 5, n.Glyph.Y + 3), (glyph.Config.Notice!.Glyph.X, glyph.Config.Notice.Glyph.Y));
        Assert.Equal(280 - n.Text.X, wide.Config.Notice!.TextWidth);
        AssertRoundTrips(text);
        AssertRoundTrips(wide);
    }

    [Fact]
    public void A_fill_handle_cannot_be_dragged_past_the_other()
    {
        var doc = Loaded("handles", """{"fillStartX":10,"fillEndX":290}""");
        var start = SkinEdits.Drag(doc, SkinLayer.VolumeBar, SkinHandle.FillStart, 500, 0, _probe);
        var end = SkinEdits.Drag(doc, SkinLayer.VolumeBar, SkinHandle.FillEnd, -500, 0, _probe);
        _out.WriteLine($"start dragged right: {start.Config.FillStartX}..{start.Config.FillEndX}; end dragged left: {end.Config.FillStartX}..{end.Config.FillEndX}");
        Assert.Equal((289, 290), (start.Config.FillStartX, start.Config.FillEndX));
        Assert.Equal((10, 11), (end.Config.FillStartX, end.Config.FillEndX));
        AssertRoundTrips(start);
    }

    /// <summary>Review focus: an area dragged past the end of the strip stops there, whole.</summary>
    [Fact]
    public void Dragging_an_area_past_the_strip_keeps_its_size()
    {
        var doc = SkinEdits.AddPowerArea(Loaded("area", "{}", strip: true));
        var right = SkinEdits.Drag(doc, SkinLayer.AirPlayPower, SkinHandle.Move, 500, 500, _probe);
        var left = SkinEdits.Drag(doc, SkinLayer.AirPlayPower, SkinHandle.Move, -500, -500, _probe);
        var shrunk = SkinEdits.Drag(doc, SkinLayer.AirPlayPower, SkinHandle.BottomRight, -100, -100, _probe);
        _out.WriteLine($"right {right.Config.AirPlay!.ConnectHit}; left {left.Config.AirPlay!.ConnectHit}; shrunk {shrunk.Config.AirPlay!.ConnectHit}");
        Assert.Equal(new SkinHitRect(270, 0, 30, 30), right.Config.AirPlay.ConnectHit);
        Assert.Equal(new SkinHitRect(0, 0, 30, 30), left.Config.AirPlay.ConnectHit);
        Assert.Equal(new SkinHitRect(270, 0, 1, 1), shrunk.Config.AirPlay.ConnectHit);
        AssertRoundTrips(left);
    }

    [Fact]
    public void Drag_routes_text_handles_to_their_edits()
    {
        var doc = Loaded("route", """{"percentText":{"show":true,"x":10,"y":5},"notice":{}}""", noticeArt: true);
        var cases = new (string What, SkinDocument Expected, SkinDocument Actual)[]
        {
            ("percent text move", SkinEdits.MoveText(doc, SkinLayer.PercentText, 4, 2, _probe),
                SkinEdits.Drag(doc, SkinLayer.PercentText, SkinHandle.Move, 4, 2, _probe)),
            ("notice icon move", SkinEdits.MoveNoticeElement(doc, SkinLayer.NoticeGlyph, 4, 2),
                SkinEdits.Drag(doc, SkinLayer.NoticeGlyph, SkinHandle.Move, 4, 2, _probe)),
            ("notice text width", SkinEdits.SetNoticeTextWidth(doc, doc.Config.Notice!.TextWidth - 7),
                SkinEdits.Drag(doc, SkinLayer.NoticeText, SkinHandle.Right, -7, 0, _probe)),
            ("art is select-only", doc, SkinEdits.Drag(doc, SkinLayer.VolumeEmpty, SkinHandle.Move, 4, 2, _probe)),
        };
        foreach (var (what, expected, actual) in cases)
        {
            _out.WriteLine($"{what}: expected {expected.Config.Text} / {expected.Config.Notice}");
            _out.WriteLine($"{what}: actual   {actual.Config.Text} / {actual.Config.Notice}");
            Assert.Equal(expected, actual);
        }
    }

    /// <summary>Review focus: a corner dragged past the strip's edge stops there and the edges it
    /// does not hold stay put - clamping position and size together would move the far edge.</summary>
    [Theory]
    [InlineData(SkinHandle.TopLeft, -500, -500, 0, 0, 120, 25)]
    [InlineData(SkinHandle.TopLeft, -500, 0, 0, 5, 120, 20)]
    [InlineData(SkinHandle.TopLeft, 0, -500, 100, 0, 20, 25)]
    [InlineData(SkinHandle.BottomRight, 500, 500, 100, 5, 200, 25)]
    public void Resizing_an_area_past_the_strip_moves_only_the_dragged_edges(SkinHandle handle, int dx, int dy,
        int x, int y, int w, int h)
    {
        var doc = SkinEdits.SetHit(Loaded("over" + handle + dx + dy, "{}", strip: true), SkinLayer.AirPlayDropdown,
            new SkinHitRect(100, 5, 20, 20));
        var resized = SkinEdits.Drag(doc, SkinLayer.AirPlayDropdown, handle, dx, dy, _probe);
        _out.WriteLine($"{handle} ({dx},{dy}) on [100,5 20x20] in 300x30: expected {new SkinHitRect(x, y, w, h)}, actual {resized.Config.AirPlay!.DropdownHit}");
        Assert.Equal(new SkinHitRect(x, y, w, h), resized.Config.AirPlay.DropdownHit);
        AssertRoundTrips(resized);
    }

    [Theory]
    [InlineData(SkinHandle.TopLeft, 5, 5, 105, 15, 15, 25)]
    [InlineData(SkinHandle.TopRight, 5, 5, 100, 15, 25, 25)]
    [InlineData(SkinHandle.BottomLeft, 5, 5, 105, 10, 15, 35)]
    [InlineData(SkinHandle.BottomRight, 5, 5, 100, 10, 25, 35)]
    [InlineData(SkinHandle.TopLeft, 99, 99, 119, 39, 1, 1)]
    public void Resize_moves_only_the_dragged_corner_and_never_inverts(SkinHandle handle, int dx, int dy,
        int x, int y, int w, int h)
    {
        var resized = SkinEdits.Resize(new SkinHitRect(100, 10, 20, 30), handle, dx, dy);
        _out.WriteLine($"{handle} ({dx},{dy}) -> {resized}");
        Assert.Equal(new SkinHitRect(x, y, w, h), resized);
    }

    /// <summary>Every SkinLayer value, each with the board written out here: a layer added to the
    /// enum without an expected board fails, rather than falling to Skin info unnoticed.</summary>
    [Fact]
    public void Every_layer_belongs_to_one_artboard()
    {
        var expected = new Dictionary<SkinLayer, SkinLayer>
        {
            [SkinLayer.SkinInfo] = SkinLayer.SkinInfo,
            [SkinLayer.VolumeBar] = SkinLayer.VolumeBar,
            [SkinLayer.VolumeEmpty] = SkinLayer.VolumeBar,
            [SkinLayer.VolumeFull] = SkinLayer.VolumeBar,
            [SkinLayer.VolumeMuted] = SkinLayer.VolumeBar,
            [SkinLayer.PercentText] = SkinLayer.VolumeBar,
            [SkinLayer.AirPlay] = SkinLayer.AirPlay,
            [SkinLayer.AirPlayEmpty] = SkinLayer.AirPlay,
            [SkinLayer.AirPlayFull] = SkinLayer.AirPlay,
            [SkinLayer.AirPlayName] = SkinLayer.AirPlay,
            [SkinLayer.AirPlayLevel] = SkinLayer.AirPlay,
            [SkinLayer.AirPlayDropdown] = SkinLayer.AirPlay,
            [SkinLayer.AirPlayPower] = SkinLayer.AirPlay,
            [SkinLayer.Notice] = SkinLayer.Notice,
            [SkinLayer.NoticeArt] = SkinLayer.Notice,
            [SkinLayer.NoticeText] = SkinLayer.Notice,
            [SkinLayer.NoticeGlyph] = SkinLayer.Notice,
        };
        var wrong = new List<string>();
        foreach (var layer in Enum.GetValues<SkinLayer>())
        {
            var board = SkinLayers.BoardOf(layer);
            _out.WriteLine($"{layer}: expected board {(expected.TryGetValue(layer, out var e) ? e : "(none written)")}, actual {board}");
            if (!expected.TryGetValue(layer, out var want) || want != board) wrong.Add(layer.ToString());
        }
        Assert.Empty(wrong);
        Assert.Equal(Enum.GetValues<SkinLayer>().Length, expected.Count);
    }

    /// <summary>A strip or notice the loader could not load stays a blocking issue through every
    /// unrelated edit, and only the explicit decision clears it: removing or re-adding the strip,
    /// turning the notice off (or on as a plate) or giving it new art. Saving after that writes
    /// what the user chose and nothing else.</summary>
    [Fact]
    public void Art_that_failed_to_load_is_resolved_only_by_an_explicit_edit()
    {
        var folder = Path.Combine(Skins, "broken");
        Directory.CreateDirectory(folder);
        TestPngs.Write(Path.Combine(folder, "empty.png"), 300, 40);
        TestPngs.Write(Path.Combine(folder, "full.png"), 300, 40);
        TestPngs.Write(Path.Combine(folder, "airplay-empty.png"), 300, 30);
        TestPngs.Write(Path.Combine(folder, "airplay-full.png"), 280, 30);
        TestPngs.Write(Path.Combine(folder, "notice.png"), 280, 48);
        File.WriteAllText(Path.Combine(folder, "skin.json"), """{"notice":{"frames":5}}""");
        var doc = SkinDocument.FromInfo(SkinLoader.Load(folder));
        _out.WriteLine($"loaded: airPlayError={doc.AirPlayError ?? "(none)"} noticeError={doc.NoticeError ?? "(none)"}");
        Assert.NotNull(doc.AirPlayError);
        Assert.NotNull(doc.NoticeError);

        var scaled = SkinEdits.SetScale(doc, 2);
        var removed = SkinEdits.RemoveAirPlay(doc);
        var readded = SkinEdits.AddAirPlay(doc, Src("ae.png", 300, 30), Src("af.png", 300, 30), _probe);
        var off = SkinEdits.SetNoticeEnabled(doc, false);
        var newArt = SkinEdits.SetArt(doc, SkinLayer.NoticeArt, Src("n.png", 260, 50), 1, _probe);
        foreach (var (what, d) in new[] { ("scaled", scaled), ("strip removed", removed), ("strip re-added", readded),
                                          ("notice off", off), ("new notice art", newArt) })
            _out.WriteLine($"{what}: airPlayError={d.AirPlayError ?? "(none)"} noticeError={d.NoticeError ?? "(none)"} " +
                           $"strip={(d.Config.AirPlay is null ? "none" : "yes")} notice={d.Config.Notice?.ToString() ?? "none"}");

        Assert.Equal((doc.AirPlayError, doc.NoticeError), (scaled.AirPlayError, scaled.NoticeError));
        Assert.Null(removed.AirPlayError);
        Assert.Equal(doc.NoticeError, removed.NoticeError);
        Assert.Null(readded.AirPlayError);
        Assert.NotNull(readded.Config.AirPlay);
        Assert.Null(off.NoticeError);
        Assert.Null(off.Config.Notice);
        Assert.Equal(doc.AirPlayError, off.AirPlayError);
        Assert.Null(newArt.NoticeError);
        Assert.True(newArt.Config.Notice!.HasArt);

        var resolved = SkinEdits.RemoveAirPlay(off);
        Assert.DoesNotContain(resolved.Issues(_probe),
            i => i.Kind is SkinIssueKind.AirPlayUnloadable or SkinIssueKind.NoticeUnloadable);
        AssertRoundTrips(resolved);
    }
}
