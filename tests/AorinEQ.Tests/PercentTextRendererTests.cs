using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AorinEQ.Core;
using AorinEQ.UI;
using Xunit.Abstractions;
using Path = System.Windows.Shapes.Path;

namespace AorinEQ.Tests;

/// <summary>The one renderer every skin text goes through: weight, slant, letter spacing and the
/// boxed (trimmed) layout the notice name uses, each checked against FormattedText laid out
/// directly - never against the renderer itself.</summary>
[Collection("Wpf")]
public class PercentTextRendererTests
{
    private readonly StaWpf _sta;
    private readonly ITestOutputHelper _out;

    public PercentTextRendererTests(StaWpf sta, ITestOutputHelper output)
    {
        _sta = sta;
        _out = output;
    }

    private static readonly SkinText Mono = new(true, 0, 0, FontFamily: "Consolas", FontSize: 20);

    private static double Dip() => VisualTreeHelper.GetDpi(new System.Windows.Controls.Border()).PixelsPerDip;

    /// <summary>The advance width of <paramref name="text"/> as one FormattedText - today's
    /// unspaced layout - in the given face.</summary>
    private static double Advance(string text, string family, double size, FontWeight weight, double dip) =>
        new FormattedText(text, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface(new System.Windows.Media.FontFamily(family), FontStyles.Normal, weight, FontStretches.Normal),
            size, Brushes.Black, dip).WidthIncludingTrailingWhitespace;

    [Fact]
    public void Letter_spacing_widens_the_text_by_the_spacing_times_the_gaps()
    {
        const string text = "12345";
        var r = _sta.Run(() =>
        {
            double dip = Dip();
            return (Unspaced: PercentTextRenderer.Measure(Mono, text, dip).Width,
                Today: Advance(text, "Consolas", 20, FontWeight.FromOpenTypeWeight(Mono.Weight), dip),
                At3: PercentTextRenderer.Measure(Mono with { LetterSpacing = 3 }, text, dip).Width,
                At8: PercentTextRenderer.Measure(Mono with { LetterSpacing = 8 }, text, dip).Width,
                Negative: PercentTextRenderer.Measure(Mono with { LetterSpacing = -2 }, text, dip).Width,
                DrawnAt8: PercentTextRenderer.Update(new Path(), Mono with { LetterSpacing = 8 }, text, 1.0, dip));
        });
        _out.WriteLine($"'{text}' (5 elements, 4 gaps): unspaced {r.Unspaced:0.###} (one FormattedText {r.Today:0.###}), " +
                       $"spaced 3 {r.At3:0.###}, spaced 8 {r.At8:0.###} (drawn {r.DrawnAt8:0.###}), spaced -2 {r.Negative:0.###}");
        Assert.Equal(r.Today, r.Unspaced, 6);              // no spacing: today's single layout, exactly
        Assert.Equal(5 * 4, r.At8 - r.At3, 6);             // 5 more pixels in each of the 4 gaps, none after the last
        Assert.Equal(r.Unspaced + 3 * 4, r.At3, 2);        // a monospace face has no kerning to lose
        Assert.Equal(r.Unspaced - 2 * 4, r.Negative, 2);
        Assert.Equal(r.At8, r.DrawnAt8, 6);                // what is measured is what is drawn
    }

    [Theory]
    [InlineData("éx", 2)]                       // a letter with a combining accent is one element
    [InlineData("a\U0001F600b", 3)]                   // an emoji outside the BMP: a surrogate pair, one element
    [InlineData("\U0001F469‍\U0001F4BBz", 2)]    // a ZWJ sequence is one element
    public void Spacing_goes_between_whole_characters_only(string text, int elements)
    {
        var r = _sta.Run(() =>
        {
            double dip = Dip();
            double each = SkinMath.TextElements(text).Sum(e => Advance(e, "Segoe UI", 20, FontWeight.FromOpenTypeWeight(600), dip));
            var style = new SkinText(true, 0, 0, FontSize: 20, LetterSpacing: 10);
            return (Spaced: PercentTextRenderer.Measure(style, text, dip).Width, Elements: each);
        });
        double expected = r.Elements + 10 * (elements - 1);
        _out.WriteLine($"'{text}' ({text.Length} UTF-16 units, {elements} elements): spaced 10 measured {r.Spaced:0.###}, " +
                       $"expected the elements' {r.Elements:0.###} + 10 x {elements - 1} = {expected:0.###}");
        Assert.Equal(elements, SkinMath.TextElements(text).Count);
        Assert.Equal(expected, r.Spaced, 6);
    }

    [Fact]
    public void Weight_and_italic_reach_the_typeface()
    {
        var r = _sta.Run(() =>
        {
            double dip = Dip();
            double Drawn(SkinText style)
            {
                var path = new Path();
                PercentTextRenderer.Update(path, style, "Volume", 1.0, dip);
                return path.Data.Bounds.Width;
            }
            var plain = new SkinText(true, 0, 0, FontSize: 30);
            return (Thin: Drawn(plain with { Weight = 100 }), ThinInk: TextInk.Width("Segoe UI", 30, FontWeights.Thin, FontStyles.Normal, "Volume", dip),
                Black: Drawn(plain with { Weight = 900 }), BlackInk: TextInk.Width("Segoe UI", 30, FontWeights.Black, FontStyles.Normal, "Volume", dip),
                Italic: Drawn(plain with { Italic = true }), ItalicInk: TextInk.Width("Segoe UI", 30, FontWeights.SemiBold, FontStyles.Italic, "Volume", dip),
                Upright: TextInk.Width("Segoe UI", 30, FontWeights.SemiBold, FontStyles.Normal, "Volume", dip));
        });
        _out.WriteLine($"'Volume' at 30: weight 100 drawn {r.Thin:0.###} / Thin ink {r.ThinInk:0.###}; 900 drawn {r.Black:0.###} / Black ink {r.BlackInk:0.###}; " +
                       $"italic drawn {r.Italic:0.###} / italic ink {r.ItalicInk:0.###} (upright {r.Upright:0.###})");
        Assert.Equal(r.ThinInk, r.Thin, 3);
        Assert.Equal(r.BlackInk, r.Black, 3);
        Assert.Equal(r.ItalicInk, r.Italic, 3);
        Assert.True(r.BlackInk - r.ThinInk > 1, "Thin and Black are too alike for this test to tell them apart");
        Assert.True(Math.Abs(r.ItalicInk - r.Upright) > 0.01, "italic and upright are too alike for this test to tell them apart");
    }

    /// <summary>A trimmed text is a prefix of it followed by an ellipsis, spaced or not: its width
    /// is exactly what some prefix plus "…" measures, and no bare prefix measures that. A
    /// proportional face, so a dropped ellipsis cannot hide behind equal advances.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void A_trimmed_text_ends_in_an_ellipsis(double spacing)
    {
        const string text = "Speakers (Realtek High Definition Audio)";
        var style = new SkinText(true, 0, 0, FontSize: 20, LetterSpacing: spacing);
        var r = _sta.Run(() =>
        {
            double dip = Dip();
            double trimmed = PercentTextRenderer.Update(new Path(), style, text, 1.0, dip, null, 150).Width;
            var withEllipsis = Enumerable.Range(0, text.Length).Where(k =>
                Math.Abs(PercentTextRenderer.Measure(style, text[..k] + "…", dip).Width - trimmed) < 0.01).ToList();
            var bare = Enumerable.Range(0, text.Length).Where(k =>
                Math.Abs(PercentTextRenderer.Measure(style, text[..k], dip).Width - trimmed) < 0.01).ToList();
            return (trimmed, withEllipsis, bare);
        });
        _out.WriteLine($"spacing {spacing}: trimmed to {r.trimmed:0.###} in a 150 box; prefixes that measure that with '…': " +
                       $"[{string.Join(", ", r.withEllipsis.Select(k => $"'{text[..k]}…'"))}], without: [{string.Join(", ", r.bare.Select(k => $"'{text[..k]}'"))}]");
        Assert.True(r.trimmed <= 150);
        Assert.NotEmpty(r.withEllipsis);
        Assert.Empty(r.bare);
    }

    /// <summary>The boxed layout the notice name uses: a text wider than its box ends in an
    /// ellipsis inside it, spaced or not; one that fits is untouched; a box with no room draws
    /// nothing; a family given outright wins over the style's.</summary>
    [Fact]
    public void A_boxed_text_is_trimmed_to_its_box()
    {
        const string text = "Speakers (Realtek High Definition Audio)";
        var r = _sta.Run(() =>
        {
            double dip = Dip();
            System.Windows.Size Boxed(SkinText style, double width, System.Windows.Media.FontFamily? family = null) =>
                PercentTextRenderer.Update(new Path(), style, text, 1.0, dip, family, width);
            var none = new Path();
            var nothing = PercentTextRenderer.Update(none, Mono, text, 1.0, dip, null, 0);
            return (Full: PercentTextRenderer.Measure(Mono, text, dip), Trimmed: Boxed(Mono, 150), SpacedTrimmed: Boxed(Mono with { LetterSpacing = 4 }, 150),
                Fits: Boxed(Mono, 10_000), Nothing: nothing, NothingEmpty: none.Data.Bounds.IsEmpty,
                Ellipsis: Advance("…", "Consolas", 20, FontWeight.FromOpenTypeWeight(600), dip),
                OtherFamily: Boxed(Mono, double.PositiveInfinity, new System.Windows.Media.FontFamily("Segoe UI")).Width,
                SegoeWidth: PercentTextRenderer.Measure(Mono with { FontFamily = "Segoe UI" }, text, dip).Width);
        });
        _out.WriteLine($"full {r.Full}; in 150: {r.Trimmed}, spaced 4 in 150: {r.SpacedTrimmed}; in 10000: {r.Fits}; " +
                       $"in 0: {r.Nothing} (empty {r.NothingEmpty}); ellipsis {r.Ellipsis:0.###}; Segoe UI given {r.OtherFamily:0.###} vs style {r.SegoeWidth:0.###}");
        Assert.True(r.Full.Width > 150, "the text fits the box: nothing to trim");
        Assert.InRange(r.Trimmed.Width, 150 - 2 * 11 - r.Ellipsis, 150);        // within a character and an ellipsis of the box
        Assert.InRange(r.SpacedTrimmed.Width, 150 - 2 * (11 + 4) - r.Ellipsis, 150);
        Assert.Equal(r.Full, r.Fits);
        Assert.Equal(r.Full.Height, r.Trimmed.Height);
        Assert.Equal(0, r.Nothing.Width);
        Assert.True(r.NothingEmpty, "a box with no room drew something");
        Assert.Equal(r.SegoeWidth, r.OtherFamily, 6);
    }
}
