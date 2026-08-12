namespace AorinEQ.Core;

/// <summary>ONE-TIME v3.0.0 rename migration of the per-user state directory:
/// <c>%APPDATA%\apo-volume</c> → <c>%APPDATA%\AorinEQ</c> (<see cref="ApoPaths.GetStateRoot"/>).
/// Runs at startup before anything reads settings.json; a no-op on every start after the first,
/// and on a machine that never had the old folder at all.
///
/// A recursive MERGE, not one <see cref="Directory.Move"/> of the whole root. A root move is
/// all-or-nothing against a destination the app itself creates on demand, and a single locked
/// file would lose the entire migration. Resolving collisions at FILE granularity — the finest
/// level at which "which copy is live" is even a question — means a destination folder that
/// already exists, empty or not, never blocks the files underneath it.
///
/// Three invariants, per file:
/// <list type="bullet">
/// <item>a file already present at the destination is never overwritten — live state wins — and
/// its legacy copy is not destroyed either;</item>
/// <item>a file is removed from the source only when its move actually succeeded;</item>
/// <item>a directory is removed only once it is empty, so a partial run resumes next start.</item>
/// </list></summary>
public static class AppDataMigration
{
    /// <summary>The pre-v3.0.0 folder name. Referenced ONLY by this migration.</summary>
    public const string LegacyFolderName = "apo-volume";

    public static string LegacyRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyFolderName);

    /// <summary>Moves everything the app owns from <paramref name="legacyRoot"/> into
    /// <paramref name="root"/>. Never throws: anything that can't be moved (locked, denied) is
    /// left exactly where it is for the next start to retry.</summary>
    public static void Run(string legacyRoot, string root)
    {
        if (!Directory.Exists(legacyRoot) || SamePath(legacyRoot, root))
            return;
        Merge(legacyRoot, root);
    }

    /// <summary>Where a state FILE lives RIGHT NOW: under <paramref name="root"/> normally, but
    /// the legacy copy while that is still waiting to be migrated and no current one exists yet.
    ///
    /// This is what stops a migration that couldn't move (locked file, denied access) from
    /// becoming permanent data loss. Without it, a first v3 start that failed to move
    /// settings.json would load defaults, then persist THOSE to the new root — and the invariant
    /// above ("a file already at the destination is never overwritten") would keep the user's
    /// real settings orphaned in the legacy folder forever. Reading and writing the legacy copy
    /// until it can be moved makes the failure merely deferred instead.</summary>
    public static string ResolveFile(string root, string legacyRoot, string fileName)
    {
        var current = Path.Combine(root, fileName);
        if (File.Exists(current) || SamePath(root, legacyRoot))
            return current;
        var legacy = Path.Combine(legacyRoot, fileName);
        return File.Exists(legacy) ? legacy : current;
    }

    private static bool SamePath(string a, string b) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
        StringComparison.OrdinalIgnoreCase);

    /// <returns>true when <paramref name="source"/> was emptied and removed.</returns>
    private static bool Merge(string source, string destination)
    {
        // Snapshot before mutating: entries are moved OUT of the directory being listed, and
        // lazily enumerating a directory while it changes is undefined on Windows.
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (entries.Length > 0 && !TryCreateDirectory(destination))
            return false; // nowhere to move to; the source stays untouched and usable

        foreach (var entry in entries)
        {
            var target = Path.Combine(destination, Path.GetFileName(entry));
            try
            {
                if (Directory.Exists(entry))
                {
                    // Move the whole subtree in one rename when nothing is in the way (same
                    // volume, so it is O(1)); otherwise merge into what is already there rather
                    // than abandoning every file underneath it.
                    if (Directory.Exists(target) || File.Exists(target))
                        Merge(entry, target);
                    else
                        Directory.Move(entry, target);
                }
                else if (!File.Exists(target) && !Directory.Exists(target))
                {
                    File.Move(entry, target);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return TryRemoveIfEmpty(source);
    }

    private static bool TryCreateDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Non-recursive delete: it succeeds only on an EMPTY directory, which is exactly the
    /// test wanted here — a directory disappears only once everything it held really did move.
    /// Anything left behind (a collision, a locked file) keeps it alive for the next start.</summary>
    private static bool TryRemoveIfEmpty(string directory)
    {
        try
        {
            Directory.Delete(directory);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
