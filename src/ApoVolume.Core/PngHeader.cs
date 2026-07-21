namespace ApoVolume.Core;

/// <summary>Minimal raw-byte PNG header parser: signature + IHDR width/height only.</summary>
public static class PngHeader
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>Returns (width, height) or null if the file is not a valid PNG signature/IHDR.</summary>
    public static (int Width, int Height)? Read(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length < 24) return null;

            var header = new byte[24];
            int offset = 0;
            while (offset < header.Length)
            {
                int read = fs.Read(header, offset, header.Length - offset);
                if (read == 0) return null;
                offset += read;
            }

            for (int i = 0; i < Signature.Length; i++)
                if (header[i] != Signature[i]) return null;

            uint width = ((uint)header[16] << 24) | ((uint)header[17] << 16) | ((uint)header[18] << 8) | header[19];
            uint height = ((uint)header[20] << 24) | ((uint)header[21] << 16) | ((uint)header[22] << 8) | header[23];
            return ((int)width, (int)height);
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
