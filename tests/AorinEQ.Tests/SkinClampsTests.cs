using AorinEQ.Core;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>Every range a skin value is pulled into, pinned once. SkinLoader reads through these
/// and the designer's edits write through them, so a clamp that drifts here drifts in both - and
/// the existing SkinLoaderTests (run unchanged in this task) prove the loader did not move.</summary>
public class SkinClampsTests
{
    private readonly ITestOutputHelper _out;

    public SkinClampsTests(ITestOutputHelper output) => _out = output;

    private void Check<T>(string what, T expected, T actual)
    {
        _out.WriteLine($"{what}: expected {expected}, actual {actual}");
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(0.1, 0.25)]
    [InlineData(5.0, 4.0)]
    [InlineData(1.5, 1.5)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    public void Scale_is_pulled_into_a_quarter_to_four(double input, double expected) =>
        Check($"Scale({input})", expected, SkinClamps.Scale(input));

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(90.0, 60.0)]
    [InlineData(12.0, 12.0)]
    [InlineData(double.NaN, 10.0)]
    public void Fps_is_pulled_into_one_to_sixty(double input, double expected) =>
        Check($"Fps({input})", expected, SkinClamps.Fps(input));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    [InlineData(7, 7)]
    public void Frames_are_at_least_one(int input, int expected) =>
        Check($"Frames({input})", expected, SkinClamps.Frames(input));

    [Theory]
    [InlineData(-1.0, 0.0)]
    [InlineData(2.0, 1.0)]
    [InlineData(0.25, 0.25)]
    [InlineData(double.NaN, 0.6)]
    public void Mute_dim_is_an_opacity(double input, double expected) =>
        Check($"MutedDim({input})", expected, SkinClamps.MutedDim(input));

    [Theory]
    [InlineData(1.0, 4.0)]
    [InlineData(500.0, 200.0)]
    [InlineData(13.5, 13.5)]
    [InlineData(double.NaN, 14.0)]
    public void Font_size_is_pulled_into_four_to_two_hundred(double input, double expected) =>
        Check($"FontSize({input})", expected, SkinClamps.FontSize(input));

    [Fact]
    public void Outline_shadow_corner_and_padding_have_their_documented_ranges()
    {
        Check("OutlineWidth(-2)", 0.0, SkinClamps.OutlineWidth(-2));
        Check("OutlineWidth(40)", 20.0, SkinClamps.OutlineWidth(40));
        Check("OutlineWidth(2)", 2.0, SkinClamps.OutlineWidth(2));
        Check("ShadowBlur(80)", 50.0, SkinClamps.ShadowBlur(80));
        Check("ShadowBlur(NaN)", 4.0, SkinClamps.ShadowBlur(double.NaN));
        Check("ShadowDepth(-1)", 0.0, SkinClamps.ShadowDepth(-1));
        Check("ShadowDepth(NaN)", 2.0, SkinClamps.ShadowDepth(double.NaN));
        Check("CornerRadius(99)", 64.0, SkinClamps.CornerRadius(99));
        Check("CornerRadius(-3)", 0.0, SkinClamps.CornerRadius(-3));
        Check("Padding(99)", 64.0, SkinClamps.Padding(99));
        Check("Padding(10)", 10.0, SkinClamps.Padding(10));
    }

    [Fact]
    public void A_fill_range_defaults_to_the_full_width_and_stays_inside_it()
    {
        Check("FillStart(null, 300)", 0, SkinClamps.FillStart(null, 300));
        Check("FillStart(-5, 300)", 0, SkinClamps.FillStart(-5, 300));
        Check("FillStart(999, 300)", 300, SkinClamps.FillStart(999, 300));
        Check("FillStart(40, 300)", 40, SkinClamps.FillStart(40, 300));
        Check("FillEnd(null, 300)", 300, SkinClamps.FillEnd(null, 300));
        Check("FillEnd(999, 300)", 300, SkinClamps.FillEnd(999, 300));
        Check("FillEnd(-5, 300)", 0, SkinClamps.FillEnd(-5, 300));
        Check("FillEnd(null, 0)", 0, SkinClamps.FillEnd(null, 0));
    }

    [Fact]
    public void A_hit_region_defaults_to_the_whole_area_and_is_pulled_inside_it()
    {
        Check("Hit(all null)", new SkinHitRect(0, 0, 300, 40), SkinClamps.Hit(null, null, null, null, 300, 40));
        Check("Hit(overhang)", new SkinHitRect(290, 0, 10, 40), SkinClamps.Hit(290, 0, 30, 40, 300, 40));
        Check("Hit(past the end)", new SkinHitRect(300, 0, 0, 40), SkinClamps.Hit(400, 0, 30, 40, 300, 40));
        Check("Hit(rect)", new SkinHitRect(0, 10, 50, 30), SkinClamps.Hit(new SkinHitRect(-5, 10, 50, 90), 300, 40));
    }

    [Fact]
    public void An_optional_region_that_clamps_to_nothing_is_dropped()
    {
        var dropped = SkinClamps.OptionalHit(new SkinHitRect(300, 0, 20, 40), 300, 40);
        var kept = SkinClamps.OptionalHit(new SkinHitRect(280, 0, 40, 40), 300, 40);
        _out.WriteLine($"zero-width -> {dropped?.ToString() ?? "(null)"}, overhang -> {kept}");
        Assert.Null(dropped);
        Assert.Equal(new SkinHitRect(280, 0, 20, 40), kept);
    }

    [Theory]
    [InlineData("CENTER ", "center")]
    [InlineData("right", "right")]
    [InlineData("justify", "left")]
    [InlineData(null, "left")]
    public void Align_reads_anything_unknown_as_left(string? input, string expected) =>
        Check($"Align('{input}')", expected, SkinClamps.Align(input));

    [Fact]
    public void Text_takes_defaults_for_blanks_and_clamps_every_number()
    {
        var raw = new SkinText(true, 3, 4, Color: " ", FontFamily: "", FontSize: 1, Weight: 1234, Italic: true, LetterSpacing: 500,
            OutlineColor: "  ", OutlineWidth: 50, ShadowColor: "", ShadowBlur: 99, ShadowDepth: -1, Align: "RIGHT");
        var expected = new SkinText(true, 3, 4, "#FFFFFFFF", "Segoe UI", 4, 900, true, 100, null, 20, null, 50, 0, "right");
        Check("Text(raw)", expected, SkinClamps.Text(raw));
    }

    [Theory]
    [InlineData(0, 100)]       // below the scale: the lightest
    [InlineData(-300, 100)]
    [InlineData(149, 100)]
    [InlineData(150, 200)]     // half way rounds up, away from zero
    [InlineData(449, 400)]
    [InlineData(450, 500)]
    [InlineData(600, 600)]
    [InlineData(949, 900)]
    [InlineData(950, 900)]     // clamped to 900 first
    [InlineData(450.5, 500)]   // a hand-written fraction is still a weight
    [InlineData(449.9, 400)]
    [InlineData(double.NaN, 600)]
    [InlineData(int.MaxValue, 900)]
    public void Weight_rounds_to_the_nearest_hundred_inside_100_to_900(double input, int expected) =>
        Check($"Weight({input})", expected, SkinClamps.Weight(input));

    [Theory]
    [InlineData(-50, -20)]
    [InlineData(-20, -20)]
    [InlineData(2.5, 2.5)]
    [InlineData(100, 100)]
    [InlineData(250, 100)]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    public void Letter_spacing_is_clamped_to_minus_20_through_100(double input, double expected) =>
        Check($"LetterSpacing({input})", expected, SkinClamps.LetterSpacing(input));

    [Fact]
    public void A_hidden_text_carrying_nothing_is_no_text_at_all()
    {
        var styled = new SkinText(false, 5, 0);
        _out.WriteLine($"null -> {SkinClamps.OptionalText(null, SkinLayer.PercentText)?.ToString() ?? "(null)"}; " +
                       $"blank hidden -> {SkinClamps.OptionalText(new SkinText(false, 0, 0, Color: " "), SkinLayer.PercentText)?.ToString() ?? "(null)"}; " +
                       $"styled -> {SkinClamps.OptionalText(styled, SkinLayer.PercentText)}");
        Assert.Null(SkinClamps.OptionalText(null, SkinLayer.PercentText));
        Assert.Null(SkinClamps.OptionalText(new SkinText(false, 0, 0, Color: " "), SkinLayer.PercentText));
        Assert.Equal(styled, SkinClamps.OptionalText(styled, SkinLayer.PercentText));
    }

    /// <summary>"Nothing" depends on the text: an AirPlay label's default weight is Normal, so a
    /// hidden label at Normal carries nothing, while the same text as the percent number carries a
    /// weight that is not its default - and the other way round.</summary>
    [Fact]
    public void A_hidden_text_is_nothing_only_at_its_own_default_weight()
    {
        var label = SkinText.HiddenLabel;
        var number = SkinText.Hidden;
        var r = (LabelAsLabel: SkinClamps.OptionalText(label, SkinLayer.AirPlayName),
            LabelAsNumber: SkinClamps.OptionalText(label, SkinLayer.PercentText),
            NumberAsLabel: SkinClamps.OptionalText(number, SkinLayer.AirPlayName),
            WeightOffScale: SkinClamps.OptionalText(label with { Weight = 420 }, SkinLayer.AirPlayName));
        _out.WriteLine($"label weight {label.Weight}, number weight {number.Weight}: {r}");
        Assert.Equal(400, label.Weight);
        Assert.Equal(600, number.Weight);
        Assert.Null(r.LabelAsLabel);
        Assert.Equal(label, r.LabelAsNumber);
        Assert.Equal(number, r.NumberAsLabel);
        Assert.Null(r.WeightOffScale); // 420 rounds to the label's 400: still nothing
    }
}
