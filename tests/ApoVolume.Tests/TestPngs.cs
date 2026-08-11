namespace ApoVolume.Tests;

/// <summary>Writes a minimal but real PNG (signature + IHDR + tiny IDAT + IEND) for tests that
/// need files SkinLoader/PngHeader accept. Not renderable image data.</summary>
internal static class TestPngs
{
    public static void Write(string path, int width, int height)
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

    /// <summary>Writes a minimal GIF89a: header + logical screen descriptor + trailer. Enough
    /// for GifHeader (which only reads the header) — carries no decodable image data.</summary>
    public static void WriteGif(string path, int width, int height)
    {
        using var fs = File.Create(path);
        var bytes = new List<byte>();
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("GIF89a"));
        bytes.Add((byte)(width & 0xFF)); bytes.Add((byte)(width >> 8));    // little-endian
        bytes.Add((byte)(height & 0xFF)); bytes.Add((byte)(height >> 8));
        bytes.Add(0x00); // packed: no global color table
        bytes.Add(0x00); // background color index
        bytes.Add(0x00); // pixel aspect ratio
        bytes.Add(0x3B); // trailer
        fs.Write(bytes.ToArray(), 0, bytes.Count);
    }
}
