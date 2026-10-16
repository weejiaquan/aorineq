using AorinEQ.Core;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>The optional "device changed" notice a skin may describe: a "notice" block, a
/// notice.png, or both. Same contract as the AirPlay bar: absence is every existing skin, and
/// nothing about the notice can ever cost somebody their volume skin.</summary>
public class SkinNoticeTests : IDisposable
{
    private readonly string _root;
    private readonly ITestOutputHelper _out;

    public SkinNoticeTests(ITestOutputHelper output)
    {
        _out = output;
        _root = Path.Combine(Path.GetTempPath(), "aorineq-notice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string MakeSkin(string name, string? json, (int W, int H)? noticeArt = null, int noticeFrames = 1)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        TestPngs.Write(Path.Combine(folder, "empty.png"), 200, 40);
        TestPngs.Write(Path.Combine(folder, "full.png"), 200, 40);
        if (noticeArt is { } a) TestPngs.Write(Path.Combine(folder, "notice.png"), a.W, a.H * noticeFrames);
        if (json is not null) File.WriteAllText(Path.Combine(folder, "skin.json"), json);
        return folder;
    }

    private SkinInfo LoadLogged(string folder)
    {
        var info = SkinLoader.Load(folder);
        _out.WriteLine($"valid={info.IsValid} notice={info.Notice} noticeError={info.NoticeError ?? "(none)"}");
        return info;
    }

    [Fact]
    public void A_skin_without_a_notice_block_or_art_has_no_notice()
    {
        var info = LoadLogged(MakeSkin("legacy", null));

        Assert.True(info.IsValid);
        Assert.Null(info.Notice);
        Assert.Null(info.NoticeError);
        Assert.False(info.HasNotice);
    }

    [Fact]
    public void A_colour_only_block_is_a_plate_with_its_values()
    {
        var info = LoadLogged(MakeSkin("plate",
            """{"notice":{"background":"#FF102030","border":"#FFFFFFFF","cornerRadius":4,"padding":{"x":10,"y":6},"text":{"color":"#FF00FF00","fontFamily":"Tahoma","fontSize":13,"bold":false},"glyph":{"size":16,"color":"#FFFF0000"}}}"""));

        var n = info.Notice;
        Assert.NotNull(n);
        Assert.True(info.HasNotice);
        Assert.False(n.HasArt);
        Assert.Equal("#FF102030", n.Background);
        Assert.Equal("#FFFFFFFF", n.Border);
        Assert.Equal(4, n.CornerRadius);
        Assert.Equal(10, n.PadX);
        Assert.Equal(6, n.PadY);
        Assert.True(n.Text.Show);
        Assert.Equal("#FF00FF00", n.Text.Color);
        Assert.Equal("Tahoma", n.Text.FontFamily);
        Assert.Equal(13, n.Text.FontSize);
        Assert.False(n.Text.Bold);
        Assert.Equal(16, n.Glyph.Size);
        Assert.Equal("#FFFF0000", n.Glyph.Color);
    }

    [Fact]
    public void Plate_values_are_clamped()
    {
        var high = LoadLogged(MakeSkin("high",
            """{"notice":{"cornerRadius":500,"padding":{"x":999},"text":{"fontSize":999},"glyph":{"size":0}}}""")).Notice!;
        var low = LoadLogged(MakeSkin("low", """{"notice":{"cornerRadius":-3}}""")).Notice!;

        Assert.Equal(64, high.CornerRadius);
        Assert.Equal(64, high.PadX);
        Assert.Equal(200, high.Text.FontSize);
        Assert.Equal(4, high.Glyph.Size);
        Assert.Equal(0, low.CornerRadius);
    }

    [Fact]
    public void Notice_art_alone_gets_defaults_placed_inside_it()
    {
        var info = LoadLogged(MakeSkin("art", null, (320, 56)));

        var n = info.Notice;
        Assert.NotNull(n);
        Assert.True(n.HasArt);
        Assert.Equal(320, n.Width);
        Assert.Equal(56, n.Height);
        Assert.Equal(48, n.Text.X);
        Assert.Equal(320 - 48 - 12, n.TextWidth);
        Assert.Equal(16, n.Glyph.X);
        Assert.Equal((56 - 18) / 2, n.Glyph.Y);
        Assert.Equal((56 - (int)Math.Round(14 * 1.33)) / 2, n.Text.Y);
    }

    [Fact]
    public void Art_block_positions_are_read_and_clamped()
    {
        var info = LoadLogged(MakeSkin("pos",
            """{"notice":{"text":{"x":50,"y":20},"textWidth":900,"glyph":{"x":400}}}""", (300, 60)));

        var n = info.Notice!;
        Assert.Equal(50, n.Text.X);
        Assert.Equal(20, n.Text.Y);
        Assert.Equal(300 - 50, n.TextWidth);
        Assert.Equal(300, n.Glyph.X);
    }

    [Fact]
    public void A_sprite_sheet_notice_uses_one_frame_height()
    {
        var info = LoadLogged(MakeSkin("sheet", """{"notice":{"frames":3}}""", (300, 50), noticeFrames: 3));

        var n = info.Notice!;
        Assert.Equal(50, n.Height);
        Assert.Equal(3, n.ArtFrames);
    }

    [Fact]
    public void A_broken_notice_image_costs_only_the_notice()
    {
        var folder = MakeSkin("broken", null);
        File.WriteAllText(Path.Combine(folder, "notice.png"), "not a png");

        var info = LoadLogged(folder);

        Assert.True(info.IsValid);
        Assert.Null(info.Notice);
        Assert.Contains("notice.png", info.NoticeError);
    }

    [Fact]
    public void A_notice_block_of_the_wrong_type_is_ignored()
    {
        var info = LoadLogged(MakeSkin("wrongtype", """{"notice": 42}"""));

        Assert.True(info.IsValid);
        Assert.Null(info.Notice);
        Assert.Null(info.NoticeError);
    }

    [Fact]
    public void Frames_that_do_not_divide_the_art_are_reported()
    {
        var info = LoadLogged(MakeSkin("indivisible", """{"notice":{"frames":3}}""", (300, 50)));

        Assert.True(info.IsValid);
        Assert.Null(info.Notice);
        Assert.Contains("not divisible", info.NoticeError);
    }
}
