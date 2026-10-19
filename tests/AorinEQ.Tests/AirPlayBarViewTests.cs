using System.IO;
using System.Windows.Media;
using System.Windows.Media.Effects;
using AorinEQ.Core;
using AorinEQ.UI;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>The AirPlay strip's labels are drawn by the same renderer as the percent number, so
/// the outline and shadow docs/reference.md promises for them are real - and the strip's art is
/// not decoded again on every redraw the designer asks for.</summary>
[Collection("Wpf")]
public class AirPlayBarViewTests : IDisposable
{
    private readonly StaWpf _sta;
    private readonly ITestOutputHelper _out;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aorineq-airplaybar-" + Guid.NewGuid().ToString("N"));

    public AirPlayBarViewTests(StaWpf sta, ITestOutputHelper output)
    {
        _sta = sta;
        _out = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private SkinAirPlay Strip(SkinText? name, SkinText? level)
    {
        var empty = Path.Combine(_dir, "ae.png");
        var full = Path.Combine(_dir, "af.png");
        if (!File.Exists(empty))
        {
            RealPngs.WriteSolid(empty, 300, 30, System.Drawing.Color.DarkGray);
            RealPngs.WriteSolid(full, 300, 30, System.Drawing.Color.SteelBlue);
        }
        return new SkinAirPlay(empty, full, 300, 30, false, false, 1, 1, 0, 300, name, level, new SkinHitRect(0, 0, 300, 30));
    }

    private static readonly AirPlayBarState Live = new(true, "Kitchen", true, true, 40);

    [Fact]
    public void Labels_get_the_outline_and_shadow_the_skin_asks_for()
    {
        var name = new SkinText(true, 10, 5, OutlineColor: "#FFFF0000", OutlineWidth: 2, ShadowColor: "#80000000", ShadowBlur: 6, ShadowDepth: 3);
        var r = _sta.Run(() =>
        {
            var view = new AirPlayBarView();
            view.SetSkin(Strip(name, new SkinText(true, 290, 5, Align: "right")), 2.0);
            view.SetState(Live);
            var n = view.SkinNameText;
            var effect = n.Effect as DropShadowEffect;
            return (Stroke: (n.Stroke as SolidColorBrush)?.Color, n.StrokeThickness, ShadowColor: effect?.Color,
                Blur: effect?.BlurRadius, Depth: effect?.ShadowDepth, n.Margin, Level: view.SkinLevelText.Margin,
                LevelRight: view.SkinLevelText.Data.Bounds.Right, n.Visibility);
        });
        _out.WriteLine($"{r}");
        Assert.Equal(Color.FromArgb(0xFF, 0xFF, 0, 0), r.Stroke);
        Assert.Equal(4, r.StrokeThickness);
        Assert.Equal(Color.FromArgb(0x80, 0, 0, 0), r.ShadowColor);
        Assert.Equal((12.0, 6.0), (r.Blur, r.Depth));
        Assert.Equal((20.0, 10.0), (r.Margin.Left, r.Margin.Top));
        // Right-aligned: the level's ink ends at its anchor (290 x scale 2), less the glyph's
        // trailing side bearing.
        Assert.InRange(r.Level.Left + r.LevelRight, 574.0, 581.0);
        Assert.Equal(System.Windows.Visibility.Visible, r.Visibility);
    }

    [Fact]
    public void A_hidden_label_is_collapsed()
    {
        var visibility = _sta.Run(() =>
        {
            var view = new AirPlayBarView();
            view.SetSkin(Strip(new SkinText(false, 10, 5, FontSize: 30), null), 1.0);
            view.SetState(Live);
            return (view.SkinNameText.Visibility, view.SkinLevelText.Visibility);
        });
        _out.WriteLine($"expected (Collapsed, Collapsed), actual {visibility}");
        Assert.Equal((System.Windows.Visibility.Collapsed, System.Windows.Visibility.Collapsed), visibility);
    }

    /// <summary>The labels were TextBlocks at Normal weight unless bold; moving them onto the
    /// percent number's renderer must not make every existing skin's labels heavier.</summary>
    [Fact]
    public void An_unbold_label_keeps_its_normal_weight_and_a_bold_one_is_bold()
    {
        var plain = new SkinText(true, 10, 5, FontSize: 20);
        var bold = plain with { Bold = true };
        var r = _sta.Run(() =>
        {
            var view = new AirPlayBarView();
            double dip = VisualTreeHelper.GetDpi(view).PixelsPerDip;
            double WidthAt(SkinText spec, System.Windows.FontWeight weight)
            {
                var path = new System.Windows.Shapes.Path();
                PercentTextRenderer.Update(path, spec, Live.DeviceName, 1.0, dip, weight);
                return path.Data.Bounds.Width;
            }
            view.SetSkin(Strip(plain, null), 1.0);
            view.SetState(Live);
            double drawnPlain = view.SkinNameText.Data.Bounds.Width;
            view.SetSkin(Strip(bold, null), 1.0);
            double drawnBold = view.SkinNameText.Data.Bounds.Width;
            return (drawnPlain, drawnBold, Normal: WidthAt(plain, System.Windows.FontWeights.Normal),
                SemiBold: WidthAt(plain, System.Windows.FontWeights.SemiBold), Bold: WidthAt(bold, System.Windows.FontWeights.Normal));
        });
        _out.WriteLine($"plain label: drawn {r.drawnPlain:0.###}, expected Normal {r.Normal:0.###} (SemiBold would be {r.SemiBold:0.###})");
        _out.WriteLine($"bold label : drawn {r.drawnBold:0.###}, expected Bold {r.Bold:0.###}");
        Assert.Equal(r.Normal, r.drawnPlain, 3);
        Assert.True(Math.Abs(r.SemiBold - r.drawnPlain) > 0.01, "the label was drawn SemiBold, heavier than it has always been");
        Assert.Equal(r.Bold, r.drawnBold, 3);
    }

    [Fact]
    public void Restyling_the_strip_does_not_decode_its_art_again_but_new_art_does()
    {
        var r = _sta.Run(() =>
        {
            var view = new AirPlayBarView();
            var strip = Strip(new SkinText(true, 10, 5), null);
            view.SetSkin(strip, 1.0);
            var first = view.EmptyArt;
            view.SetSkin(strip with { Name = new SkinText(true, 40, 5, FontSize: 20), FillStartX = 30 }, 1.5);
            var restyled = view.EmptyArt;
            var other = Path.Combine(_dir, "other.png");
            RealPngs.WriteSolid(other, 300, 30, System.Drawing.Color.Orange);
            view.SetSkin(strip with { EmptyPath = other }, 1.0);
            var swapped = view.EmptyArt;
            view.SetSkin(null, 1.0);
            return (Same: ReferenceEquals(first, restyled), Swapped: !ReferenceEquals(first, swapped), Released: view.EmptyArt is null);
        });
        _out.WriteLine($"{r}");
        Assert.True(r.Same, "a restyle decoded the strip art again");
        Assert.True(r.Swapped);
        Assert.True(r.Released, "the native strip kept the skin's bitmap alive");
    }

    [Fact]
    public void Hud_colours_parse_through_the_one_parser()
    {
        var fallback = Colors.DeepSkyBlue;
        var cases = new (string? Input, Color Expected)[]
        {
            ("#80FF0000", Color.FromArgb(0x80, 0xFF, 0, 0)), ("red", Colors.Red), ("not a colour", fallback), (null, fallback),
        };
        foreach (var (input, expected) in cases)
        {
            var actual = HudSpectrumView.ParseColor(input, fallback);
            _out.WriteLine($"ParseColor({input ?? "null"}): expected {expected}, actual {actual}");
            Assert.Equal(expected, actual);
        }
    }
}
