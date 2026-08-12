using AorinEQ.Core;

namespace AorinEQ.Tests;

/// <summary>Real directories, real files, real locks — the v3.0.0 %APPDATA% rename migration is
/// entirely a filesystem operation, so every assertion here is against the filesystem it
/// actually produced.</summary>
public class AppDataMigrationTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string _legacy;
    private readonly string _current;

    public AppDataMigrationTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "aorineq-tests-" + Guid.NewGuid().ToString("N"));
        _legacy = Path.Combine(_sandbox, "apo-volume");
        _current = Path.Combine(_sandbox, "AorinEQ");
        Directory.CreateDirectory(_legacy);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void WriteLegacyFile(string relativePath, string content)
    {
        var full = Path.Combine(_legacy, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public void MovesFilesAndDirectoryTreesToTheNewRoot()
    {
        WriteLegacyFile("settings.json", "{\"Percent\":60}");
        WriteLegacyFile("autoeq-index.md", "index");
        WriteLegacyFile(Path.Combine("skins", "seia-bar-shadow", "skin.json"), "{}");
        WriteLegacyFile(Path.Combine("presets", "HD650.txt"), "Preamp: -6.0 dB");

        AppDataMigration.Run(_legacy, _current);

        Assert.Equal("{\"Percent\":60}", File.ReadAllText(Path.Combine(_current, "settings.json")));
        Assert.Equal("index", File.ReadAllText(Path.Combine(_current, "autoeq-index.md")));
        Assert.Equal("{}", File.ReadAllText(
            Path.Combine(_current, "skins", "seia-bar-shadow", "skin.json")));
        Assert.Equal("Preamp: -6.0 dB", File.ReadAllText(Path.Combine(_current, "presets", "HD650.txt")));
        // Emptied of everything it owned, so it is gone rather than left as a confusing stub.
        Assert.False(Directory.Exists(_legacy));
    }

    [Fact]
    public void SecondRunChangesNothing()
    {
        WriteLegacyFile("settings.json", "first");
        AppDataMigration.Run(_legacy, _current);
        var afterFirst = Directory.GetFileSystemEntries(_current, "*", SearchOption.AllDirectories);

        AppDataMigration.Run(_legacy, _current);

        Assert.Equal(afterFirst, Directory.GetFileSystemEntries(_current, "*", SearchOption.AllDirectories));
        Assert.Equal("first", File.ReadAllText(Path.Combine(_current, "settings.json")));
    }

    [Fact]
    public void AnItemAlreadyAtTheDestinationIsNeverOverwrittenAndTheLegacyCopySurvives()
    {
        WriteLegacyFile("settings.json", "legacy");
        Directory.CreateDirectory(_current);
        File.WriteAllText(Path.Combine(_current, "settings.json"), "current");

        AppDataMigration.Run(_legacy, _current);

        // The live location wins outright — a migration must never silently replace newer state.
        Assert.Equal("current", File.ReadAllText(Path.Combine(_current, "settings.json")));
        // ...and the legacy copy is not destroyed either, so nothing is lost in the collision.
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(_legacy, "settings.json")));
        Assert.True(Directory.Exists(_legacy));
    }

    [Fact]
    public void DirectoriesAreMergedSoAPopulatedDestinationNeverBlocksTheFilesUnderIt()
    {
        // Collisions are resolved per FILE. A skins folder that already holds a skin the user
        // installed post-rename must not strand every skin they had before it.
        WriteLegacyFile(Path.Combine("skins", "legacy-skin", "skin.json"), "{}");
        WriteLegacyFile(Path.Combine("skins", "shared", "skin.json"), "legacy");
        Directory.CreateDirectory(Path.Combine(_current, "skins", "current-skin"));
        Directory.CreateDirectory(Path.Combine(_current, "skins", "shared"));
        File.WriteAllText(Path.Combine(_current, "skins", "shared", "skin.json"), "current");

        AppDataMigration.Run(_legacy, _current);

        Assert.Equal("{}", File.ReadAllText(Path.Combine(_current, "skins", "legacy-skin", "skin.json")));
        Assert.True(Directory.Exists(Path.Combine(_current, "skins", "current-skin")));
        // The one real collision keeps the live copy and keeps the legacy one too.
        Assert.Equal("current", File.ReadAllText(Path.Combine(_current, "skins", "shared", "skin.json")));
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(_legacy, "skins", "shared", "skin.json")));
        // ...and only the colliding file's branch survives in the legacy tree.
        Assert.False(Directory.Exists(Path.Combine(_legacy, "skins", "legacy-skin")));
        Assert.True(Directory.Exists(_legacy));
    }

    [Fact]
    public void ResolveFile_prefers_the_current_root_once_the_file_is_there()
    {
        Directory.CreateDirectory(_current);
        File.WriteAllText(Path.Combine(_current, "settings.json"), "current");
        WriteLegacyFile("settings.json", "legacy");

        Assert.Equal(Path.Combine(_current, "settings.json"),
            AppDataMigration.ResolveFile(_current, _legacy, "settings.json"));
    }

    [Fact]
    public void ResolveFile_keeps_using_the_legacy_file_while_its_move_is_still_pending()
    {
        // The data-loss guard: a settings.json that couldn't move must still be the file this
        // session READS AND WRITES. Otherwise the session boots on defaults, persists them to the
        // new root, and "never overwrite the destination" orphans the user's real state forever.
        WriteLegacyFile("settings.json", "the user's real state");

        var resolved = AppDataMigration.ResolveFile(_current, _legacy, "settings.json");

        Assert.Equal(Path.Combine(_legacy, "settings.json"), resolved);
        Assert.Equal("the user's real state", File.ReadAllText(resolved));
    }

    [Fact]
    public void ResolveFile_points_at_the_current_root_when_neither_copy_exists()
    {
        // A brand-new install writes to the new location, not to a legacy folder it never had.
        Assert.Equal(Path.Combine(_current, "settings.json"),
            AppDataMigration.ResolveFile(_current, _legacy, "settings.json"));
    }

    [Fact]
    public void A_locked_settings_file_survives_a_full_session_and_migrates_on_the_next_start()
    {
        // End-to-end of finding 1: lock it, run the migration, resolve, WRITE through the
        // resolved path the way a session would, then unlock and start again.
        WriteLegacyFile("settings.json", "{\"Percent\":60}");
        string resolved;
        using (new FileStream(Path.Combine(_legacy, "settings.json"),
                   FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            AppDataMigration.Run(_legacy, _current);
            resolved = AppDataMigration.ResolveFile(_current, _legacy, "settings.json");
            Assert.Equal(Path.Combine(_legacy, "settings.json"), resolved);
        }
        File.WriteAllText(resolved, "{\"Percent\":75}"); // the session persists a change

        AppDataMigration.Run(_legacy, _current);

        Assert.Equal("{\"Percent\":75}", File.ReadAllText(Path.Combine(_current, "settings.json")));
        Assert.False(Directory.Exists(_legacy));
    }

    [Fact]
    public void AnEmptyPlaceholderDirectoryAtTheDestinationDoesNotBlockTheMove()
    {
        // The app creates skins/ and presets/ on demand, so the new root can already contain
        // empty placeholders before the migration ever runs. Treating those as "live state wins"
        // would strand the user's real skins in the legacy folder forever.
        WriteLegacyFile(Path.Combine("skins", "seia-bar-shadow", "skin.json"), "{}");
        WriteLegacyFile(Path.Combine("presets", "HD650.txt"), "Preamp: -6.0 dB");
        Directory.CreateDirectory(Path.Combine(_current, "skins"));
        Directory.CreateDirectory(Path.Combine(_current, "presets"));

        AppDataMigration.Run(_legacy, _current);

        Assert.Equal("{}", File.ReadAllText(
            Path.Combine(_current, "skins", "seia-bar-shadow", "skin.json")));
        Assert.Equal("Preamp: -6.0 dB", File.ReadAllText(Path.Combine(_current, "presets", "HD650.txt")));
        Assert.False(Directory.Exists(_legacy));
    }

    [Fact]
    public void ALockedFileStaysPutAndEverythingElseStillMoves()
    {
        WriteLegacyFile("settings.json", "movable");
        WriteLegacyFile("locked.bin", "held");

        using (var hold = new FileStream(Path.Combine(_legacy, "locked.bin"),
                   FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            AppDataMigration.Run(_legacy, _current);
        }

        Assert.Equal("movable", File.ReadAllText(Path.Combine(_current, "settings.json")));
        // The failed item is untouched, not half-moved and not deleted...
        Assert.Equal("held", File.ReadAllText(Path.Combine(_legacy, "locked.bin")));
        Assert.False(File.Exists(Path.Combine(_current, "locked.bin")));
        // ...and the legacy root survives precisely because it still holds something.
        Assert.True(Directory.Exists(_legacy));

        // Once the lock clears, the next start finishes the job.
        AppDataMigration.Run(_legacy, _current);
        Assert.Equal("held", File.ReadAllText(Path.Combine(_current, "locked.bin")));
        Assert.False(Directory.Exists(_legacy));
    }

    [Fact]
    public void NoLegacyRootIsANoOpAndDoesNotCreateTheNewRoot()
    {
        Directory.Delete(_legacy);

        AppDataMigration.Run(_legacy, _current);

        Assert.False(Directory.Exists(_current));
    }

    [Fact]
    public void AnEmptyLegacyRootIsRemoved()
    {
        AppDataMigration.Run(_legacy, _current);

        Assert.False(Directory.Exists(_legacy));
    }

    [Fact]
    public void MigratingARootOntoItselfDoesNothing()
    {
        WriteLegacyFile("settings.json", "same");

        AppDataMigration.Run(_legacy, _legacy);

        Assert.Equal("same", File.ReadAllText(Path.Combine(_legacy, "settings.json")));
        Assert.True(Directory.Exists(_legacy));
    }

    [Fact]
    public void TheRealLegacyRootIsTheOldFolderNameBesideTheNewOne()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        Assert.Equal(Path.Combine(appData, "apo-volume"), AppDataMigration.LegacyRoot);
        // The pair the live migration actually runs on: same parent, only the leaf differs.
        Assert.Equal(Path.Combine(appData, "AorinEQ"), ApoPaths.GetStateRoot());
    }
}
