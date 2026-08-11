namespace ApoVolume.Core;

/// <summary>Minimal raw-byte GIF header parser: signature + logical screen size only (the same
/// role <see cref="PngHeader"/> plays for PNGs — frame data is decoded by the UI layer).</summary>
public static class GifHeader
{
    /// <summary>Returns (width, height) or null if the file is not a valid GIF87a/GIF89a header.</summary>
    public static (int Width, int Height)? Read(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length < 10) return null;

            var header = new byte[10];
            int offset = 0;
            while (offset < header.Length)
            {
                int read = fs.Read(header, offset, header.Length - offset);
                if (read == 0) return null;
                offset += read;
            }

            // "GIF87a" or "GIF89a"
            if (header[0] != 'G' || header[1] != 'I' || header[2] != 'F' || header[3] != '8'
                || (header[4] != '7' && header[4] != '9') || header[5] != 'a')
                return null;

            int width = header[6] | (header[7] << 8);   // little-endian, per GIF spec
            int height = header[8] | (header[9] << 8);
            if (width == 0 || height == 0)
                return null;
            return (width, height);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
