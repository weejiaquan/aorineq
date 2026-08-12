using System.Windows.Media.Imaging;

namespace ApoVolume.Tests;

/// <summary>The shipped .ico is the app's identity everywhere the shell shows a window: the exe's
/// Win32 icon (taskbar/alt-tab/Explorer) via ApplicationIcon, and every window's title bar via the
/// embedded WPF resource. None of that fails the build — a missing, truncated, or single-size icon
/// only shows up as a blank or fuzzy glyph at runtime — so the shipped asset is checked here.
///
/// The TRAY is not among its users: since v2.1.2 the notification area draws its own glyph at
/// runtime (see <see cref="TrayGlyphTests"/>), which is why the muted variant of this file, and the
/// mute-to-art mapping that chose between them, are gone.
///
/// The frames are PNG-compressed (every size, not just 256), which Windows and WIC handle but
/// System.Drawing's managed rasterisers (Icon.ToBitmap, Graphics.DrawIcon) do not. Nothing loads
/// this file through those, so it is asserted through WIC — the decoder WPF itself uses.</summary>
public class AppIconTests
{
    /// <summary>The sizes Windows picks between: 16 title bar, 24/32 taskbar and alt-tab at common
    /// DPIs, 48/64/256 Explorer views.</summary>
    private static readonly int[] ExpectedSizes = [16, 24, 32, 48, 64, 256];

    /// <summary>The file name is a literal in three places that must agree: the csproj's
    /// ApplicationIcon and Resource items, and every window's <c>Icon="/ApoVolume.ico"</c>.</summary>
    private const string IconFileName = "ApoVolume.ico";

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
}
