using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AorinEQ.Core;

/// <summary>Packs bitmap frames into a real multi-size .ico byte stream: an ICONDIR, one
/// ICONDIRENTRY per frame, then the frame data.
///
/// WHY THIS EXISTS RATHER THAN A LIBRARY CALL. .NET can read an .ico and can save a PNG, but it has
/// no API that writes a multi-size icon, so the container is assembled by hand here.
///
/// WHY THE SMALL FRAMES ARE UNCOMPRESSED. The format has allowed PNG-compressed frames since Vista
/// and v2.0.1 used them for EVERY size, which the project ledger records as the cause of
/// <c>"Requested range extends past the end of the array"</c> out of System.Drawing's
/// <c>Icon.ToBitmap</c>/<c>Graphics.DrawIcon</c>. Worth being precise, because it was measured
/// again when this writer was built: that throw does NOT reproduce on .NET 8 — .NET Core's
/// <c>Icon.ToBitmap</c> sniffs the PNG signature and hands the frame to the image decoder, and both
/// the old all-PNG icon and a deliberately all-PNG rebuild of this one rasterise cleanly. It is a
/// .NET Framework-era failure that the runtime has since fixed.
///
/// The uncompressed layout is kept anyway, on narrower grounds than "otherwise it throws":
/// BITMAPINFOHEADER + 32bpp BGRA + AND mask is what every icon toolchain emits at these sizes and
/// what the oldest consumers (and .NET Framework tooling, which this repo's users may still point
/// at the shipped exe) can read without help. It costs ~26 KB. Only frames above
/// <see cref="MaxDibSizePx"/> are PNG, where the trade reverses hard — a 256 DIB is ~270 KB against
/// ~9 KB of PNG — and where nothing but the shell and WIC ever looks.
///
/// This type only encodes. <see cref="AppIconArt"/> draws the frames and <c>tools/AppIconGen</c>
/// is the command that runs both.</summary>
public static class IcoWriter
{
    /// <summary>The largest frame stored uncompressed. Everything above it is PNG. 64 is where the
    /// two costs cross: a DIB at 64px is 17 KB (fine), at 256px it is 270 KB (not).</summary>
    public const int MaxDibSizePx = 64;

    /// <summary>The largest dimension an ICONDIRENTRY can address. 256 is spelled 0.</summary>
    private const int MaxIconSizePx = 256;

    private const int IconDirSize = 6;
    private const int IconDirEntrySize = 16;
    private const int BitmapInfoHeaderSize = 40;

    /// <summary>Encodes <paramref name="frames"/> into the bytes of an .ico file. Frames are stored
    /// in the order given — Windows picks by size, not by position, but a sorted directory is what
    /// every other tool writes and what makes a hex dump readable.</summary>
    /// <exception cref="ArgumentException">A frame is non-square, outside 1..256, or the list is
    /// empty or longer than the directory's 16-bit count. Each of these produces a file that opens
    /// and then misbehaves, so they fail here instead of on a user's desktop.</exception>
    public static byte[] Write(IReadOnlyList<Bitmap> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0)
            throw new ArgumentException("an .ico needs at least one frame", nameof(frames));
        if (frames.Count > ushort.MaxValue)
            throw new ArgumentException(
                $"{frames.Count} frames exceeds the directory's 16-bit count", nameof(frames));

        var blobs = new byte[frames.Count][];
        for (int i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            if (frame.Width != frame.Height)
                throw new ArgumentException(
                    $"frame {i} is {frame.Width}x{frame.Height}; .ico frames are square", nameof(frames));
            if (frame.Width < 1 || frame.Width > MaxIconSizePx)
                throw new ArgumentException(
                    $"frame {i} is {frame.Width}px; .ico dimensions are 1..{MaxIconSizePx}", nameof(frames));

            blobs[i] = frame.Width <= MaxDibSizePx ? EncodeDib(frame) : EncodePng(frame);
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        // ICONDIR
        w.Write((ushort)0);              // reserved
        w.Write((ushort)1);              // type: 1 = icon (2 would make it a cursor)
        w.Write((ushort)frames.Count);

        // ICONDIRENTRY per frame. The blobs follow the whole directory, back to back.
        int offset = IconDirSize + (IconDirEntrySize * frames.Count);
        for (int i = 0; i < frames.Count; i++)
        {
            int size = frames[i].Width;
            byte dimension = (byte)(size == MaxIconSizePx ? 0 : size); // 0 means 256
            w.Write(dimension);
            w.Write(dimension);
            w.Write((byte)0);            // colours in the palette: 0 = not paletted
            w.Write((byte)0);            // reserved
            w.Write((ushort)1);          // colour planes
            w.Write((ushort)32);         // bits per pixel
            w.Write(blobs[i].Length);
            w.Write(offset);
            offset += blobs[i].Length;
        }

        foreach (var blob in blobs)
            w.Write(blob);

        w.Flush();
        return ms.ToArray();
    }

    /// <summary>A frame as an uncompressed DIB: BITMAPINFOHEADER, the XOR colour bitmap, then the
    /// AND mask.
    ///
    /// Two things about this layout catch people out. The header's height is DOUBLED, because it
    /// describes the colour bitmap and the mask stacked together. And both are stored BOTTOM-UP,
    /// like every other Windows DIB.</summary>
    private static byte[] EncodeDib(Bitmap frame)
    {
        int width = frame.Width, height = frame.Height;
        byte[] pixels = ReadBgra(frame);
        int rowBytes = width * 4;

        // The mask is 1bpp with rows padded to a 4-byte boundary, as DIB rows always are.
        int maskStride = (width + 31) / 32 * 4;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        w.Write(BitmapInfoHeaderSize);
        w.Write(width);
        w.Write(height * 2);             // XOR bitmap + AND mask
        w.Write((ushort)1);              // planes
        w.Write((ushort)32);             // bpp
        w.Write(0);                      // BI_RGB — uncompressed, the whole point of this path
        w.Write(rowBytes * height);      // biSizeImage: the colour bitmap only
        w.Write(0);                      // pixels-per-metre, X: meaningless for an icon
        w.Write(0);                      // pixels-per-metre, Y
        w.Write(0);                      // colours used: 0 = all
        w.Write(0);                      // colours important: 0 = all

        for (int y = height - 1; y >= 0; y--)
            w.Write(pixels, y * rowBytes, rowBytes);

        // The AND mask sets a bit where the pixel is to be left alone — i.e. transparent. A 32bpp
        // icon carries transparency in its alpha channel too, and modern Windows uses that, but the
        // mask is what the legacy paths read and a mask of zeros makes the rounded tile's corners
        // come back square through them.
        var maskRow = new byte[maskStride];
        for (int y = height - 1; y >= 0; y--)
        {
            Array.Clear(maskRow);
            for (int x = 0; x < width; x++)
                if (pixels[(y * rowBytes) + (x * 4) + 3] == 0)
                    maskRow[x / 8] |= (byte)(0x80 >> (x % 8));
            w.Write(maskRow);
        }

        w.Flush();
        return ms.ToArray();
    }

    private static byte[] EncodePng(Bitmap frame)
    {
        using var ms = new MemoryStream();
        frame.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>The frame's pixels as tightly packed top-down BGRA — the byte order a DIB wants,
    /// with the padding a locked bitmap adds removed. Copied row by row against the reported
    /// stride rather than assuming it equals <c>width * 4</c>: GDI+ aligns scanlines, and guessing
    /// produces a sheared image on any width where the two differ.</summary>
    private static byte[] ReadBgra(Bitmap frame)
    {
        var rect = new Rectangle(0, 0, frame.Width, frame.Height);
        var data = frame.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = frame.Width * 4;
            var pixels = new byte[rowBytes * frame.Height];
            for (int y = 0; y < frame.Height; y++)
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * rowBytes, rowBytes);
            return pixels;
        }
        finally
        {
            frame.UnlockBits(data);
        }
    }
}
