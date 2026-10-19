using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AorinEQ.Core;
using AorinEQ.UI;
using Xunit.Abstractions;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace AorinEQ.Tests;

/// <summary>Review focus: the eyedropper on a DPI-scaled monitor. Pointer positions are DIPs and
/// the snapshot is device pixels; the pixel sampled must be the one under the pointer at any
/// scale, and an element offset inside its parent must be sampled where it is.</summary>
[Collection("Wpf")]
public class ArtboardSnapshotTests
{
    private readonly StaWpf _sta;
    private readonly ITestOutputHelper _out;

    public ArtboardSnapshotTests(StaWpf sta, ITestOutputHelper output)
    {
        _sta = sta;
        _out = output;
    }

    /// <summary>A 100x40 grid - opaque red on the left, half-transparent blue on the right - placed
    /// 30 px into a grey host, in a shown window.</summary>
    private T WithArtboard<T>(Func<FrameworkElement, T> body) =>
        _sta.Run(() =>
        {
            var art = new Grid { Width = 100, Height = 40 };
            art.ColumnDefinitions.Add(new ColumnDefinition());
            art.ColumnDefinitions.Add(new ColumnDefinition());
            art.Children.Add(new Border { Background = new SolidColorBrush(Colors.Red) });
            var blue = new Border { Background = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0xFF)) };
            Grid.SetColumn(blue, 1);
            art.Children.Add(blue);
            var host = new Border { Background = Brushes.Gray, Padding = new Thickness(30), Child = art };
            var window = new Window { Content = host, Width = 300, Height = 200, ShowActivated = false };
            window.Show();
            try { return body(art); }
            finally { window.Close(); }
        });

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void The_pixel_under_a_dip_point_is_sampled_at_any_dpi_scale(double dpiScale)
    {
        var r = WithArtboard(art =>
        {
            var snapshot = new ArtboardSnapshot(art, dpiScale);
            return (snapshot.PixelWidth, snapshot.PixelHeight, Red: snapshot.ColorAt(new Point(10, 10)),
                Blue: snapshot.ColorAt(new Point(75, 20)), Corner: snapshot.ColorAt(new Point(99.9, 39.9)),
                Outside: snapshot.ColorAt(new Point(100, 0)));
        });
        _out.WriteLine($"x{dpiScale}: {r.PixelWidth}x{r.PixelHeight} red={r.Red?.ToHex()} blue={r.Blue?.ToHex()} corner={r.Corner?.ToHex()} outside={r.Outside?.ToHex() ?? "(none)"}");
        Assert.Equal(((int)Math.Ceiling(100 * dpiScale), (int)Math.Ceiling(40 * dpiScale)), (r.PixelWidth, r.PixelHeight));
        Assert.Equal(new SkinColor(0xFF, 0xFF, 0, 0), r.Red);
        Assert.Equal(new SkinColor(0x80, 0, 0, 0xFF), r.Blue);
        Assert.Equal(new SkinColor(0x80, 0, 0, 0xFF), r.Corner);
        Assert.Null(r.Outside);
    }

    [Fact]
    public void An_offset_element_is_snapshotted_where_it_is()
    {
        var topLeft = WithArtboard(art => new ArtboardSnapshot(art, 1.0).ColorAt(new Point(0, 0)));
        // RenderTargetBitmap.Render(visual) would have drawn the art 30 px in, so (0,0) would be
        // the empty corner instead of the art's own first pixel.
        _out.WriteLine($"expected the art's own red at (0,0), actual {topLeft?.ToHex() ?? "(none)"}");
        Assert.Equal(new SkinColor(0xFF, 0xFF, 0, 0), topLeft);
    }

    [Fact]
    public void The_magnifier_region_is_clamped_to_the_snapshot()
    {
        var r = WithArtboard(art =>
        {
            var snapshot = new ArtboardSnapshot(art, 1.0);
            var middle = snapshot.Region(new Point(10, 10), 4)!;
            var corner = snapshot.Region(new Point(0, 0), 4)!;
            return ((middle.PixelWidth, middle.PixelHeight), (corner.PixelWidth, corner.PixelHeight));
        });
        _out.WriteLine($"expected middle 9x9 and corner 5x5, actual {r}");
        Assert.Equal(((9, 9), (5, 5)), r);
    }
}
