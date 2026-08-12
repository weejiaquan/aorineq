namespace AorinEQ.Core;

/// <summary>Applies a downloaded update to the portable exe in place: the running
/// <c>AorinEQ.exe</c> is RENAMED to <c>AorinEQ.exe.old</c> (renaming an execution-locked
/// image is legal on NTFS — deleting or overwriting it is not) and the staged new exe moves
/// into the vacated path, so the exe's path normally never changes and every external reference
/// to it (protocol registration, autostart entries, shortcuts) stays valid. Split from
/// <see cref="UpdateChecker"/>: this class only moves files that already passed the download
/// gates.
///
/// The exception is v3.0.0's rename, where the running image is a pre-rename
/// <c>ApoVolume.exe</c> and the release ships <c>AorinEQ.exe</c>: see
/// <see cref="TargetPathFor"/>.</summary>
public static class UpdateApplier
{
    /// <summary>Size cap for the downloaded exe (current build is ~70 MB).</summary>
    public const long MaxExeBytes = 200 * 1024 * 1024;

    public static string OldPathFor(string exePath) => exePath + ".old";

    /// <summary>Where an update installs to: the release asset's own name, in the running exe's
    /// directory. Identical to <paramref name="exePath"/> for any normally-named install, so the
    /// path stays invariant as it always has. It differs exactly once — on the update that
    /// carries v3.0.0's rename — and taking the new name there is the point: landing v3 back at
    /// <c>ApoVolume.exe</c> would leave that machine running AorinEQ out of a file named after
    /// the app it replaced, forever. The single path change is reconciled on the next start,
    /// which re-registers both URL classes and re-points autostart at the running exe.</summary>
    public static string TargetPathFor(string exePath) =>
        Path.Combine(Path.GetDirectoryName(exePath)!, UpdateChecker.ExeAssetName);

    /// <summary>The swap. On any failure after the running exe was renamed aside, it is renamed
    /// back to the name it was launched as — the install is never left without a working exe.
    /// Throws <see cref="InvalidOperationException"/> with a readable message on failure.</summary>
    /// <returns>The path the new build now lives at — what the caller must relaunch. Only ever
    /// different from <paramref name="exePath"/> for the rename described on
    /// <see cref="TargetPathFor"/>.</returns>
    public static string Apply(string exePath, string stagedExePath)
    {
        var targetPath = TargetPathFor(exePath);
        // Named after the TARGET, not the running exe: after a renaming swap the successor's
        // startup cleanup looks for a .old beside ITS own name, and an ApoVolume.exe.old would
        // never be found there again.
        var oldPath = OldPathFor(targetPath);
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
            // overwrite: on the non-renaming path the target was just vacated, so this is a plain
            // move. On the renaming path it replaces anything stale already sitting at the new
            // name — the staged build has passed the sha256 gate and is the authority for it. A
            // target that is genuinely LOCKED still throws, and rolls back below.
            File.Move(stagedExePath, targetPath, overwrite: true);
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
        return targetPath;
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
