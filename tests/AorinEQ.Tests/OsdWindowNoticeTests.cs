using System.IO;
using System.Windows;
using System.Windows.Controls;
using AorinEQ.Core;
using AorinEQ.UI;
using Application = System.Windows.Application;

namespace AorinEQ.Tests;

/// <summary>The device notice is drawn by the real <see cref="OsdWindow"/>, so these build one and
/// measure it. What a name is cut to is DeviceNoticeTests' business; what is pinned here is the
/// part only a laid-out window can show - that the notice sits where the volume bar sits, that a
/// wide name stays on its plate, and that the bar comes back afterwards.
///
/// The window really is shown, for as long as each test takes, wherever the OSD would appear.</summary>
[Collection("Wpf")]
public class OsdWindowNoticeTests
{
    private readonly StaWpf _sta;
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public OsdWindowNoticeTests(StaWpf sta, Xunit.Abstractions.ITestOutputHelper output)
    {
        _sta = sta;
        _out = output;
    }

    /// <summary>Runs <paramref name="body"/> against a configured OSD on the STA thread. The
    /// window is hidden rather than closed afterwards: OsdWindow cancels Close by design.</summary>
    private T With<T>(Func<OsdWindow, T> body) =>
        _sta.Run(() =>
        {
            // OsdWindow's XAML resolves this from App.xaml; the fixture's Application is not App.
            Application.Current.Resources["AppFontFamily"] = new System.Windows.Media.FontFamily("Segoe UI");
            var osd = new OsdWindow();
            osd.ApplyConfig(Settings.Default);
            try { return body(osd); }
            finally { osd.Hide(); }
        });

    private static FrameworkElement Part(OsdWindow osd, string name) =>
        (FrameworkElement)osd.FindName(name);

    private static NoticeView Notice(OsdWindow osd) => (NoticeView)osd.FindName("NoticeRoot");

    private static System.Windows.Media.Color ColorOf(System.Windows.Media.Brush? brush) =>
        Assert.IsType<System.Windows.Media.SolidColorBrush>(brush).Color;

    private static System.Windows.Media.Color Parse(string hex) =>
        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);

    private static void ShowVolume(OsdWindow osd)
    {
        osd.SetAirPlay(AirPlayBarState.Hidden, skin: null, scale: 1.0);
        osd.ShowVolume(50, muted: false, interactive: false);
        osd.UpdateLayout();
    }

    [Fact]
    public void The_notice_appears_where_the_volume_bar_does()
    {
        var (bar, notice, visible) = With(osd =>
        {
            ShowVolume(osd);
            var barRect = new Rect(osd.Left, osd.Top, osd.Width, osd.Height);

            osd.ShowNotice("Speakers");
            osd.UpdateLayout();
            return (barRect, new Rect(osd.Left, osd.Top, osd.Width, osd.Height), osd.IsVisible);
        });

        _out.WriteLine($"volume bar {bar}, notice {notice}");
        Assert.True(visible, "ShowNotice did not show the window.");
        Assert.Equal(bar, notice);
    }

    [Fact]
    public void The_notice_replaces_the_volume_bar_rather_than_joining_it()
    {
        var (styles, strip, plate, text) = With(osd =>
        {
            ShowVolume(osd);
            osd.ShowNotice("Headphones (USB DAC)");
            osd.UpdateLayout();
            return (Part(osd, "StyleHost").Visibility, Part(osd, "AirPlayBar").Visibility,
                Part(osd, "NoticeRoot").Visibility, Notice(osd).NoticeText.Text);
        });

        Assert.Equal(Visibility.Collapsed, styles);
        Assert.Equal(Visibility.Collapsed, strip);
        Assert.Equal(Visibility.Visible, plate);
        Assert.Equal("Headphones (USB DAC)", text);
    }

    [Fact]
    public void The_next_volume_change_puts_the_bar_back()
    {
        var (styles, plate) = With(osd =>
        {
            osd.ShowNotice("Speakers");
            ShowVolume(osd);
            return (Part(osd, "StyleHost").Visibility, Part(osd, "NoticeRoot").Visibility);
        });

        Assert.Equal(Visibility.Visible, styles);
        Assert.Equal(Visibility.Collapsed, plate);
    }

    [Fact]
    public void A_name_too_wide_for_the_plate_stays_inside_it()
    {
        // As long as DeviceNotice lets a name be, in full-width characters: few enough to be left
        // whole, and still half as wide again as the plate is allowed to get.
        var name = new string('音', DeviceNotice.MaxNameLength);

        var (shortWidth, wideWidth, textRight, plateWidth, workArea, window) = With(osd =>
        {
            osd.ShowNotice("Speakers");
            osd.UpdateLayout();
            double narrow = osd.Width;

            osd.ShowNotice(name);
            osd.UpdateLayout();
            var plate = Part(osd, "NoticeRoot");
            var text = Notice(osd).NoticeText;
            double right = text.TranslatePoint(new System.Windows.Point(text.ActualWidth, 0), plate).X;
            return (narrow, osd.Width, right, plate.ActualWidth, SystemParameters.WorkArea,
                new Rect(osd.Left, osd.Top, osd.Width, osd.Height));
        });

        _out.WriteLine($"short name: {shortWidth} wide; {name.Length} full-width characters: {wideWidth} wide, " +
                       $"text ends at {textRight:0.#} of a {plateWidth:0.#} plate; window {window} in {workArea}");
        Assert.Equal(300, shortWidth); // a short name gets the volume bar's own width, not a sliver
        Assert.True(wideWidth > shortWidth, "A long name did not widen the plate at all.");
        Assert.True(wideWidth <= 480, $"The plate grew to {wideWidth}, past its 480 cap.");
        Assert.True(textRight <= plateWidth - 16 + 0.5,
            $"The name runs to {textRight:0.#}, past the plate's padding at {plateWidth - 16:0.#}.");
        Assert.True(workArea.Contains(window), $"The notice {window} is not inside the work area {workArea}.");
    }

    [Fact]
    public void Fluent_light_and_dark_notices_use_the_fluent_plate()
    {
        var (light, dark) = With(osd =>
        {
            osd.ApplyConfig(Settings.Default with { OsdStyle = OsdStyles.Fluent });
            var view = Notice(osd);

            osd.ShowNotice("Speakers", light: true);
            osd.UpdateLayout();
            var l = (Back: ColorOf(view.PlateBorder.Background), Border: ColorOf(view.PlateBorder.BorderBrush),
                Text: ColorOf(view.NoticeText.Foreground));

            osd.ShowNotice("Speakers", light: false);
            osd.UpdateLayout();
            var d = (Back: ColorOf(view.PlateBorder.Background), Border: ColorOf(view.PlateBorder.BorderBrush),
                Text: ColorOf(view.NoticeText.Foreground));
            return (l, d);
        });

        _out.WriteLine($"light: back {light.Back} border {light.Border} text {light.Text}");
        _out.WriteLine($"dark:  back {dark.Back} border {dark.Border} text {dark.Text}");
        Assert.Equal(Parse(OsdPlates.FluentLight.Background), light.Back);
        Assert.Equal(Parse(OsdPlates.FluentLight.Border!), light.Border);
        Assert.Equal(Parse(OsdPlates.FluentLight.Text.Color), light.Text);
        Assert.Equal(Parse(OsdPlates.FluentDark.Background), dark.Back);
        Assert.Equal(Parse(OsdPlates.FluentDark.Border!), dark.Border);
        Assert.Equal(Parse(OsdPlates.FluentDark.Text.Color), dark.Text);
    }

    [Fact]
    public void Minimal_bar_notice_uses_its_own_plate()
    {
        var (corner, fontSize, glyphSize) = With(osd =>
        {
            osd.ApplyConfig(Settings.Default with { OsdStyle = OsdStyles.MinimalBar });
            osd.ShowNotice("Speakers");
            osd.UpdateLayout();
            var view = Notice(osd);
            return (view.PlateBorder.CornerRadius.TopLeft, view.NoticeText.FontSize, view.NoticeGlyph.FontSize);
        });

        _out.WriteLine($"minimal-bar notice: corner {corner}, text {fontSize}, glyph {glyphSize}");
        Assert.Equal(8, corner);
        Assert.Equal(12, fontSize);
        Assert.Equal(OsdPlates.MinimalBar.Glyph.Size, glyphSize);
    }

    [Fact]
    public void A_skin_notice_is_used_only_in_skin_style()
    {
        var custom = SkinLoader.DefaultNotice with { Background = "#FF102030" };

        var (pillBack, skinBack) = With(osd =>
        {
            osd.SetSkinNotice(custom, 1.0);

            osd.ShowNotice("Speakers");
            osd.UpdateLayout();
            var pill = ColorOf(Notice(osd).PlateBorder.Background);

            osd.ApplyConfig(Settings.Default with { OsdStyle = OsdStyles.Skin });
            osd.ShowNotice("Speakers");
            osd.UpdateLayout();
            return (pill, ColorOf(Notice(osd).PlateBorder.Background));
        });

        _out.WriteLine($"dark-pill style: {pillBack}; skin style: {skinBack}");
        Assert.Equal(Parse(SkinLoader.DefaultNotice.Background), pillBack);
        Assert.Equal(Parse(custom.Background), skinBack);
    }

    /// <summary>A temp skin with notice art, loaded by the real loader. The caller deletes the
    /// folder.</summary>
    private SkinInfo LoadArtSkin(string folder, int artWidth, int artHeight)
    {
        Directory.CreateDirectory(folder);
        TestPngs.Write(Path.Combine(folder, "empty.png"), 200, 40);
        TestPngs.Write(Path.Combine(folder, "full.png"), 200, 40);
        TestPngs.Write(Path.Combine(folder, "notice.png"), artWidth, artHeight);
        var info = SkinLoader.Load(folder);
        _out.WriteLine($"skin valid={info.IsValid} notice={info.Notice} noticeError={info.NoticeError ?? "(none)"}");
        Assert.True(info.Notice is { HasArt: true }, "The test skin's notice art did not load.");
        return info;
    }

    private static string TempFolder() =>
        Path.Combine(Path.GetTempPath(), "aorineq-osd-notice-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void An_art_notice_takes_the_art_size_times_the_skin_scale()
    {
        var folder = TempFolder();
        try
        {
            var info = LoadArtSkin(folder, 320, 56);

            var (width, height, plate) = With(osd =>
            {
                osd.ApplyConfig(Settings.Default with { OsdStyle = OsdStyles.Skin });
                osd.SetSkinNotice(info.Notice, 1.5);
                osd.ShowNotice("Speakers");
                osd.UpdateLayout();
                return (osd.Width, osd.Height, Notice(osd).PlateBorder.Visibility);
            });

            _out.WriteLine($"art notice window {width}x{height}, plate {plate}");
            Assert.Equal(480, width);
            Assert.Equal(84, height);
            Assert.Equal(Visibility.Collapsed, plate);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void A_long_name_on_art_is_trimmed_inside_its_text_box()
    {
        const double scale = 1.5;
        var folder = TempFolder();
        try
        {
            var notice = LoadArtSkin(folder, 320, 56).Notice!;
            var name = new string('W', DeviceNotice.MaxNameLength);

            var (trimming, width, right) = With(osd =>
            {
                osd.ApplyConfig(Settings.Default with { OsdStyle = OsdStyles.Skin });
                osd.SetSkinNotice(notice, scale);
                osd.ShowNotice(name);
                osd.UpdateLayout();
                var view = Notice(osd);
                var text = view.NoticeText;
                double r = text.TranslatePoint(new System.Windows.Point(text.ActualWidth, 0), view).X;
                return (text.TextTrimming, text.Width, r);
            });

            _out.WriteLine($"{name.Length}-char name: trimming {trimming}, text box {width} " +
                           $"(TextWidth {notice.TextWidth} x {scale}), right edge {right:0.#} of {notice.Width * scale}");
            Assert.Equal(TextTrimming.CharacterEllipsis, trimming);
            Assert.Equal(notice.TextWidth * scale, width);
            Assert.True(right <= notice.Width * scale + 0.5,
                $"The name runs to {right:0.#}, past the art's right edge at {notice.Width * scale}.");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Unparseable_skin_colours_fall_back_rather_than_throw()
    {
        var d = SkinLoader.DefaultNotice;
        var broken = d with
        {
            Background = "teal-ish",
            Border = "#nothex",
            Text = d.Text with { Color = "" },
            Glyph = d.Glyph with { Color = "rgb(1,2,3)" },
        };

        var (visible, back, borderThickness, text, glyph) = With(osd =>
        {
            osd.ApplyConfig(Settings.Default with { OsdStyle = OsdStyles.Skin });
            osd.SetSkinNotice(broken, 1.0);
            osd.ShowNotice("Speakers");
            osd.UpdateLayout();
            var view = Notice(osd);
            return (osd.IsVisible, ColorOf(view.PlateBorder.Background), view.PlateBorder.BorderThickness.Left,
                ColorOf(view.NoticeText.Foreground), ColorOf(view.NoticeGlyph.Foreground));
        });

        _out.WriteLine($"visible {visible}, back {back}, border {borderThickness}, text {text}, glyph {glyph}");
        Assert.True(visible, "A notice with unparseable colours was not shown.");
        Assert.Equal(Parse(d.Background), back);
        Assert.Equal(0, borderThickness); // the default plate has no border to fall back to
        Assert.Equal(Parse(d.Text.Color), text);
        Assert.Equal(Parse(d.Text.Color), glyph);
    }

    [Fact]
    public void Notice_art_that_will_not_decode_falls_back_to_the_default_plate()
    {
        var folder = TempFolder();
        try
        {
            var info = LoadArtSkin(folder, 320, 56);
            // Passed the loader's header check; now the image body is gone, so decoding fails.
            var art = Path.Combine(folder, "notice.png");
            File.WriteAllBytes(art, File.ReadAllBytes(art).Take(40).ToArray());

            var (width, height, plate, back) = With(osd =>
            {
                osd.ApplyConfig(Settings.Default with { OsdStyle = OsdStyles.Skin });
                osd.SetSkinNotice(info.Notice, 1.5);
                osd.ShowNotice("Speakers");
                osd.UpdateLayout();
                var view = Notice(osd);
                return (osd.Width, osd.Height, view.PlateBorder.Visibility, ColorOf(view.PlateBorder.Background));
            });

            _out.WriteLine($"undecodable art: window {width}x{height}, plate {plate}, back {back}");
            Assert.Equal(Visibility.Visible, plate);
            Assert.Equal(Parse(SkinLoader.DefaultNotice.Background), back);
            Assert.Equal(300, width);
            Assert.Equal(64, height);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}

/// <summary>One STA thread and one <see cref="Application"/> for every test class that builds WPF
/// controls. The Application can only be constructed once per process and belongs to the thread
/// that made it, so a second class with a StaWpf of its own would be handed controls styled from
/// another thread's resources.</summary>
[CollectionDefinition("Wpf")]
public class WpfCollection : ICollectionFixture<StaWpf>;
