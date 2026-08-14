namespace AorinEQ.Core;

/// <summary>Applies a downloaded update to the portable exe in place: the running
/// <c>AorinEQ.exe</c> is RENAMED to <c>AorinEQ.exe.old</c> (renaming an execution-locked
/// image is legal on NTFS — deleting or overwriting it is not) and the staged new exe moves
/// into the vacated path, so the exe's path normally never changes and every external reference
/// to it (protocol registration, autostart entries, shortcuts) stays valid. Split from
/// <see cref="UpdateChecker"/>: this class only moves files that already passed the download
/// gates.
///
/// WHEN <see cref="Apply"/> MAY BE CALLED, and it is not a style preference — v3.5.0 and earlier
/// called it the moment the download finished and kept running, which crashed the app. This build
/// publishes as a self-contained SINGLE FILE: the CLR reads bundled managed assemblies out of the
/// exe BY PATH, ON DEMAND, for the life of the process. Renaming the running image aside does NOT
/// give the process a private copy — the path it will read from now holds a different build, so
/// the next not-yet-loaded assembly is read at the old offsets and throws FileNotFoundException.
/// A user ran 12 hours that way and the app died the moment opening the tray menu needed an
/// assembly it had not loaded yet. So: stage to <see cref="StagedPathFor"/> while running, and
/// call <see cref="Apply"/> only as the process exits (see PendingUpdate).
///
/// The exception is v3.0.0's rename, where the running image is a pre-rename
/// <c>ApoVolume.exe</c> and the release ships <c>AorinEQ.exe</c>: see
/// <see cref="TargetPathFor"/>.</summary>
public static class UpdateApplier
{
    /// <summary>Size cap for the downloaded exe (current build is ~70 MB).</summary>
    public const long MaxExeBytes = 200 * 1024 * 1024;

    public static string OldPathFor(string exePath) => exePath + ".old";

    /// <summary>Where a verified download waits until shutdown. Beside the target for two
    /// reasons: the exit-time <see cref="Apply"/> is then a rename on one volume rather than a
    /// 74 MB copy across two (temp is not always on the system drive, and shutdown is the one
    /// moment the app must not hang), and a staged build abandoned by a crash is found by
    /// <see cref="TryDeleteStaged"/> on the next start. Named after the TARGET for the same
    /// reason <see cref="Apply"/> names the backup that way — see <see cref="TargetPathFor"/>.</summary>
    public static string StagedPathFor(string exePath) => TargetPathFor(exePath) + ".new";

    /// <summary>Drops a staged build left behind by a process that died before its shutdown swap.
    /// It is deliberately NOT applied at startup: this process is running from the very exe that
    /// swap would rename, which is exactly what v3.5.1 stopped doing. The next check downloads it
    /// again. False when the file is still locked, on the same terms as
    /// <see cref="TryDeleteOld"/>.</summary>
    public static bool TryDeleteStaged(string exePath)
    {
        try
        {
            File.Delete(StagedPathFor(exePath)); // no-op when absent
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

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
