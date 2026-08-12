namespace ApoVolume.Core;

/// <summary>Applies a downloaded update to the portable exe in place: the running
/// <c>ApoVolume.exe</c> is RENAMED to <c>ApoVolume.exe.old</c> (renaming an execution-locked
/// image is legal on NTFS — deleting or overwriting it is not) and the staged new exe moves
/// into the vacated path, so the exe's path never changes and every external reference to it
/// (protocol registration, autostart entries, shortcuts) stays valid. Split from
/// <see cref="UpdateChecker"/>: this class only moves files that already passed the download
/// gates.</summary>
public static class UpdateApplier
{
    /// <summary>Size cap for the downloaded exe (current build is ~70 MB).</summary>
    public const long MaxExeBytes = 200 * 1024 * 1024;

    public static string OldPathFor(string exePath) => exePath + ".old";

    /// <summary>The swap. On any failure after the running exe was renamed aside, it is renamed
    /// back — the install is never left without a working exe. Throws
    /// <see cref="InvalidOperationException"/> with a readable message on failure.</summary>
    public static void Apply(string exePath, string stagedExePath)
    {
        var oldPath = OldPathFor(exePath);
        try
        {
            // A leftover .old from a previous update whose cleanup never ran: it is not the
            // image of any running process by now (that process updated successfully and
            // exited), so deleting is safe. If it IS somehow locked, the delete throws and the
            // swap aborts before anything moved.
            if (File.Exists(oldPath))
                File.Delete(oldPath);
            File.Move(exePath, oldPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Couldn't stage the update: {ex.Message}", ex);
        }

        try
        {
            File.Move(stagedExePath, exePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                if (!File.Exists(exePath))
                    File.Move(oldPath, exePath); // roll back: the original exe returns to its path
            }
            catch (Exception rollbackEx) when (rollbackEx is IOException or UnauthorizedAccessException)
            {
            }
            throw new InvalidOperationException($"Couldn't apply the update: {ex.Message}", ex);
        }
    }

    /// <summary>Deletes the <c>.old</c> backup next to <paramref name="exePath"/>. True when it
    /// is gone (deleted or never existed); false when still locked — right after an update the
    /// backup IS the exiting previous process's image, so the caller retries later.</summary>
    public static bool TryDeleteOld(string exePath)
    {
        var oldPath = OldPathFor(exePath);
        try
        {
            File.Delete(oldPath); // no-op when absent
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Whether the process can create files in <paramref name="directory"/> — the gate
    /// between the silent in-place swap and the "click to open the release page" balloon (e.g.
    /// an exe parked in Program Files without elevation).</summary>
    public static bool CanWriteTo(string directory)
    {
        var probe = Path.Combine(directory, ".update-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
