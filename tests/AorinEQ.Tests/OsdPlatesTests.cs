using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

public class OsdPlatesTests(ITestOutputHelper output)
{
    private static readonly SkinNotice CustomSkinNotice =
        SkinLoader.DefaultNotice with { Background = "#FF123456", CornerRadius = 3 };

    [Theory]
    [InlineData("dark-pill", true)]
    [InlineData("dark-pill", false)]
    [InlineData("minimal-bar", true)]
    [InlineData("minimal-bar", false)]
    [InlineData("fluent", true)]
    [InlineData("fluent", false)]
    [InlineData("skin", true)]
    [InlineData("skin", false)]
    [InlineData("garbage", false)]
    public void Each_style_resolves_to_its_own_plate(string style, bool light)
    {
        var expected = style switch
        {
            "minimal-bar" => OsdPlates.MinimalBar,
            "fluent" => light ? OsdPlates.FluentLight : OsdPlates.FluentDark,
            "skin" => CustomSkinNotice,
            _ => OsdPlates.DarkPill,
        };
        var actual = OsdPlates.NoticeFor(style, light, CustomSkinNotice);
        output.WriteLine($"style={style} light={light} -> bg={actual.Background} radius={actual.CornerRadius} text={actual.Text.FontSize}");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Dark_pill_is_the_loader_default()
    {
        output.WriteLine($"DarkPill={OsdPlates.DarkPill}");
        Assert.Equal(SkinLoader.DefaultNotice, OsdPlates.DarkPill);
    }

    [Fact]
    public void Minimal_bar_has_its_own_measurements()
    {
        var p = OsdPlates.MinimalBar;
        output.WriteLine($"{p}");
        Assert.Equal("#F2202020", p.Background);
        Assert.Equal(8, p.CornerRadius);
        Assert.Equal(10, p.PadX);
        Assert.Equal(4, p.PadY);
        Assert.Equal(12, p.Text.FontSize);
        Assert.True(p.Text.Bold);
        Assert.Equal(14, p.Glyph.Size);
    }

    [Fact]
    public void Fluent_follows_light_and_dark()
    {
        var light = OsdPlates.NoticeFor("fluent", true, null);
        var dark = OsdPlates.NoticeFor("fluent", false, null);
        output.WriteLine($"light bg={light.Background} border={light.Border} text={light.Text.Color}");
        output.WriteLine($"dark  bg={dark.Background} border={dark.Border} text={dark.Text.Color}");
        Assert.Equal(("#F2F3F3F3", "#14000000", "#FF000000", 8.0), (light.Background, light.Border, light.Text.Color, light.CornerRadius));
        Assert.Equal(("#F2202020", "#26FFFFFF", "#FFFFFFFF", 8.0), (dark.Background, dark.Border, dark.Text.Color, dark.CornerRadius));
    }

    [Fact]
    public void A_skin_without_a_notice_falls_back_to_the_dark_pill()
    {
        var plate = OsdPlates.NoticeFor("skin", false, null);
        output.WriteLine($"plate={plate}");
        Assert.Equal(OsdPlates.DarkPill, plate);
    }

    [Fact]
    public void A_skin_notice_wins_only_in_skin_style()
    {
        var plate = OsdPlates.NoticeFor("fluent", true, CustomSkinNotice);
        output.WriteLine($"fluent with skin notice -> {plate.Background}");
        Assert.Equal(OsdPlates.FluentLight, plate);
        Assert.Equal(OsdPlates.MinimalBar, OsdPlates.NoticeFor("minimal-bar", false, CustomSkinNotice));
        Assert.Equal(OsdPlates.DarkPill, OsdPlates.NoticeFor("dark-pill", false, CustomSkinNotice));
    }

    [Theory]
    [InlineData("DarkPill")]
    [InlineData("MinimalBar")]
    [InlineData("FluentLight")]
    [InlineData("FluentDark")]
    public void Every_plate_reads_on_its_own_background(string name)
    {
        var plate = name switch
        {
            "DarkPill" => OsdPlates.DarkPill,
            "MinimalBar" => OsdPlates.MinimalBar,
            "FluentLight" => OsdPlates.FluentLight,
            _ => OsdPlates.FluentDark,
        };
        double ratio = Contrast(plate.Text.Color, plate.Background);
        output.WriteLine($"{name}: text={plate.Text.Color} bg={plate.Background} contrast={ratio:F2}");
        Assert.True(ratio >= 4.5, $"{name} contrast {ratio:F2} < 4.5");
    }

    // WCAG contrast ratio of two #AARRGGBB colours; alpha is ignored (plates are ~95% opaque).
    private static double Contrast(string fg, string bg)
    {
        double a = Luminance(bg), b = Luminance(fg);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(string argb)
    {
        uint v = Convert.ToUInt32(argb.TrimStart('#'), 16);
        double Lin(uint c) { double s = c / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Lin((v >> 16) & 0xFF) + 0.7152 * Lin((v >> 8) & 0xFF) + 0.0722 * Lin(v & 0xFF);
    }
}
