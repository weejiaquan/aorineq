namespace AorinEQ.Core;

/// <summary>Why an artwork layer cannot be used.</summary>
public enum LayerProblem { None, NotFound, Unreadable, FramesDoNotDivide }

/// <summary>One artwork layer as its header describes it: the file, whether it is a GIF, and its
/// LOGICAL frame size - a sprite sheet's height divided by its declared frames, a GIF's logical
/// screen - or the problem, with the loader's own wording for it in <see cref="Error"/>.</summary>
public sealed record LayerProbe(string Path, bool IsGif, int LogicalWidth, int LogicalHeight,
    LayerProblem Problem, string? Error)
{
    public bool IsUsable => Problem == LayerProblem.None;
}

/// <summary>Reads artwork headers for the loader and the designer. .gif is a GIF, anything else
/// a PNG - the same rule the frame decoder uses.
///
/// An instance caches each file's header by full path, last-write time and length: the designer
/// re-derives the whole skin on every edit (every mouse move of a drag), and re-reading five
/// headers each time would be the slowest thing it does. A changed file has a new stamp and is
/// read again, so the cache can never answer for bytes that are no longer on disk.</summary>
public sealed class ImageProbe
{
    private readonly Dictionary<string, (DateTime Written, long Length, (int Width, int Height)? Size)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static bool IsGif(string path) =>
        System.IO.Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase);

    public static (int Width, int Height)? ReadHeader(string path) =>
        IsGif(path) ? GifHeader.Read(path) : PngHeader.Read(path);

    /// <summary>The file's pixel size, or null when it is missing or its header is not valid.</summary>
    public (int Width, int Height)? Size(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) return null;
        if (_cache.TryGetValue(file.FullName, out var hit)
            && hit.Written == file.LastWriteTimeUtc && hit.Length == file.Length)
            return hit.Size;

        var size = ReadHeader(file.FullName);
        _cache[file.FullName] = (file.LastWriteTimeUtc, file.Length, size);
        return size;
    }

    /// <summary>Probes the artwork at <paramref name="path"/>; <paramref name="layer"/> names it in
    /// the error the way the loader does ("empty", "airplay-full", "notice").</summary>
    public LayerProbe Layer(string path, string layer, int declaredFrames)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return new LayerProbe(path, IsGif(path), 0, 0, LayerProblem.NotFound,
                $"{System.IO.Path.GetFileName(path)} not found");
        return Evaluate(path, layer, declaredFrames, Size(path));
    }

    /// <summary>The verdict on a header already read. For a PNG sheet the declared frame count must
    /// divide the pixel height; a GIF ignores it, because its real frames come from the decoder.</summary>
    public static LayerProbe Evaluate(string path, string layer, int declaredFrames, (int Width, int Height)? size)
    {
        int frames = SkinClamps.Frames(declaredFrames);
        if (IsGif(path))
            return size is { } gif
                ? new LayerProbe(path, true, gif.Width, gif.Height, LayerProblem.None, null)
                : new LayerProbe(path, true, 0, 0, LayerProblem.Unreadable, $"{layer}.gif is not a valid GIF");

        if (size is not { } png)
            return new LayerProbe(path, false, 0, 0, LayerProblem.Unreadable, $"{layer}.png is not a valid PNG");
        if (png.Height % frames != 0)
            return new LayerProbe(path, false, 0, 0, LayerProblem.FramesDoNotDivide,
                $"{layer}.png height {png.Height} is not divisible by {layer}Frames {frames}");
        return new LayerProbe(path, false, png.Width, png.Height / frames, LayerProblem.None, null);
    }
}
