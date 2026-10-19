namespace AorinEQ.Core;

/// <summary>The one guard every zip AorinEQ extracts runs an entry name through.
///
/// Two archive shapes use it: a shared skin (files at the root or one folder deep) and VB-Audio's
/// driver package (flat, and the installer needs every file beside it). Both build their output
/// path from the name this returns, never from the entry's own path, so an entry that climbs
/// ("..\"), roots ("\", "C:") or reaches a share is refused here rather than resolved somewhere
/// unexpected.</summary>
public static class ZipEntryName
{
    /// <summary>The entry's path segments with separators normalised and "." segments dropped, or
    /// null when the name is empty, rooted, drive- or share-qualified, or has a ".." segment.</summary>
    public static string[]? Segments(string fullName)
    {
        if (string.IsNullOrEmpty(fullName) || fullName.Contains(':') || fullName[0] is '/' or '\\')
            return null;
        var parts = fullName.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p != ".")
            .ToArray();
        return parts.Contains("..") ? null : parts;
    }

    /// <summary>The entry's name when it is a bare file name and nothing else — no separator of
    /// either kind, no "..", no drive, no root — or null. A directory entry ("folder/") is not a
    /// bare name either.</summary>
    public static string? FlatName(string fullName) =>
        !fullName.Contains("..", StringComparison.Ordinal)
        && Segments(fullName) is [var only]
        && only.Length == fullName.Length
            ? only
            : null;
}
