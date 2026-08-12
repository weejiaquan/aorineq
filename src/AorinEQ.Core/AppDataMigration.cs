namespace AorinEQ.Core;

/// <summary>ONE-TIME v3.0.0 rename migration of the per-user state directory:
/// <c>%APPDATA%\apo-volume</c> → <c>%APPDATA%\AorinEQ</c> (<see cref="ApoPaths.GetStateRoot"/>).
/// Runs at startup before anything reads settings.json; a no-op on every start after the first,
/// and on a machine that never had the old folder at all.
///
/// Deliberately item-by-item rather than one <see cref="Directory.Move"/> of the whole root. A
/// root move is all-or-nothing against a destination the app itself may already have created,
/// and a single locked file would lose the entire migration. Per item, the three invariants are:
/// an item already present at the destination is never overwritten (live state always wins), an
/// item is never deleted from the source unless its move actually succeeded, and the legacy root
/// is removed only once it is empty — so a partial run simply resumes on the next start.</summary>
public static class AppDataMigration
{
    /// <summary>The pre-v3.0.0 folder name. Referenced ONLY by this migration.</summary>
    public const string LegacyFolderName = "apo-volume";

    public static string LegacyRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyFolderName);

    /// <summary>Moves everything the app owns from <paramref name="legacyRoot"/> into
    /// <paramref name="root"/>. Never throws: an item that can't be moved (locked, denied) is
    /// left exactly where it is for the next start to retry.</summary>
    public static void Run(string legacyRoot, string root)
    {
        if (!Directory.Exists(legacyRoot)
            || string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(legacyRoot)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)),
                StringComparison.OrdinalIgnoreCase))
            return;

        // Snapshot before mutating: entries are moved OUT of the directory being listed, and
        // lazily enumerating a directory while it changes is undefined on Windows.
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(legacyRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (entries.Length > 0)
        {
            try
            {
                Directory.CreateDirectory(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return; // nowhere to move to; the legacy root stays untouched and usable
            }
        }

        foreach (var entry in entries)
        {
            var destination = Path.Combine(root, Path.GetFileName(entry));
            if (File.Exists(destination) || (Directory.Exists(destination) && !TryRemoveIfEmpty(destination)))
                continue; // live state wins; the legacy copy is kept, not merged and not deleted
            try
            {
                // Same volume, so both of these are renames: atomic, and a failure moves nothing.
                if (Directory.Exists(entry))
                    Directory.Move(entry, destination);
                else
                    File.Move(entry, destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        TryRemoveIfEmpty(legacyRoot);
    }

    /// <summary>Non-recursive delete — it succeeds only on an EMPTY directory, which is exactly
    /// the test both callers want. For the legacy root it means the root disappears only once
    /// everything it held really did move. For a destination it means an empty <c>skins</c> or
    /// <c>presets</c> placeholder (the app creates those on demand, and so does the test suite)
    /// steps aside instead of permanently blocking the real folder's move — an empty directory
    /// holds no state, so nothing is being overwritten. A destination with anything in it fails
    /// here and is left alone.</summary>
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
