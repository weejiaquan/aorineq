using System.Drawing;
using System.Windows.Media.Imaging;
using AorinEQ.Core;

namespace AorinEQ.Tests;

/// <summary>The .ico container, asserted against the two rasterisers that will actually open the
/// shipped file: WIC (what WPF uses for every window's <c>Icon</c>) and System.Drawing (what
/// <c>Icon.ToBitmap</c>/<c>Graphics.DrawIcon</c> use, and what any future tray or menu code would
/// reach for).
///
/// Loading it twice is not belt-and-braces here. The writer assembles the DIB frames by hand —
/// doubled header height, bottom-up rows, a separately strided 1bpp mask — and every one of those
/// is a field WIC is forgiving about and System.Drawing is not. A DIB with the wrong declared
/// height decodes as its own top half through WIC and fails outright through System.Drawing, which
/// is why <see cref="SmallFramesSurviveSystemDrawingsRasteriser"/> earns its place: it is the test
/// that catches a malformed DIB, not the WIC round trip.
///
/// On the v2.0.1 PNG history recorded in the ledger — all frames PNG-compressed, blamed for
/// "Requested range extends past the end of the array" out of Icon.ToBitmap — the layout tests
/// below pin the uncompressed frames for the reasons given in <see cref="IcoWriter"/>'s own docs,
/// but NOT because a PNG frame still throws. It does not on .NET 8; that was checked by building
/// this icon with every frame PNG and rasterising it, and it loaded fine.</summary>
public class IcoWriterTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public IcoWriterTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private const int IconDirSize = 6;
    private const int IconDirEntrySize = 16;

    /// <summary>Real art at the real shipped sizes — the writer is only interesting on the frames
    /// it will actually be asked to store.</summary>
    private static Bitmap[] ShippedFrames() =>
        AppIconArt.FrameSizes.Select(AppIconArt.Draw).ToArray();

    private static void DisposeAll(IEnumerable<Bitmap> frames)
    {
        foreach (var frame in frames) frame.Dispose();
    }

    private static byte[] WriteShipped()
    {
        var frames = ShippedFrames();
        try { return IcoWriter.Write(frames); }
        finally { DisposeAll(frames); }
    }

    /// <summary>ICONDIR: reserved must be 0 and type must be 1 (2 would make it a cursor, which the
    /// shell loads with a hotspot instead of an icon), and the count must cover every frame.</summary>
    [Fact]
    public void HeaderDeclaresAnIconCarryingEveryFrame()
    {
        var bytes = WriteShipped();
        _out.WriteLine($"{bytes.Length} bytes for {AppIconArt.FrameSizes.Count} frames");

        Assert.Equal(0, BitConverter.ToUInt16(bytes, 0));
        Assert.Equal(1, BitConverter.ToUInt16(bytes, 2));
        Assert.Equal(AppIconArt.FrameSizes.Count, BitConverter.ToUInt16(bytes, 4));
    }

    /// <summary>Every directory entry has to address bytes that exist. A truncated or overlapping
    /// blob is the classic hand-rolled-.ico bug: the file opens, the shell picks the broken frame,
    /// and the window shows nothing. Also checks the blobs start after the directory and tile the
    /// rest of the file exactly, so no frame is silently dropped or double-counted.</summary>
    [Fact]
    public void EveryEntryAddressesBytesInsideTheFile()
    {
        var bytes = WriteShipped();
        int count = BitConverter.ToUInt16(bytes, 4);
        int expectedOffset = IconDirSize + (IconDirEntrySize * count);

        for (int i = 0; i < count; i++)
        {
            int entry = IconDirSize + (IconDirEntrySize * i);
            int length = BitConverter.ToInt32(bytes, entry + 8);
            int offset = BitConverter.ToInt32(bytes, entry + 12);
            _out.WriteLine($"entry {i}: {bytes[entry]}x{bytes[entry + 1]} at {offset} for {length} bytes");

            Assert.True(length > 0, $"entry {i} declares an empty frame");
            Assert.Equal(expectedOffset, offset);
            Assert.True(offset + length <= bytes.Length,
                $"entry {i} runs {offset + length - bytes.Length} bytes past the end of the file");
            expectedOffset += length;
        }

        Assert.Equal(bytes.Length, expectedOffset);
    }

    /// <summary>The one field an .ico cannot express directly: 256 does not fit in a byte, so the
    /// format spells it 0. Writing 256 truncates to 0 by accident and writing 255 is a different
    /// icon — either way the shell reads the entry as 256, so this is asserted alongside the
    /// decoded frame size in <see cref="RoundTripsThroughWicAtTheExactFrameSizes"/>.</summary>
    [Fact]
    public void TheTwoHundredAndFiftySixPixelEntryEncodesItsDimensionAsZero()
    {
        var bytes = WriteShipped();
        int count = BitConverter.ToUInt16(bytes, 4);
        int index = AppIconArt.FrameSizes.ToList().IndexOf(256);
        Assert.InRange(index, 0, count - 1);

        int entry = IconDirSize + (IconDirEntrySize * index);
        _out.WriteLine($"entry {index} dimension bytes = {bytes[entry]},{bytes[entry + 1]}");
        Assert.Equal(0, bytes[entry]);
        Assert.Equal(0, bytes[entry + 1]);

        // Every other entry spells its size literally.
        for (int i = 0; i < count; i++)
        {
            if (i == index) continue;
            int other = IconDirSize + (IconDirEntrySize * i);
            Assert.Equal(AppIconArt.FrameSizes[i], bytes[other]);
            Assert.Equal(AppIconArt.FrameSizes[i], bytes[other + 1]);
        }
    }

    /// <summary>WPF's path. Every authored frame comes back at exactly its authored size — proving
    /// the dimension bytes, the offsets and the per-frame encodings all agree.</summary>
    [Fact]
    public void RoundTripsThroughWicAtTheExactFrameSizes()
    {
        using var stream = new MemoryStream(WriteShipped());
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

        foreach (var frame in decoder.Frames)
            _out.WriteLine($"WIC frame {frame.PixelWidth}x{frame.PixelHeight} {frame.Format}");

        Assert.Equal(AppIconArt.FrameSizes, decoder.Frames.Select(f => f.PixelWidth).OrderBy(w => w).ToArray());
        Assert.All(decoder.Frames, f => Assert.Equal(f.PixelWidth, f.PixelHeight));
    }

    /// <summary>System.Drawing's path, and the strictest reader the hand-assembled DIBs will meet.
    /// Each small frame is selected by size and rasterised. The bitmap's SIZE is asserted, not just
    /// that it loaded: a DIB whose header lies about its height silently comes back as the wrong
    /// frame or half of one, which is how a stride or mask mistake actually presents.</summary>
    [Fact]
    public void SmallFramesSurviveSystemDrawingsRasteriser()
    {
        var bytes = WriteShipped();

        foreach (int size in AppIconArt.FrameSizes.Where(s => s <= IcoWriter.MaxDibSizePx))
        {
            using var stream = new MemoryStream(bytes);
            using var icon = new Icon(stream, size, size);
            using var raster = icon.ToBitmap();

            var centre = raster.GetPixel(raster.Width / 2, raster.Height / 2);
            _out.WriteLine($"System.Drawing {size}px -> {icon.Width}x{icon.Height}, "
                + $"raster {raster.Width}x{raster.Height}, centre {centre}");

            Assert.Equal(size, icon.Width);
            Assert.Equal(size, raster.Width);
            Assert.Equal(size, raster.Height);
            Assert.Equal(255, centre.A);
        }
    }

    /// <summary>The cause behind the symptom above, pinned directly at the bytes: frames up to
    /// <see cref="IcoWriter.MaxDibSizePx"/> begin with a 40-byte BITMAPINFOHEADER, and the 256
    /// begins with the PNG signature. A DIB at 256 would be ~270 KB, which is why the largest frame
    /// keeps the compression that the small ones cannot afford to use.
    ///
    /// Both paths are asserted to have actually been taken. Everything below is expressed against
    /// <see cref="IcoWriter.MaxDibSizePx"/>, so a writer that stored every frame as PNG and moved
    /// the threshold to match would otherwise satisfy this test while shipping the v2.0.1 bug.</summary>
    [Fact]
    public void SmallFramesAreStoredUncompressedAndOnlyTheLargestIsPng()
    {
        var bytes = WriteShipped();
        int count = BitConverter.ToUInt16(bytes, 4);
        byte[] pngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G'];
        int dibs = 0, pngs = 0;

        for (int i = 0; i < count; i++)
        {
            int entry = IconDirSize + (IconDirEntrySize * i);
            int offset = BitConverter.ToInt32(bytes, entry + 12);
            int size = AppIconArt.FrameSizes[i];
            bool isPng = bytes.Skip(offset).Take(4).SequenceEqual(pngSignature);
            int headerSize = BitConverter.ToInt32(bytes, offset);

            _out.WriteLine($"{size}px: {(isPng ? "PNG" : $"DIB(biSize={headerSize})")}");

            if (size <= IcoWriter.MaxDibSizePx)
            {
                Assert.False(isPng, $"{size}px is PNG-compressed — System.Drawing will throw on it");
                Assert.Equal(40, headerSize);
                dibs++;
            }
            else
            {
                Assert.True(isPng, $"{size}px is an uncompressed DIB — needlessly large");
                pngs++;
            }
        }

        _out.WriteLine($"{dibs} uncompressed frame(s), {pngs} PNG frame(s)");
        Assert.True(dibs > 0, "nothing was stored uncompressed — every frame is a System.Drawing trap");
        Assert.True(pngs > 0, "the 256px frame was stored as a ~270 KB DIB");
    }

    /// <summary>A DIB frame's declared height is doubled: the format stacks the XOR colour bitmap
    /// and the AND mask into one BITMAPINFOHEADER. Getting this wrong produces an icon that decodes
    /// as its own top half, which is the kind of thing that only shows up on a user's desktop.</summary>
    [Fact]
    public void DibFramesDeclareTheDoubledHeightTheAndMaskRequires()
    {
        var bytes = WriteShipped();
        int count = BitConverter.ToUInt16(bytes, 4);

        for (int i = 0; i < count; i++)
        {
            int size = AppIconArt.FrameSizes[i];
            if (size > IcoWriter.MaxDibSizePx) continue;

            int offset = BitConverter.ToInt32(bytes, IconDirSize + (IconDirEntrySize * i) + 12);
            int width = BitConverter.ToInt32(bytes, offset + 4);
            int height = BitConverter.ToInt32(bytes, offset + 8);
            int planes = BitConverter.ToUInt16(bytes, offset + 12);
            int bitCount = BitConverter.ToUInt16(bytes, offset + 14);
            int compression = BitConverter.ToInt32(bytes, offset + 16);

            _out.WriteLine($"{size}px DIB: {width}x{height}, {planes} plane(s), {bitCount}bpp, "
                + $"compression {compression}");
            Assert.Equal(size, width);
            Assert.Equal(size * 2, height);
            Assert.Equal(1, planes);
            Assert.Equal(32, bitCount);
            Assert.Equal(0, compression); // BI_RGB
        }
    }

    /// <summary>The rounded tile's transparent corners have to survive the round trip. A 32bpp icon
    /// carries them twice — in the alpha channel and in the AND mask — and a writer that filled the
    /// mask with zeros would still pass every structural check above while the corners came back
    /// opaque through the legacy path.</summary>
    [Fact]
    public void TransparentCornersSurviveTheRoundTrip()
    {
        var bytes = WriteShipped();

        foreach (int size in AppIconArt.FrameSizes.Where(s => s <= IcoWriter.MaxDibSizePx))
        {
            using var stream = new MemoryStream(bytes);
            using var icon = new Icon(stream, size, size);
            using var raster = icon.ToBitmap();

            var corner = raster.GetPixel(0, 0);
            _out.WriteLine($"{size}px corner after round trip = {corner}");
            Assert.Equal(0, corner.A);
        }
    }

    /// <summary>Inputs an .ico cannot represent. Each would otherwise produce a file that opens and
    /// then misbehaves, so they fail at the writer instead.</summary>
    [Fact]
    public void RejectsAnEmptyFrameList() =>
        Assert.Throws<ArgumentException>(() => IcoWriter.Write([]));

    [Fact]
    public void RejectsANonSquareFrame()
    {
        using var oblong = new Bitmap(32, 16);
        Assert.Throws<ArgumentException>(() => IcoWriter.Write([oblong]));
    }

    [Fact]
    public void RejectsAFrameLargerThanTheFormatCanAddress()
    {
        using var huge = new Bitmap(257, 257);
        Assert.Throws<ArgumentException>(() => IcoWriter.Write([huge]));
    }
}
