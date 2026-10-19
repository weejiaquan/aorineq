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
            // The ink, not the text's box: the box is the star column whatever is drawn in it.
            var glyphs = Notice(osd).NoticeText.Glyphs;
            double right = glyphs.TranslatePoint(new System.Windows.Point(glyphs.Data.Bounds.Right, 0), plate).X;
            return (narrow, osd.Width, right, plate.ActualWidth, SystemParameters.WorkArea,
                new Rect(osd.Left, osd.Top, osd.Width, osd.Height));
        });

        _out.WriteLine($"short name: {shortWidth} wide; {name.Length} full-width characters: {wideWidth} wide, " +
                       $"ink ends at {textRight:0.#} of a {plateWidth:0.#} plate; window {window} in {workArea}");
        Assert.Equal(300, shortWidth); // a short name gets the volume bar's own width, not a sliver
        Assert.True(wideWidth > shortWidth, "A long name did not widen the plate at all.");
        Assert.True(wideWidth <= 480, $"The plate grew to {wideWidth}, past its 480 cap.");
        Assert.True(textRight <= plateWidth - 16 + 0.5,
            $"The name's ink runs to {textRight:0.#}, past the plate's padding at {plateWidth - 16:0.#}.");
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
                Text: ColorOf(view.NoticeText.Glyphs.Fill));

            osd.ShowNotice("Speakers", light: false);
            osd.UpdateLayout();
            var d = (Back: ColorOf(view.PlateBorder.Background), Border: ColorOf(view.PlateBorder.BorderBrush),
                Text: ColorOf(view.NoticeText.Glyphs.Fill));
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
        var (corner, drawn, at12, at14, glyphSize) = With(osd =>
        {
            osd.ApplyConfig(Settings.Default with { OsdStyle = OsdStyles.MinimalBar });
            osd.ShowNotice("Speakers");
            osd.UpdateLayout();
            var view = Notice(osd);
            double dip = System.Windows.Media.VisualTreeHelper.GetDpi(view).PixelsPerDip;
            string family = view.NoticeText.FontFamily.Source;
            return (view.PlateBorder.CornerRadius.TopLeft, view.NoticeText.Glyphs.Data.Bounds.Width,
                TextInk.Width(family, 12, FontWeights.SemiBold, FontStyles.Normal, "Speakers", dip),
                TextInk.Width(family, 14, FontWeights.SemiBold, FontStyles.Normal, "Speakers", dip),
                view.NoticeGlyph.FontSize);
        });

        _out.WriteLine($"minimal-bar notice: corner {corner}, text ink {drawn:0.###} (12 px would be {at12:0.###}, 14 px {at14:0.###}), glyph {glyphSize}");
        Assert.Equal(8, corner);
        Assert.Equal(at12, drawn, 3);
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

    /// <summary>Where a short name's ink sits in the view: its box's left and right, and the ink's.</summary>
    private static (double BoxLeft, double BoxRight, double InkLeft, double InkRight) InkInBox(OsdWindow osd)
    {
        var view = Notice(osd);
        var text = view.NoticeText;
        var ink = text.Glyphs.Data.Bounds;
        double boxLeft = text.TranslatePoint(new System.Windows.Point(0, 0), view).X;
        return (boxLeft, boxLeft + text.ActualWidth,
            text.Glyphs.TranslatePoint(new System.Windows.Point(ink.Left, 0), view).X,
            text.Glyphs.TranslatePoint(new System.Windows.Point(ink.Right, 0), view).X);
    }

    /// <summary>On art the name is aligned inside its textWidth box by its align, as the
    /// TextBlock's TextAlignment did. A glyph's side bearing keeps ink a pixel or two off the
    /// advance edge, hence the tolerance.</summary>
    [Theory]
    [InlineData("left")]
    [InlineData("center")]
    [InlineData("right")]
    public void A_name_on_art_is_aligned_inside_its_box(string align)
    {
        var folder = TempFolder();
        try
        {
            var art = LoadArtSkin(folder, 320, 56).Notice!;
            var look = art with { Text = art.Text with { Align = align } };
            var r = With(osd =>
            {
                osd.ApplyConfig(Settings.Default with { OsdStyle = OsdStyles.Skin });
                osd.SetSkinNotice(look, 1.0);
                osd.ShowNotice("Hi");
                osd.UpdateLayout();
                return InkInBox(osd);
            });
            double centre = (r.BoxLeft + r.BoxRight) / 2, inkCentre = (r.InkLeft + r.InkRight) / 2;
            _out.WriteLine($"align {align}: box {r.BoxLeft:0.##}..{r.BoxRight:0.##} (centre {centre:0.##}), ink {r.InkLeft:0.##}..{r.InkRight:0.##} (centre {inkCentre:0.##})");
            Assert.True(r.BoxRight - r.BoxLeft > 100, "the box is too narrow for the test to tell the aligns apart");
            switch (align)
            {
                case "left": Assert.InRange(r.InkLeft, r.BoxLeft, r.BoxLeft + 3); break;
                case "right": Assert.InRange(r.InkRight, r.BoxRight - 3, r.BoxRight); break;
                default: Assert.InRange(inkCentre, centre - 2, centre + 2); break;
            }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    /// <summary>A plate lays the name out by its padding, so a skin's align has no effect there:
    /// the name starts at the left of its column, as it always has.</summary>
    [Fact]
    public void A_plate_name_starts_at_the_left_whatever_its_align()
    {
        var d = SkinLoader.DefaultNotice;
        var r = With(osd =>
        {
            osd.ApplyConfig(Settings.Default with { OsdStyle = OsdStyles.Skin });
            osd.SetSkinNotice(d with { Text = d.Text with { Align = "right" } }, 1.0);
            osd.ShowNotice("Hi");
            osd.UpdateLayout();
            return InkInBox(osd);
        });
        _out.WriteLine($"plate, align right: column {r.BoxLeft:0.##}..{r.BoxRight:0.##}, ink {r.InkLeft:0.##}..{r.InkRight:0.##}");
        Assert.True(r.BoxRight - r.BoxLeft > 100, "the column is too narrow for the test to tell left from right");
        Assert.InRange(r.InkLeft, r.BoxLeft, r.BoxLeft + 3);
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

            var (width, boxRight, inkRight, ink, untrimmed) = With(osd =>
            {
                osd.ApplyConfig(Settings.Default with { OsdStyle = OsdStyles.Skin });
                osd.SetSkinNotice(notice, scale);
                osd.ShowNotice(name);
                osd.UpdateLayout();
                var view = Notice(osd);
                var text = view.NoticeText;
                var bounds = text.Glyphs.Data.Bounds;
                double dip = System.Windows.Media.VisualTreeHelper.GetDpi(view).PixelsPerDip;
                return (text.Width, text.TranslatePoint(new System.Windows.Point(text.ActualWidth, 0), view).X,
                    text.Glyphs.TranslatePoint(new System.Windows.Point(bounds.Right, 0), view).X, bounds.Width,
                    TextInk.Width(notice.Text.FontFamily, notice.Text.FontSize * scale, FontWeight.FromOpenTypeWeight(notice.Text.Weight),
                        FontStyles.Normal, name, dip));
            });

            _out.WriteLine($"{name.Length}-char name: text box {width} (TextWidth {notice.TextWidth} x {scale}) ending at {boxRight:0.#}; " +
                           $"ink {ink:0.#} wide (untrimmed {untrimmed:0.#}) ending at {inkRight:0.#}; art {notice.Width * scale} wide");
            Assert.Equal(notice.TextWidth * scale, width);
            Assert.True(untrimmed > width, "the name is not long enough to need trimming");
            Assert.True(inkRight <= boxRight + 0.5, $"The name's ink runs to {inkRight:0.#}, past its box at {boxRight:0.#}.");
            Assert.True(inkRight <= notice.Width * scale + 0.5,
                $"The name runs to {inkRight:0.#}, past the art's right edge at {notice.Width * scale}.");
            // Trimmed, not cut short: what is left (with its ellipsis) fills the box to within two characters.
            Assert.True(inkRight > boxRight - 2 * notice.Text.FontSize * scale,
                $"The trimmed name ends at {inkRight:0.#}, far short of its box at {boxRight:0.#}.");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void The_dark_pill_notice_is_semibold_and_a_skin_notice_takes_its_weight_and_slant()
    {
        var d = SkinLoader.DefaultNotice;
        var bold = d with { Text = d.Text with { Weight = 700 } };
        var lightItalic = d with { Text = d.Text with { Weight = 300, Italic = true } };

        var r = With(osd =>
        {
            double Drawn() { osd.UpdateLayout(); return Notice(osd).NoticeText.Glyphs.Data.Bounds.Width; }
            osd.ShowNotice("Speakers");
            double pill = Drawn();

            osd.ApplyConfig(Settings.Default with { OsdStyle = OsdStyles.Skin });
            osd.SetSkinNotice(bold, 1.0);
            osd.ShowNotice("Speakers");
            double skinBold = Drawn();
            osd.SetSkinNotice(lightItalic, 1.0);
            osd.ShowNotice("Speakers");
            double skinLightItalic = Drawn();

            double dip = System.Windows.Media.VisualTreeHelper.GetDpi(Notice(osd)).PixelsPerDip;
            string family = Notice(osd).NoticeText.FontFamily.Source;
            double Ink(FontWeight weight, FontStyle style) => TextInk.Width(family, d.Text.FontSize, weight, style, "Speakers", dip);
            return (pill, skinBold, skinLightItalic, SemiBold: Ink(FontWeights.SemiBold, FontStyles.Normal),
                Bold: Ink(FontWeights.Bold, FontStyles.Normal), LightItalic: Ink(FontWeights.Light, FontStyles.Italic),
                Light: Ink(FontWeights.Light, FontStyles.Normal));
        });

        _out.WriteLine($"dark pill: drawn {r.pill:0.###}, SemiBold ink {r.SemiBold:0.###}; weight 700: drawn {r.skinBold:0.###}, " +
                       $"Bold ink {r.Bold:0.###}; 300 italic: drawn {r.skinLightItalic:0.###}, Light italic ink {r.LightItalic:0.###} " +
                       $"(upright Light {r.Light:0.###})");
        Assert.Equal(r.SemiBold, r.pill, 3);
        Assert.Equal(r.Bold, r.skinBold, 3);
        Assert.Equal(r.LightItalic, r.skinLightItalic, 3);
        Assert.True(Math.Abs(r.LightItalic - r.Light) > 0.01, "italic and upright ink are the same width: the test cannot tell them apart");
    }

    [Fact]
    public void A_skin_notice_draws_its_outline_and_shadow_on_a_plate_and_on_art()
    {
        var d = SkinLoader.DefaultNotice;
        var styled = d.Text with { OutlineColor = "#FFFF0000", OutlineWidth = 2, ShadowColor = "#80000000", ShadowBlur = 6, ShadowDepth = 3 };
        var folder = TempFolder();
        try
        {
            var art = LoadArtSkin(folder, 320, 56).Notice!;
            var r = With(osd =>
            {
                osd.ApplyConfig(Settings.Default with { OsdStyle = OsdStyles.Skin });
                (System.Windows.Media.Color Stroke, double Thickness, double Blur, double Depth) Look(SkinNotice look, double scale)
                {
                    osd.SetSkinNotice(look, scale);
                    osd.ShowNotice("Speakers");
                    osd.UpdateLayout();
                    var glyphs = Notice(osd).NoticeText.Glyphs;
                    var shadow = Assert.IsType<System.Windows.Media.Effects.DropShadowEffect>(glyphs.Effect);
                    return (ColorOf(glyphs.Stroke), glyphs.StrokeThickness, shadow.BlurRadius, shadow.ShadowDepth);
                }
                var plate = Look(d with { Text = styled }, 1.0);
                var onArt = Look(art with { Text = styled with { X = art.Text.X, Y = art.Text.Y } }, 2.0);
                osd.SetSkinNotice(d, 1.0);
                osd.ShowNotice("Speakers");
                osd.UpdateLayout();
                var plain = Notice(osd).NoticeText.Glyphs;
                return (plate, onArt, PlainStroke: plain.Stroke, PlainEffect: plain.Effect);
            });

            _out.WriteLine($"plate: {r.plate}; art at scale 2: {r.onArt}; plain: stroke {r.PlainStroke?.ToString() ?? "none"}, effect {r.PlainEffect?.ToString() ?? "none"}");
            Assert.Equal((Parse("#FFFF0000"), 2.0, 6.0, 3.0), r.plate);
            Assert.Equal((Parse("#FFFF0000"), 4.0, 12.0, 6.0), r.onArt); // art sizes scale with the skin
            Assert.Null(r.PlainStroke);
            Assert.Null(r.PlainEffect);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void A_cjk_name_draws_through_the_app_font_chain_and_spacing_widens_it()
    {
        const string name = "扬声器 (USB)";
        var d = SkinLoader.DefaultNotice;
        var r = _sta.Run(() =>
        {
            // The chain App.xaml ships: Segoe UI has no Han glyphs, so only the fallbacks after it can draw them.
            var chain = new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI, Microsoft JhengHei UI, Yu Gothic UI, Malgun Gothic, Segoe UI Symbol");
            Application.Current.Resources["AppFontFamily"] = chain;
            var osd = new OsdWindow();
            osd.ApplyConfig(Settings.Default with { OsdStyle = OsdStyles.Skin });
            try
            {
                double Drawn(SkinNotice look)
                {
                    osd.SetSkinNotice(look, 1.0);
                    osd.ShowNotice(name);
                    osd.UpdateLayout();
                    return Notice(osd).NoticeText.Glyphs.Data.Bounds.Width;
                }
                double plain = Drawn(d);
                var family = Notice(osd).NoticeText.FontFamily;
                double spaced = Drawn(d with { Text = d.Text with { LetterSpacing = 3 } });
                double dip = System.Windows.Media.VisualTreeHelper.GetDpi(Notice(osd)).PixelsPerDip;
                return (plain, spaced, SameChain: ReferenceEquals(chain, family),
                    ChainInk: TextInk.Width(chain.Source, d.Text.FontSize, FontWeights.SemiBold, FontStyles.Normal, name, dip),
                    Elements: SkinMath.TextElements(name).Count);
            }
            finally { osd.Hide(); }
        });

        _out.WriteLine($"'{name}': drawn {r.plain:0.###} through the chain (ink {r.ChainInk:0.###}, same family object {r.SameChain}); " +
                       $"spaced 3: {r.spaced:0.###} over {r.Elements} elements (expected about {r.plain + 3 * (r.Elements - 1):0.###})");
        Assert.True(r.SameChain, "the notice name did not inherit the app's font chain");
        Assert.Equal(r.ChainInk, r.plain, 3);
        // Laid out element by element the kerning between them is gone, so allow a pixel.
        Assert.InRange(r.spaced, r.plain + 3 * (r.Elements - 1) - 1, r.plain + 3 * (r.Elements - 1) + 1);
    }

    [Fact]
    public void The_default_family_inherits_the_app_font_chain_and_a_skin_family_is_its_own()
    {
        var tahoma = SkinLoader.DefaultNotice with { Text = SkinLoader.DefaultNotice.Text with { FontFamily = "Tahoma" } };

        var (appFamily, pillFamily, skinFamily) = With(osd =>
        {
            var app = (System.Windows.Media.FontFamily)Application.Current.Resources["AppFontFamily"];
            osd.ShowNotice("Speakers");
            var p = Notice(osd).NoticeText.FontFamily;

            osd.ApplyConfig(Settings.Default with { OsdStyle = OsdStyles.Skin });
            osd.SetSkinNotice(tahoma, 1.0);
            osd.ShowNotice("Speakers");
            var s = Notice(osd).NoticeText.FontFamily;

            // And back: a later default look must not keep the skin's family.
            osd.SetSkinNotice(null, 1.0);
            osd.ShowNotice("Speakers");
            Assert.Same(app, Notice(osd).NoticeText.FontFamily);
            return (app, p, s);
        });

        _out.WriteLine($"AppFontFamily {appFamily.Source}; dark-pill {pillFamily.Source}; skin {skinFamily.Source}");
        // Same instance, not just the same name: the fixture's resource is "Segoe UI" too, and only
        // inheritance hands back the resource object itself.
        Assert.Same(appFamily, pillFamily);
        Assert.Equal("Tahoma", skinFamily.Source);
    }

    [Fact]
    public void An_unparseable_glyph_colour_takes_the_notices_own_text_colour()
    {
        var d = SkinLoader.DefaultNotice;
        var look = d with
        {
            Text = d.Text with { Color = "#FF00FF00" },
            Glyph = d.Glyph with { Color = "not-a-colour" },
        };

        var (text, glyph) = With(osd =>
        {
            osd.ApplyConfig(Settings.Default with { OsdStyle = OsdStyles.Skin });
            osd.SetSkinNotice(look, 1.0);
            osd.ShowNotice("Speakers");
            var view = Notice(osd);
            return (ColorOf(view.NoticeText.Glyphs.Fill), ColorOf(view.NoticeGlyph.Foreground));
        });

        _out.WriteLine($"text {text}, glyph {glyph}");
        Assert.Equal(Parse("#FF00FF00"), text);
        Assert.Equal(text, glyph);
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
                ColorOf(view.NoticeText.Glyphs.Fill), ColorOf(view.NoticeGlyph.Foreground));
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

    /// <summary>App owns its OSD's lifetime, so Close only hides it and the window can be shown
    /// again; CloseForGood (the designer's notice preview, at its Dispose) really closes it, its
    /// hide timer stopped.</summary>
    [Fact]
    public void Close_only_hides_the_osd_and_close_for_good_really_closes_it()
    {
        var r = With(osd =>
        {
            int closed = 0;
            osd.Closed += (_, _) => closed++;
            osd.ShowNotice("Speakers", light: false);
            osd.Close();
            var afterClose = (osd.IsVisible, Closed: closed);
            osd.ShowNotice("Speakers", light: false);
            var reshown = (osd.IsVisible, Timer: osd.HideTimerRunning);
            osd.CloseForGood();
            return (afterClose, reshown, AfterCloseForGood: (osd.IsVisible, Closed: closed, Timer: osd.HideTimerRunning));
        });
        _out.WriteLine("expected: Close hides without closing; the notice shows again with its hide timer running; " +
                       $"CloseForGood closes once with the timer stopped; actual {r}");
        Assert.Equal((false, 0), r.afterClose);
        Assert.Equal((true, true), r.reshown);
        Assert.Equal((false, 1, false), r.AfterCloseForGood);
    }

    /// <summary>HideNow takes a shown notice down at once and stops its hide timer, so nothing
    /// ticks for a window that is already hidden (the designer's notice preview between uses).</summary>
    [Fact]
    public void Hide_now_hides_the_notice_and_stops_its_timer()
    {
        var r = With(osd =>
        {
            osd.ShowNotice("Speakers", light: false);
            var shown = (osd.IsVisible, Timer: osd.HideTimerRunning);
            osd.HideNow();
            return (shown, Hidden: (osd.IsVisible, Timer: osd.HideTimerRunning));
        });
        _out.WriteLine($"expected shown with the timer running, then hidden with it stopped; actual {r}");
        Assert.Equal((true, true), r.shown);
        Assert.Equal((false, false), r.Hidden);
    }
}

/// <summary>One STA thread and one <see cref="Application"/> for every test class that builds WPF
/// controls. The Application can only be constructed once per process and belongs to the thread
/// that made it, so a second class with a StaWpf of its own would be handed controls styled from
/// another thread's resources.</summary>
[CollectionDefinition("Wpf")]
public class WpfCollection : ICollectionFixture<StaWpf>;
