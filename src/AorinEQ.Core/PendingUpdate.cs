namespace AorinEQ.Core;

/// <summary>A downloaded, verified build waiting for the process to exit before it is swapped in.
///
/// It exists because the swap CANNOT happen while the app runs — see the timing contract on
/// <see cref="UpdateApplier"/> for why a single-file process that renames its own image aside
/// dies on its next lazy assembly load. Holding one of these changes nothing on disk: the running
/// exe stays exactly where the CLR expects it, and <see cref="TryApply"/> is called from
/// App.OnExit, after the last window is gone.
///
/// Single-entry: once applied (or discarded) it does nothing further, so a second teardown pass
/// cannot rename the freshly installed build aside as if it were the old one.</summary>
public sealed class PendingUpdate
{
    private int _consumed;

    public PendingUpdate(string runningExePath, string stagedExePath, string tagName)
    {
        RunningExePath = runningExePath;
        StagedExePath = stagedExePath;
        TagName = tagName;
    }

    /// <summary>The exe this process is running from — what gets renamed aside at exit.</summary>
    public string RunningExePath { get; }

    /// <summary>The verified new build, already sitting beside <see cref="RunningExePath"/>.</summary>
    public string StagedExePath { get; }

    /// <summary>The release this came from, e.g. <c>v3.5.1</c> — for the balloon and status line.</summary>
    public string TagName { get; }

    /// <summary>Performs the swap. Called from OnExit, where a throw would take the rest of the
    /// teardown with it and there is no handler above that could do anything useful — so failure
    /// is a return value, and on failure <see cref="UpdateApplier.Apply"/> has already rolled the
    /// running exe back under the name it was launched as.</summary>
    /// <param name="installedPath">Where the new build landed — what to relaunch. Differs from
    /// <see cref="RunningExePath"/> only on the swap that carries v3.0.0's rename.</param>
    /// <param name="error">A readable reason when this returns false.</param>
    public bool TryApply(out string? installedPath, out string? error)
    {
        installedPath = null;
        error = null;
        if (Interlocked.Exchange(ref _consumed, 1) != 0)
        {
            error = "the update was already applied.";
            return false;
        }

        try
        {
            installedPath = UpdateApplier.Apply(RunningExePath, StagedExePath);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Apply wraps what it expects; anything that reaches here (e.g. the staged file
            // vanished between download and shutdown) still must not escape into OnExit.
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Throws the staged build away — the user turned auto-update off, or a newer release
    /// superseded this one before the app exited.</summary>
    public void Discard()
    {
        Interlocked.Exchange(ref _consumed, 1);
        try
        {
            File.Delete(StagedExePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The next start's TryDeleteStaged sweeps it.
        }
    }
}
