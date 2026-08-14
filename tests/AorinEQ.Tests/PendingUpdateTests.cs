using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>The v3.5.1 regression suite. A downloaded update used to be swapped into place the
/// moment it finished downloading, while the process kept running — which is fatal for a
/// self-contained single-file build, because the CLR reads bundled assemblies out of the exe BY
/// PATH, ON DEMAND. Once the path holds a different build, the next not-yet-loaded assembly is
/// read from the wrong file at the old offsets and throws FileNotFoundException. A user hit
/// exactly that: an update applied at 08:13 killed the app 12 hours later, the moment opening the
/// tray menu needed an assembly it had not loaded yet.
///
/// <see cref="PendingUpdate"/> exists to keep the swap OFF the live process: the download is
/// staged, and the two moves happen at shutdown.</summary>
public class PendingUpdateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apo-pending-test-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _out;

    public PendingUpdateTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string PathOf(string name) => Path.Combine(_dir, name);

    private (string exe, string staged) Stage(string running = "OLD BUILD", string incoming = "NEW BUILD")
    {
        var exe = PathOf("AorinEQ.exe");
        File.WriteAllText(exe, running);
        var staged = UpdateApplier.StagedPathFor(exe);
        File.WriteAllText(staged, incoming);
        return (exe, staged);
    }

    // ---- THE REGRESSION: staging must not disturb the running exe ----

    [Fact]
    public void Holding_a_pending_update_leaves_the_running_exe_untouched()
    {
        var (exe, staged) = Stage();

        var pending = new PendingUpdate(exe, staged, "v9.9.9");

        // Everything the running process reads its own assemblies from must be exactly as it was.
        _out.WriteLine($"exe still reads: {File.ReadAllText(exe)}");
        Assert.Equal("OLD BUILD", File.ReadAllText(exe));
        Assert.False(File.Exists(UpdateApplier.OldPathFor(exe)));
        Assert.True(File.Exists(staged));
        Assert.Equal("v9.9.9", pending.TagName);
    }

    [Fact]
    public void The_staged_file_sits_beside_the_exe_so_the_shutdown_swap_is_a_rename()
    {
        // Staging into %TEMP% would make the exit-time move a 74 MB cross-volume COPY on any
        // machine whose temp dir is on another drive — at the one moment the app must not hang.
        // Beside the target it is a rename on the same volume.
        var exe = PathOf("AorinEQ.exe");
        var staged = UpdateApplier.StagedPathFor(exe);

        _out.WriteLine($"staged path: {staged}");
        Assert.Equal(_dir, Path.GetDirectoryName(staged));
        Assert.Equal(UpdateApplier.TargetPathFor(exe) + ".new", staged);
    }

    [Fact]
    public void The_staged_file_is_named_after_the_TARGET_so_a_renaming_swap_still_finds_it()
    {
        // Same reasoning as the .old backup: after v3.0.0's rename the successor looks beside its
        // OWN name, so an ApoVolume.exe.new would be orphaned.
        var legacy = PathOf("ApoVolume.exe");
        Assert.Equal(PathOf(UpdateChecker.ExeAssetName) + ".new", UpdateApplier.StagedPathFor(legacy));
    }

    // ---- the shutdown swap ----

    [Fact]
    public void TryApply_performs_the_swap_and_reports_where_the_new_build_landed()
    {
        var (exe, staged) = Stage();
        var pending = new PendingUpdate(exe, staged, "v9.9.9");

        // The real call happens as the process dies, with its own image still execute-locked.
        using (File.Open(exe, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            Assert.True(pending.TryApply(out var installed, out var error));
            _out.WriteLine($"installed: {installed} error: {error ?? "<none>"}");
            Assert.Equal(exe, installed);
            Assert.Null(error);
        }

        Assert.Equal("NEW BUILD", File.ReadAllText(exe));
        Assert.Equal("OLD BUILD", File.ReadAllText(UpdateApplier.OldPathFor(exe)));
        Assert.False(File.Exists(staged));
    }

    [Fact]
    public void TryApply_reports_a_renaming_swap_at_its_new_name()
    {
        var legacy = PathOf("ApoVolume.exe");
        File.WriteAllText(legacy, "OLD BUILD");
        var staged = UpdateApplier.StagedPathFor(legacy);
        File.WriteAllText(staged, "NEW BUILD");

        var pending = new PendingUpdate(legacy, staged, "v3.0.0");
        Assert.True(pending.TryApply(out var installed, out _));

        Assert.Equal(PathOf(UpdateChecker.ExeAssetName), installed);
        Assert.Equal("NEW BUILD", File.ReadAllText(installed!));
    }

    [Fact]
    public void TryApply_never_throws_when_the_swap_cannot_complete()
    {
        // OnExit has no exception handler above it that can do anything useful, and a throw there
        // would take the teardown with it. Failure must be a return value.
        var (exe, staged) = Stage();
        var pending = new PendingUpdate(exe, staged, "v9.9.9");

        using (File.Open(staged, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(pending.TryApply(out var installed, out var error));
            _out.WriteLine($"reported error: {error}");
            Assert.Null(installed);
            Assert.False(string.IsNullOrWhiteSpace(error));
        }

        // Rolled back: the install still has a working exe under the name it was launched as.
        Assert.Equal("OLD BUILD", File.ReadAllText(exe));
        Assert.False(File.Exists(UpdateApplier.OldPathFor(exe)));
    }

    [Fact]
    public void TryApply_reports_failure_rather_than_throwing_when_the_staged_file_vanished()
    {
        var (exe, staged) = Stage();
        var pending = new PendingUpdate(exe, staged, "v9.9.9");
        File.Delete(staged); // e.g. a cleaner swept it between download and shutdown

        Assert.False(pending.TryApply(out var installed, out var error));
        _out.WriteLine($"reported error: {error}");
        Assert.Null(installed);
        Assert.Equal("OLD BUILD", File.ReadAllText(exe)); // and the exe is still the working one
    }

    [Fact]
    public void TryApply_is_single_entry_so_a_second_shutdown_pass_cannot_swap_twice()
    {
        var (exe, staged) = Stage();
        var pending = new PendingUpdate(exe, staged, "v9.9.9");

        Assert.True(pending.TryApply(out _, out _));
        File.WriteAllText(staged, "SOMETHING ELSE"); // whatever is at that path later is not ours

        Assert.False(pending.TryApply(out var installed, out _));
        Assert.Null(installed);
        Assert.Equal("NEW BUILD", File.ReadAllText(exe));           // the first swap stands
        Assert.Equal("OLD BUILD", File.ReadAllText(UpdateApplier.OldPathFor(exe)));
    }

    // ---- discarding ----

    [Fact]
    public void Discard_removes_the_staged_build_and_leaves_the_install_alone()
    {
        var (exe, staged) = Stage();
        var pending = new PendingUpdate(exe, staged, "v9.9.9");

        pending.Discard();

        Assert.False(File.Exists(staged));
        Assert.Equal("OLD BUILD", File.ReadAllText(exe));
        Assert.False(pending.TryApply(out _, out _)); // discarded means discarded
    }

    [Fact]
    public void TryDeleteStaged_clears_a_stale_staged_build_and_reports_absence_as_success()
    {
        // A staged build outlives a process that crashed before its shutdown swap. It is not
        // applied on the next start — that start is itself running from the exe it would rename,
        // which is the very thing this release stops doing. It is simply dropped; the next check
        // downloads again.
        var exe = PathOf("AorinEQ.exe");
        File.WriteAllText(exe, "CURRENT");
        File.WriteAllText(UpdateApplier.StagedPathFor(exe), "ABANDONED");

        Assert.True(UpdateApplier.TryDeleteStaged(exe));
        Assert.False(File.Exists(UpdateApplier.StagedPathFor(exe)));
        Assert.True(UpdateApplier.TryDeleteStaged(exe)); // already gone: nothing to do
        Assert.Equal("CURRENT", File.ReadAllText(exe));
    }

    [Fact]
    public void TryDeleteStaged_reports_false_while_the_staged_file_is_locked()
    {
        var exe = PathOf("AorinEQ.exe");
        var staged = UpdateApplier.StagedPathFor(exe);
        File.WriteAllText(staged, "MID-DOWNLOAD");
        using (File.Open(staged, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(UpdateApplier.TryDeleteStaged(exe));
        }
        Assert.True(UpdateApplier.TryDeleteStaged(exe));
    }
}
