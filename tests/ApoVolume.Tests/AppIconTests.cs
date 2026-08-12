using System.Drawing;
using System.Windows.Media.Imaging;
using ApoVolume.Core;

namespace ApoVolume.Tests;

/// <summary>The shipped .ico files are the app's whole visual identity: the exe's Win32 icon
/// (taskbar/alt-tab/Explorer) via ApplicationIcon, every window's title bar via the embedded WPF
/// resource, and the tray icon via the same resource. None of that fails the build — a missing,
/// truncated, or single-size icon only shows up as a blank or fuzzy glyph at runtime — so both
/// shipped assets and their runtime load paths are checked here.
///
/// Two files ship: the normal art and the muted variant the tray swaps to while audio is muted
/// (<see cref="AppIcons.FileName"/>). Both must carry the same frame set, because the tray asks
/// for whichever size the shell wants and a missing frame would be a fuzzy icon in one state only.
///
/// The frames are PNG-compressed (every size, not just 256), which Windows and WIC handle but
/// System.Drawing's managed rasterisers (Icon.ToBitmap, Graphics.DrawIcon) do not. Neither the
/// tray nor WPF goes through those, so the tray path is asserted at the HICON level instead.</summary>
public class AppIconTests
{
    /// <summary>The sizes Windows picks between: 16 tray/title bar, 24/32 taskbar and alt-tab at
    /// common DPIs, 48/64/256 Explorer views.</summary>
    private static readonly int[] ExpectedSizes = [16, 24, 32, 48, 64, 256];

    /// <summary>Both mute states, i.e. both shipped icon files.</summary>
    public static TheoryData<bool> MuteStates => new() { false, true };

    public static TheoryData<bool, int> MuteStatesAndTraySizes => new()
    {
        { false, 16 }, { false, 32 },  // tray and title bar at 100% / 200% DPI
        { true, 16 }, { true, 32 },
    };

    private static string PathFor(bool muted) =>
        Path.Combine(AppContext.BaseDirectory, AppIcons.FileName(muted));

    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public AppIconTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    [Theory]
    [MemberData(nameof(MuteStates))]
    public void IconShipsWithTheApp(bool muted)
    {
        var path = PathFor(muted);
        Assert.True(File.Exists(path), $"app icon missing at {path}");

        var bytes = File.ReadAllBytes(path);
        _out.WriteLine($"{path} is {bytes.Length} bytes");

        // ICONDIR: reserved=0, type=1 (icon), then the frame count.
        Assert.Equal(0, BitConverter.ToUInt16(bytes, 0));
        Assert.Equal(1, BitConverter.ToUInt16(bytes, 2));
        Assert.Equal(ExpectedSizes.Length, BitConverter.ToUInt16(bytes, 4));
    }

    [Theory]
    [MemberData(nameof(MuteStates))]
    public void IconCarriesEveryExpectedSizeForWindowsToPickFrom(bool muted)
    {
        var decoder = BitmapDecoder.Create(
            new Uri(PathFor(muted)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

        var sizes = decoder.Frames.Select(f => f.PixelWidth).OrderBy(w => w).ToArray();
        foreach (var frame in decoder.Frames)
            _out.WriteLine($"WIC frame {frame.PixelWidth}x{frame.PixelHeight} {frame.Format}");

        Assert.Equal(ExpectedSizes, sizes);
        Assert.All(decoder.Frames, f => Assert.Equal(f.PixelWidth, f.PixelHeight));
    }

    [Theory]
    [MemberData(nameof(MuteStates))]
    public void EveryFrameDecodesToRealPixels(bool muted)
    {
        var decoder = BitmapDecoder.Create(
            new Uri(PathFor(muted)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

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

    [Theory]
    [MemberData(nameof(MuteStatesAndTraySizes))]
    public void TrayCanTurnTheIconIntoAnHicon(bool muted, int size)
    {
        // Exactly what TrayIcon.LoadIcon does, minus the pack-resource lookup: NotifyIcon
        // hands this HICON straight to the shell, so a size mismatch here is a fuzzy tray icon.
        using var stream = File.OpenRead(PathFor(muted));
        using var icon = new Icon(stream, size, size);

        _out.WriteLine($"muted={muted} asked {size}px, got {icon.Width}x{icon.Height}, handle={icon.Handle}");
        Assert.Equal(size, icon.Width);
        Assert.Equal(size, icon.Height);
        Assert.NotEqual(IntPtr.Zero, icon.Handle);
    }

    /// <summary>The regression this pair exists for: v2.0.1 replaced the two runtime-drawn glyphs
    /// with one constant piece of brand art, so the tray stopped indicating mute at all. The
    /// selection must reach genuinely different pixels — an accidental copy of the same art would
    /// pass every structural check above while showing the user nothing.</summary>
    [Fact]
    public void MutedStateSelectsDifferentArtFromUnmuted()
    {
        Assert.Equal("ApoVolume.ico", AppIcons.FileName(muted: false));
        Assert.Equal("ApoVolume-muted.ico", AppIcons.FileName(muted: true));
        Assert.Equal("pack://application:,,,/ApoVolume-muted.ico", AppIcons.ResourceUri(muted: true));
        Assert.Equal("pack://application:,,,/ApoVolume.ico", AppIcons.ResourceUri(muted: false));

        int differing = 0;
        foreach (var size in ExpectedSizes)
        {
            var normal = FramePixels(PathFor(muted: false), size);
            var mutedPixels = FramePixels(PathFor(muted: true), size);
            Assert.Equal(normal.Length, mutedPixels.Length);
            int diff = normal.Where((b, i) => b != mutedPixels[i]).Count();
            _out.WriteLine($"{size}px: {diff}/{normal.Length} bytes differ between normal and muted art");
            if (diff > 0)
                differing++;
        }
        Assert.Equal(ExpectedSizes.Length, differing);
    }

    /// <summary>BGRA pixels of the frame at the given square size.</summary>
    private static byte[] FramePixels(string path, int size)
    {
        var decoder = BitmapDecoder.Create(
            new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.Single(f => f.PixelWidth == size);
        var converted = new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var pixels = new byte[size * size * 4];
        converted.CopyPixels(pixels, size * 4, 0);
        return pixels;
    }
}
