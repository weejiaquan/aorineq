using ApoVolume.Core;
using Xunit;
using Xunit.Abstractions;

namespace ApoVolume.Tests;

public class PngHeaderTests : IDisposable
{
    private readonly string _dir;
    private readonly ITestOutputHelper _out;

    public PngHeaderTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), "apo-volume-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // Writes a minimal but REAL PNG (signature + IHDR + zero-data IDAT + IEND) with the
    // given dimensions. Enough for header parsing; not a renderable image (tests that
    // need pixels use synthetic alpha arrays instead, never decode).
    private static void WritePng(string path, int width, int height)
    {
        using var fs = File.Create(path);
        void W(byte[] b) => fs.Write(b, 0, b.Length);
        void BE(int v) => W(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
        uint Crc(byte[] data)
        {
            uint[] table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            uint crc = 0xFFFFFFFF;
            foreach (var b in data) crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFF;
        }
        void Chunk(string type, byte[] data)
        {
            BE(data.Length);
            var typeAndData = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            W(typeAndData);
            BE((int)Crc(typeAndData));
        }
        W(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        ihdr[0] = (byte)(width >> 24); ihdr[1] = (byte)(width >> 16); ihdr[2] = (byte)(width >> 8); ihdr[3] = (byte)width;
        ihdr[4] = (byte)(height >> 24); ihdr[5] = (byte)(height >> 16); ihdr[6] = (byte)(height >> 8); ihdr[7] = (byte)height;
        ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
        Chunk("IHDR", ihdr);
        Chunk("IDAT", new byte[] { 0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01 });
        Chunk("IEND", Array.Empty<byte>());
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(300, 100)]
    [InlineData(1920, 1080)]
    public void Read_round_trips_dimensions_for_valid_png(int width, int height)
    {
        var path = Path.Combine(_dir, "test.png");
        WritePng(path, width, height);

        var result = PngHeader.Read(path);
        _out.WriteLine($"wrote {width}x{height}, parsed: {(result is null ? "<null>" : $"{result.Value.Width}x{result.Value.Height}")}");

        Assert.NotNull(result);
        Assert.Equal(width, result!.Value.Width);
        Assert.Equal(height, result.Value.Height);
    }

    [Fact]
    public void Read_returns_null_for_truncated_file()
    {
        var path = Path.Combine(_dir, "truncated.png");
        WritePng(path, 300, 100);
        var fullBytes = File.ReadAllBytes(path);
        // Truncate to fewer than 24 bytes so the IHDR width/height fields are unreadable.
        var truncated = fullBytes.Take(16).ToArray();
        File.WriteAllBytes(path, truncated);

        var result = PngHeader.Read(path);
        _out.WriteLine($"truncated file ({truncated.Length} bytes) parsed: {(result is null ? "<null>" : "non-null!")}");

        Assert.Null(result);
    }

    [Fact]
    public void Read_returns_null_for_non_png_bytes()
    {
        var path = Path.Combine(_dir, "not-a-png.png");
        File.WriteAllBytes(path, System.Text.Encoding.ASCII.GetBytes("this is definitely not a png file, just plain text padding to be long enough"));

        var result = PngHeader.Read(path);
        _out.WriteLine($"non-PNG bytes parsed: {(result is null ? "<null>" : "non-null!")}");

        Assert.Null(result);
    }

    [Fact]
    public void Read_returns_null_for_missing_file()
    {
        var path = Path.Combine(_dir, "does-not-exist.png");
        var result = PngHeader.Read(path);
        _out.WriteLine($"missing file parsed: {(result is null ? "<null>" : "non-null!")}");
        Assert.Null(result);
    }
}
