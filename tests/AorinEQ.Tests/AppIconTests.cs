using System.Windows.Media.Imaging;

namespace AorinEQ.Tests;

/// <summary>The shipped .ico is the app's identity everywhere the shell shows a window: the exe's
/// Win32 icon (taskbar/alt-tab/Explorer) via ApplicationIcon, and every window's title bar via the
/// embedded WPF resource. None of that fails the build — a missing, truncated, or single-size icon
/// only shows up as a blank or fuzzy glyph at runtime — so the shipped asset is checked here.
///
/// The TRAY is not among its users: since v2.1.2 the notification area draws its own glyph at
/// runtime (see <see cref="TrayGlyphTests"/>), which is why the muted variant of this file, and the
/// mute-to-art mapping that chose between them, are gone.
///
/// Since v3.1.0 the file is GENERATED, not authored: <c>tools/AppIconGen</c> runs
/// <see cref="AorinEQ.Core.AppIconArt"/> through <see cref="AorinEQ.Core.IcoWriter"/> and writes it,
/// so the taskbar icon and the tray glyph are the same speaker geometry. Those two types carry
/// their own tests; what is asserted HERE is the committed artefact — that it was regenerated after
/// the art changed, and that it is a well-formed icon on disk.
///
/// Until v3.1.0 every frame was PNG-compressed (all sizes, not just 256). Frames up to 64px are now
/// uncompressed DIBs and only the 256 is PNG, so the file is asserted through BOTH decoders that
/// read it: WIC (what WPF uses for every window's Icon) and System.Drawing.</summary>
public class AppIconTests
{
    /// <summary>The sizes Windows picks between: 16 title bar, 24/32 taskbar and alt-tab at common
    /// DPIs, 48/64/256 Explorer views.</summary>
    private static readonly int[] ExpectedSizes = [16, 24, 32, 48, 64, 256];

    /// <summary>The file name is a literal in three places that must agree: the csproj's
    /// ApplicationIcon and Resource items, and every window's <c>Icon="/AorinEQ.ico"</c>.</summary>
    private const string IconFileName = "AorinEQ.ico";

    private static string IconPath => Path.Combine(AppContext.BaseDirectory, IconFileName);

    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public AppIconTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    [Fact]
    public void IconShipsWithTheApp()
    {
        Assert.True(File.Exists(IconPath), $"app icon missing at {IconPath}");

        var bytes = File.ReadAllBytes(IconPath);
        _out.WriteLine($"{IconPath} is {bytes.Length} bytes");

        // ICONDIR: reserved=0, type=1 (icon), then the frame count.
        Assert.Equal(0, BitConverter.ToUInt16(bytes, 0));
        Assert.Equal(1, BitConverter.ToUInt16(bytes, 2));
        Assert.Equal(ExpectedSizes.Length, BitConverter.ToUInt16(bytes, 4));
    }

    [Fact]
    public void IconCarriesEveryExpectedSizeForWindowsToPickFrom()
    {
        var decoder = BitmapDecoder.Create(
            new Uri(IconPath), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

        var sizes = decoder.Frames.Select(f => f.PixelWidth).OrderBy(w => w).ToArray();
        foreach (var frame in decoder.Frames)
            _out.WriteLine($"WIC frame {frame.PixelWidth}x{frame.PixelHeight} {frame.Format}");

        Assert.Equal(ExpectedSizes, sizes);
        Assert.All(decoder.Frames, f => Assert.Equal(f.PixelWidth, f.PixelHeight));
    }

    [Fact]
    public void EveryFrameDecodesToRealPixels()
    {
        var decoder = BitmapDecoder.Create(
            new Uri(IconPath), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

        foreach (var frame in decoder.Frames)
        {
            // The art is an opaque rounded square, so the centre pixel is fully opaque in every
            // frame. A frame that failed to decode comes back fully transparent instead.
            var converted = new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            var pixel = new byte[4];
            var centre = frame.PixelWidth / 2;
            converted.CopyPixels(
                new System.Windows.Int32Rect(centre, centre, 1, 1), pixel, 4, 0);

            _out.WriteLine($"{frame.PixelWidth}px centre BGRA = "
                + $"{pixel[0]},{pixel[1]},{pixel[2]},{pixel[3]}");
            Assert.Equal(255, pixel[3]);
        }
    }

    /// <summary>The committed file through System.Drawing, the stricter of the two readers. WIC
    /// (above) is forgiving about a DIB's declared height and stride; System.Drawing is not, so a
    /// generator that mis-assembled the uncompressed frames passes there and fails here — which is
    /// the whole reason to load the shipped icon twice.
    ///
    /// Nothing in the app takes this path today; that is exactly why it is worth pinning. The next
    /// person to reach for <c>Icon.ToBitmap</c> for a menu image or a WinForms surface should get a
    /// working bitmap of the size they asked for. Only the small frames are checked because only
    /// they are stored uncompressed — the 256 remains PNG on purpose (a 256 DIB is ~270 KB).
    ///
    /// 64 is written out rather than read from <c>IcoWriter.MaxDibSizePx</c>, like every other
    /// expectation in this file: a test that takes its own bounds from the code it checks passes
    /// vacuously the moment that code stops storing anything uncompressed.</summary>
    [Fact]
    public void SmallFramesLoadThroughSystemDrawingsRasteriser()
    {
        foreach (int size in ExpectedSizes.Where(s => s <= 64))
        {
            using var icon = new System.Drawing.Icon(IconPath, size, size);
            using var raster = icon.ToBitmap();

            var centre = raster.GetPixel(raster.Width / 2, raster.Height / 2);
            _out.WriteLine($"System.Drawing {size}px -> icon {icon.Width}x{icon.Height}, "
                + $"raster {raster.Width}x{raster.Height}, centre {centre}");

            Assert.Equal(size, icon.Width);
            Assert.Equal(size, raster.Width);
            Assert.Equal(size, raster.Height);
            Assert.Equal(255, centre.A);
        }
    }
}
