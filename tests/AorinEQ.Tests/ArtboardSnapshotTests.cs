using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AorinEQ.Core;
using AorinEQ.UI;
using Xunit.Abstractions;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using Point = System.Windows.Point;

namespace AorinEQ.Tests;

/// <summary>Review focus: the eyedropper on a DPI-scaled monitor. Pointer positions are DIPs and
/// the snapshot is device pixels; the pixel sampled must be the one under the pointer at any
/// scale and zoom, and an element offset inside its parent must be sampled where it is.</summary>
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
    /// inside a padded grey host (so not at its parent's origin), in a shown window.</summary>
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

    private static readonly Rect WholeArt = new(0, 0, 100, 40);

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void The_pixel_under_a_dip_point_is_sampled_at_any_dpi_scale(double dpiScale)
    {
        var r = WithArtboard(art =>
        {
            var snapshot = new ArtboardSnapshot(art, dpiScale, 1, WholeArt);
            return (snapshot.PixelWidth, snapshot.PixelHeight, Red: snapshot.ColorAt(new Point(10, 10)),
                LastRed: snapshot.ColorAt(new Point(49.9, 20)), FirstBlue: snapshot.ColorAt(new Point(50, 20)),
                Corner: snapshot.ColorAt(new Point(99.9, 39.9)), Outside: snapshot.ColorAt(new Point(100, 0)));
        });
        _out.WriteLine($"x{dpiScale}: {r.PixelWidth}x{r.PixelHeight} red={r.Red?.ToHex()} at 49.9={r.LastRed?.ToHex()} at 50={r.FirstBlue?.ToHex()} " +
                       $"corner={r.Corner?.ToHex()} outside={r.Outside?.ToHex() ?? "(none)"}");
        Assert.Equal(((int)Math.Ceiling(100 * dpiScale), (int)Math.Ceiling(40 * dpiScale)), (r.PixelWidth, r.PixelHeight));
        Assert.Equal(new SkinColor(0xFF, 0xFF, 0, 0), r.Red);
        Assert.Equal(new SkinColor(0xFF, 0xFF, 0, 0), r.LastRed);
        Assert.Equal(new SkinColor(0x80, 0, 0, 0xFF), r.FirstBlue);
        Assert.Equal(new SkinColor(0x80, 0, 0, 0xFF), r.Corner);
        Assert.Null(r.Outside);
    }

    [Fact]
    public void An_offset_element_is_snapshotted_where_it_is()
    {
        var topLeft = WithArtboard(art => new ArtboardSnapshot(art, 1.0, 1, WholeArt).ColorAt(new Point(0, 0)));
        // A VisualBrush, like RenderTargetBitmap.Render(visual), draws the art where it sits in its
        // padded host, so a viewbox at (0,0) would see the empty corner, not the art's first pixel.
        _out.WriteLine($"expected the art's own red at (0,0), actual {topLeft?.ToHex() ?? "(none)"}");
        Assert.Equal(new SkinColor(0xFF, 0xFF, 0, 0), topLeft);
    }

    [Fact]
    public void The_magnifier_region_is_clamped_to_the_snapshot()
    {
        var r = WithArtboard(art =>
        {
            var snapshot = new ArtboardSnapshot(art, 1.0, 1, WholeArt);
            var middle = snapshot.Region(new Point(10, 10), 4)!;
            var corner = snapshot.Region(new Point(0, 0), 4)!;
            return ((middle.PixelWidth, middle.PixelHeight), (corner.PixelWidth, corner.PixelHeight));
        });
        _out.WriteLine($"expected middle 9x9 and corner 5x5, actual {r}");
        Assert.Equal(((9, 9), (5, 5)), r);
    }

    /// <summary>Only the area asked for (the part on screen) is rendered - so 800 % never builds the
    /// whole canvas - and it is still addressed in the element's own DIPs.</summary>
    [Fact]
    public void Only_the_area_on_screen_is_rendered_and_it_is_addressed_as_the_whole()
    {
        var r = WithArtboard(art =>
        {
            var snapshot = new ArtboardSnapshot(art, 1.5, 8, new Rect(45.3, 10, 10, 5));
            return (snapshot.PixelWidth, snapshot.PixelHeight, Red: snapshot.ColorAt(new Point(49.99, 12)),
                Blue: snapshot.ColorAt(new Point(50, 12)), Left: snapshot.ColorAt(new Point(45, 12)), Above: snapshot.ColorAt(new Point(50, 9.9)));
        });
        // 45.3..55.3 at 12 px/DIP: device columns 543..664 -> 121 wide; rows 120..180 -> 60 high.
        _out.WriteLine($"expected 121x60, red at 49.99, blue at 50, nothing left of or above the area; actual {r.PixelWidth}x{r.PixelHeight} " +
                       $"red={r.Red?.ToHex()} blue={r.Blue?.ToHex()} left={r.Left?.ToHex() ?? "(none)"} above={r.Above?.ToHex() ?? "(none)"}");
        Assert.Equal((121, 60), (r.PixelWidth, r.PixelHeight));
        Assert.Equal(new SkinColor(0xFF, 0xFF, 0, 0), r.Red);
        Assert.Equal(new SkinColor(0x80, 0, 0, 0xFF), r.Blue);
        Assert.Null(r.Left);
        Assert.Null(r.Above);
    }

    /// <summary>Review focus at 150 % and zoom 4: one-pixel checker art drawn nearest-neighbour as
    /// the zoomed canvas draws it. Just either side of every art-pixel edge the snapshot gives the
    /// colour of the device pixel the screen shows there. A snapshot at the DPI alone (1.5 px per
    /// DIP) puts device-pixel centres across those edges and samples the neighbour.</summary>
    [Fact]
    public void A_zoomed_canvas_is_sampled_as_the_screen_shows_it()
    {
        const double dpi = 1.5, zoom = 4;
        var r = _sta.Run(() =>
        {
            var pixels = new byte[4 * 4 * 4];
            for (int i = 0; i < 16; i++)
            {
                byte v = (i % 4 + i / 4) % 2 == 0 ? (byte)0 : (byte)0xFF;
                pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = v;
                pixels[i * 4 + 3] = 0xFF;
            }
            var checker = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, pixels, 16);
            var art = new Image { Source = checker, Width = 4, Height = 4, Stretch = Stretch.Fill };
            var artboards = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            artboards.Children.Add(art);
            RenderOptions.SetBitmapScalingMode(artboards, BitmapScalingMode.NearestNeighbor);
            var zoomHost = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                LayoutTransform = new ScaleTransform(zoom, zoom),
            };
            zoomHost.Children.Add(artboards);
            var window = new Window { Content = zoomHost, Width = 200, Height = 200, ShowActivated = false, Style = new Style(typeof(Window)) };
            window.Show();
            try
            {
                window.UpdateLayout();
                var screen = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi), (int)Math.Ceiling(window.ActualHeight * dpi),
                    96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
                screen.Render(window);
                var snapshot = new ArtboardSnapshot(artboards, dpi, zoom, new Rect(0, 0, 4, 4));
                var dpiOnly = new ArtboardSnapshot(artboards, dpi, 1, new Rect(0, 0, 4, 4));
                var rows = new List<(Point At, string Screen, string? Snapshot, string? DpiOnly)>();
                foreach (double x in new[] { 0.99, 1.01, 1.99, 2.01, 2.99, 3.01 })
                    foreach (double y in new[] { 0.5, 1.99, 2.01 })
                    {
                        var at = new Point(x, y);
                        var onScreen = artboards.TranslatePoint(at, window);
                        var bgra = new byte[4];
                        screen.CopyPixels(new Int32Rect((int)Math.Floor(onScreen.X * dpi), (int)Math.Floor(onScreen.Y * dpi), 1, 1), bgra, 4, 0);
                        rows.Add((at, new SkinColor(bgra[3], bgra[2], bgra[1], bgra[0]).ToHex(),
                            snapshot.ColorAt(at)?.ToHex(), dpiOnly.ColorAt(at)?.ToHex()));
                    }
                return rows;
            }
            finally { window.Close(); }
        });
        foreach (var row in r)
            _out.WriteLine($"at {row.At}: screen {row.Screen}, snapshot {row.Snapshot}, dpi-only {row.DpiOnly}");
        Assert.All(r, row => Assert.Equal(row.Screen, row.Snapshot));
        // Not a vacuous match: the samples straddle edges, so both checker colours appear...
        Assert.Equal(2, r.Select(row => row.Screen).Distinct().Count());
        // ...and the art pixel under each point is the one the screen shows.
        Assert.All(r, row => Assert.Equal(((int)row.At.X + (int)row.At.Y) % 2 == 0 ? "#FF000000" : "#FFFFFFFF", row.Screen));
        Assert.Contains(r, row => row.DpiOnly != row.Screen);
    }
}
