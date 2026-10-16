using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AorinEQ.Core;
using AorinEQ.UI;
using Xunit.Abstractions;
using Application = System.Windows.Application;

namespace AorinEQ.Tests;

/// <summary>The Skin Designer's notice section: what a loaded skin's notice turns into in the
/// editor, and what the editor hands SkinWriter back. The window is built on the STA thread and
/// never shown; every skin is a real folder of real, decodable PNGs in a temp directory.</summary>
[Collection("Wpf")]
public class SkinDesignerNoticeTests : IDisposable
{
    private readonly StaWpf _sta;
    private readonly ITestOutputHelper _out;
    private readonly string _root;

    public SkinDesignerNoticeTests(StaWpf sta, ITestOutputHelper output)
    {
        _sta = sta;
        _out = output;
        _root = Path.Combine(Path.GetTempPath(), "aorineq-designer-notice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>A real, decodable PNG: the designer decodes the volume layers and the notice art
    /// for its previews, so header-only test PNGs would only exercise its error paths.</summary>
    private static void WritePng(string path, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0x40; pixels[i + 1] = 0x80; pixels[i + 2] = 0xC0; pixels[i + 3] = 0xFF;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    private string MakeSkin(string name, string json, (int W, int H)? noticeArt = null)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        WritePng(Path.Combine(folder, "empty.png"), 200, 40);
        WritePng(Path.Combine(folder, "full.png"), 200, 40);
        if (noticeArt is { } a) WritePng(Path.Combine(folder, "notice.png"), a.W, a.H);
        File.WriteAllText(Path.Combine(folder, "skin.json"), json);
        return folder;
    }

    /// <summary>Builds a designer on the STA thread, runs <paramref name="body"/> on it, and closes
    /// it (the designer cancels Close and hides, which also tears down any test OSD).</summary>
    private T WithDesigner<T>(Func<SkinDesignerWindow, T> body) =>
        _sta.Run(() =>
        {
            // The window's XAML resolves this from App.xaml; the fixture's Application is not App.
            Application.Current.Resources["AppFontFamily"] = new System.Windows.Media.FontFamily("Segoe UI");
            var designer = new SkinDesignerWindow(() => Settings.Default);
            try { return body(designer); }
            finally { designer.Close(); }
        });

    private SkinInfo LoadLogged(string folder)
    {
        var info = SkinLoader.Load(folder);
        _out.WriteLine($"loaded valid={info.IsValid} error={info.Error ?? "(none)"} " +
                       $"notice={info.Notice} noticeError={info.NoticeError ?? "(none)"}");
        Assert.True(info.IsValid, info.Error);
        return info;
    }

    [Fact]
    public void A_skin_with_a_notice_loads_into_the_editor()
    {
        // Every plate field the loader reads, including two the section has no control for
        // (padding, alignment): those must ride through the editor untouched.
        var info = LoadLogged(MakeSkin("plate",
            """{"notice":{"background":"#FF102030","border":"#FFFFFFFF","cornerRadius":4,"padding":{"x":10,"y":6},"text":{"color":"#FF00FF00","fontFamily":"Tahoma","fontSize":13,"bold":true,"align":"center"},"glyph":{"show":false,"size":16,"color":"#FFFF0000"}}}"""));

        var (current, enabled, artPanel) = WithDesigner(d =>
        {
            d.LoadSkin(info);
            return (d.CurrentNotice(), d.NoticeEnableCheck.IsChecked,
                d.NoticeArtPanel.Visibility);
        });

        _out.WriteLine($"editor notice={current} enabled={enabled} artPanel={artPanel}");
        Assert.True(enabled);
        Assert.Equal(System.Windows.Visibility.Collapsed, artPanel);
        Assert.Equal(info.Notice, current);
    }

    [Fact]
    public void A_skin_with_notice_art_loads_into_the_editor()
    {
        var info = LoadLogged(MakeSkin("art",
            """{"notice":{"text":{"x":60,"y":12,"fontSize":15},"textWidth":180,"glyph":{"x":20,"y":10,"size":20}}}""",
            noticeArt: (300, 48)));

        var (current, artPanel, artText) = WithDesigner(d =>
        {
            d.LoadSkin(info);
            return (d.CurrentNotice(), d.NoticeArtPanel.Visibility, d.NoticeArtPathText.ToolTip);
        });

        _out.WriteLine($"editor notice={current} artPanel={artPanel} artPathTooltip={artText}");
        Assert.Equal(System.Windows.Visibility.Visible, artPanel);
        Assert.Equal(info.Notice!.ArtPath, artText);
        Assert.Equal(info.Notice, current);
    }

    [Fact]
    public void A_skin_without_a_notice_starts_with_the_notice_off()
    {
        var info = LoadLogged(MakeSkin("legacy", """{"scale":1.0}"""));

        var (current, enabled, panelEnabled) = WithDesigner(d =>
        {
            d.LoadSkin(info);
            return (d.CurrentNotice(), d.NoticeEnableCheck.IsChecked, d.NoticePanel.IsEnabled);
        });

        _out.WriteLine($"editor notice={current?.ToString() ?? "(null)"} enabled={enabled} panelEnabled={panelEnabled}");
        Assert.False(enabled);
        Assert.False(panelEnabled);
        Assert.Null(current);
    }

    [Fact]
    public void Turning_the_notice_off_saves_none()
    {
        var info = LoadLogged(MakeSkin("plate", """{"notice":{"background":"#FF102030"}}"""));

        var (before, after, panelEnabled) = WithDesigner(d =>
        {
            d.LoadSkin(info);
            var on = d.CurrentNotice();
            d.NoticeEnableCheck.IsChecked = false;
            return (on, d.CurrentNotice(), d.NoticePanel.IsEnabled);
        });

        _out.WriteLine($"before={before} after={after?.ToString() ?? "(null)"} panelEnabled={panelEnabled}");
        Assert.NotNull(before);
        Assert.Null(after);
        Assert.False(panelEnabled);
    }

    [Fact]
    public void Clearing_art_returns_to_a_plate()
    {
        var info = LoadLogged(MakeSkin("plate", """{"notice":{"background":"#FF102030"}}"""));
        var art = Path.Combine(_root, "source-notice.png");
        WritePng(art, 260, 50);

        var (withArt, cleared, artText) = WithDesigner(d =>
        {
            d.LoadSkin(info);
            d.SetNoticeArt(art);
            var placed = d.CurrentNotice();
            d.ClearNoticeArt();
            return (placed, d.CurrentNotice(), d.NoticeArtPathText.Text);
        });

        _out.WriteLine($"with art={withArt}");
        _out.WriteLine($"cleared={cleared} artPathText={artText}");
        Assert.True(withArt!.HasArt);
        Assert.Equal(art, withArt.ArtPath);
        Assert.Equal((260, 50), (withArt.Width, withArt.Height));
        // Fresh art takes the loader's default placement.
        var expected = SkinLoader.PlaceNoticeArt(withArt with { ArtPath = null }, art, false, 1, 260, 50,
            null, null, null, null, null);
        Assert.Equal(expected, withArt);
        Assert.False(cleared!.HasArt);
        Assert.Equal("#FF102030", cleared.Background);
        Assert.Equal(Loc.T("designer.notice.art.none"), artText);
    }

    [Fact]
    public void Out_of_range_values_save_and_load_back_as_the_editor_shows_them()
    {
        // The editor clamps exactly as the loader does: what CurrentNotice reports is what a
        // saved skin reads back, so the preview cannot promise something the skin will not draw.
        var info = LoadLogged(MakeSkin("plate", """{"notice":{"background":"#FF102030"}}"""));
        var art = Path.Combine(_root, "source-notice.png");
        WritePng(art, 240, 40);

        var current = WithDesigner(d =>
        {
            d.LoadSkin(info);
            d.SetNoticeArt(art);
            d.NoticeRadiusBox.Text = "500";
            d.NoticeFontSizeBox.Text = "1";
            d.NoticeGlyphSizeBox.Text = "9000";
            d.NoticeTextXBox.Text = "9999";
            d.NoticeTextYBox.Text = "-5";
            d.NoticeTextWidthBox.Text = "9999";
            d.NoticeGlyphXBox.Text = "-3";
            d.NoticeGlyphYBox.Text = "41";
            return d.CurrentNotice();
        });
        _out.WriteLine($"editor notice={current}");

        var folder = SkinWriter.Save(_root, "roundtrip",
            Path.Combine(info.Folder, "empty.png"), Path.Combine(info.Folder, "full.png"),
            new SkinConfig(null, 1.0, 10, 1, 1, null, null, Notice: current));
        var reloaded = LoadLogged(folder).Notice;
        _out.WriteLine($"reloaded notice={reloaded}");

        Assert.Equal(64, current!.CornerRadius);
        Assert.Equal(4, current.Text.FontSize);
        Assert.Equal(200, current.Glyph.Size);
        Assert.Equal((240, 0, 0), (current.Text.X, current.Text.Y, current.TextWidth));
        Assert.Equal((0, 40), (current.Glyph.X, current.Glyph.Y));
        Assert.Equal(current with { ArtPath = reloaded!.ArtPath }, reloaded);
    }

    [Fact]
    public void The_preview_shows_the_sample_name()
    {
        var info = LoadLogged(MakeSkin("plate", """{"notice":{"background":"#FF102030","cornerRadius":3}}"""));

        var (newSkinText, loadedText, radius) = WithDesigner(d =>
        {
            var fresh = d.NoticePreview.NoticeText.Text;
            d.LoadSkin(info);
            return (fresh, d.NoticePreview.NoticeText.Text, d.NoticePreview.PlateBorder.CornerRadius.TopLeft);
        });

        _out.WriteLine($"new skin preview text='{newSkinText}' loaded='{loadedText}' corner={radius}");
        Assert.Equal(Loc.T("designer.notice.sample"), newSkinText);
        Assert.Equal(Loc.T("designer.notice.sample"), loadedText);
        // Scale 1.0, so the preview's corner is the skin's own.
        Assert.Equal(3, radius);
    }
}
