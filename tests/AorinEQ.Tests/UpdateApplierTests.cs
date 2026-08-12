using System.Security.AccessControl;
using System.Security.Principal;
using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

public class UpdateApplierTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apo-applier-test-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _out;

    public UpdateApplierTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string PathOf(string name) => Path.Combine(_dir, name);

    [Fact]
    public void Apply_renames_running_exe_aside_and_moves_staged_in()
    {
        var exe = PathOf("AorinEQ.exe");
        var staged = PathOf("staged.exe");
        File.WriteAllText(exe, "OLD BUILD");
        File.WriteAllText(staged, "NEW BUILD");

        // The real swap happens while the old exe is RUNNING — its file is open for execute.
        // FileShare.Read|Delete approximates that lock shape (a mapped image allows rename but
        // not delete/overwrite), and proves Apply never needs write access to the old file.
        using (File.Open(exe, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            UpdateApplier.Apply(exe, staged);
        }

        _out.WriteLine("exe now: " + File.ReadAllText(exe));
        Assert.Equal("NEW BUILD", File.ReadAllText(exe));
        Assert.Equal("OLD BUILD", File.ReadAllText(UpdateApplier.OldPathFor(exe)));
        Assert.False(File.Exists(staged)); // staged file was MOVED, not copied
    }

    [Fact]
    public void Apply_replaces_a_leftover_old_file()
    {
        var exe = PathOf("AorinEQ.exe");
        var staged = PathOf("staged.exe");
        File.WriteAllText(exe, "CURRENT");
        File.WriteAllText(staged, "NEWER");
        File.WriteAllText(UpdateApplier.OldPathFor(exe), "ANCIENT"); // failed cleanup from a previous update

        UpdateApplier.Apply(exe, staged);
        Assert.Equal("NEWER", File.ReadAllText(exe));
        Assert.Equal("CURRENT", File.ReadAllText(UpdateApplier.OldPathFor(exe)));
    }

    [Fact]
    public void Apply_rolls_back_when_the_staged_move_fails()
    {
        var exe = PathOf("AorinEQ.exe");
        var staged = PathOf("staged.exe");
        File.WriteAllText(exe, "OLD BUILD");
        File.WriteAllText(staged, "NEW BUILD");

        // Lock the staged file exclusively so its move into place fails after the rename-aside.
        using (File.Open(staged, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = Assert.Throws<InvalidOperationException>(() => UpdateApplier.Apply(exe, staged));
            _out.WriteLine("error: " + ex.Message);
        }

        // The original exe must be back in place — never left renamed aside.
        Assert.Equal("OLD BUILD", File.ReadAllText(exe));
        Assert.False(File.Exists(UpdateApplier.OldPathFor(exe)));
    }

    [Fact]
    public void TryDeleteOld_removes_the_backup_and_reports_absence_as_success()
    {
        var exe = PathOf("AorinEQ.exe");
        File.WriteAllText(UpdateApplier.OldPathFor(exe), "ANCIENT");
        Assert.True(UpdateApplier.TryDeleteOld(exe));
        Assert.False(File.Exists(UpdateApplier.OldPathFor(exe)));
        Assert.True(UpdateApplier.TryDeleteOld(exe)); // already gone: success, nothing to do
    }

    [Fact]
    public void TryDeleteOld_reports_false_while_the_old_exe_is_still_locked()
    {
        // Right after an update relaunch the .old file IS the still-exiting previous process's
        // image — delete fails until it exits, and the caller retries later.
        var exe = PathOf("AorinEQ.exe");
        var old = UpdateApplier.OldPathFor(exe);
        File.WriteAllText(old, "STILL RUNNING");
        using (File.Open(old, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(UpdateApplier.TryDeleteOld(exe));
        }
        Assert.True(UpdateApplier.TryDeleteOld(exe));
    }

    // ---- v3.0.0: the swap that also RENAMES the exe ----

    [Fact]
    public void TargetPathFor_is_always_the_release_asset_name_beside_the_running_exe()
    {
        Assert.Equal(PathOf(UpdateChecker.ExeAssetName), UpdateApplier.TargetPathFor(PathOf("AorinEQ.exe")));
        Assert.Equal(PathOf(UpdateChecker.ExeAssetName), UpdateApplier.TargetPathFor(PathOf("ApoVolume.exe")));
    }

    [Fact]
    public void Apply_returns_the_path_it_installed_to_when_the_name_did_not_change()
    {
        var exe = PathOf("AorinEQ.exe");
        var staged = PathOf("staged.exe");
        File.WriteAllText(exe, "OLD BUILD");
        File.WriteAllText(staged, "NEW BUILD");

        Assert.Equal(exe, UpdateApplier.Apply(exe, staged));
    }

    [Fact]
    public void Apply_installs_under_the_current_name_when_the_running_exe_still_has_the_old_one()
    {
        // The pre-v3.0.0 → v3.0.0 upgrade: the running image is ApoVolume.exe, the release ships
        // AorinEQ.exe. Landing the new build back at the old path would leave this machine
        // running v3 out of a file called ApoVolume.exe forever.
        var legacyExe = PathOf("ApoVolume.exe");
        var staged = PathOf("staged.exe");
        File.WriteAllText(legacyExe, "OLD BUILD");
        File.WriteAllText(staged, "NEW BUILD");

        string installed;
        using (File.Open(legacyExe, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            installed = UpdateApplier.Apply(legacyExe, staged);
        }

        _out.WriteLine("installed at: " + installed);
        Assert.Equal(PathOf("AorinEQ.exe"), installed);
        Assert.Equal("NEW BUILD", File.ReadAllText(installed));
        Assert.False(File.Exists(legacyExe)); // the old name is gone, not left as a decoy
        Assert.False(File.Exists(staged));
    }

    [Fact]
    public void The_backup_of_a_renamed_swap_is_named_after_the_NEW_exe_so_cleanup_finds_it()
    {
        // TryDeleteOld looks beside the RUNNING exe, and after the relaunch that is AorinEQ.exe.
        // A backup called ApoVolume.exe.old would sit there forever.
        var legacyExe = PathOf("ApoVolume.exe");
        var staged = PathOf("staged.exe");
        File.WriteAllText(legacyExe, "OLD BUILD");
        File.WriteAllText(staged, "NEW BUILD");

        var installed = UpdateApplier.Apply(legacyExe, staged);

        Assert.Equal("OLD BUILD", File.ReadAllText(UpdateApplier.OldPathFor(installed)));
        Assert.False(File.Exists(UpdateApplier.OldPathFor(legacyExe)));
        Assert.True(UpdateApplier.TryDeleteOld(installed));
        Assert.Equal(new[] { installed }, Directory.GetFiles(_dir));
    }

    [Fact]
    public void A_renamed_swap_replaces_a_stale_file_already_sitting_at_the_new_name()
    {
        // e.g. an earlier attempt that died between the two moves. The staged build has already
        // passed the sha256 gate, so it is the authority for that name.
        var legacyExe = PathOf("ApoVolume.exe");
        var staged = PathOf("staged.exe");
        File.WriteAllText(legacyExe, "OLD BUILD");
        File.WriteAllText(PathOf("AorinEQ.exe"), "STALE");
        File.WriteAllText(staged, "NEW BUILD");

        var installed = UpdateApplier.Apply(legacyExe, staged);

        Assert.Equal("NEW BUILD", File.ReadAllText(installed));
        Assert.Equal("OLD BUILD", File.ReadAllText(UpdateApplier.OldPathFor(installed)));
    }

    [Fact]
    public void A_renamed_swap_rolls_the_running_exe_back_to_its_own_name_on_failure()
    {
        var legacyExe = PathOf("ApoVolume.exe");
        var staged = PathOf("staged.exe");
        File.WriteAllText(legacyExe, "OLD BUILD");
        File.WriteAllText(staged, "NEW BUILD");

        using (File.Open(staged, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<InvalidOperationException>(() => UpdateApplier.Apply(legacyExe, staged));
        }

        // Back under the name the still-running process was launched as — not the new one.
        Assert.Equal("OLD BUILD", File.ReadAllText(legacyExe));
        Assert.False(File.Exists(PathOf("AorinEQ.exe")));
        Assert.False(File.Exists(UpdateApplier.OldPathFor(PathOf("AorinEQ.exe"))));
    }

    [Fact]
    public void CanWriteTo_true_for_a_writable_directory_and_false_for_missing()
    {
        Assert.True(UpdateApplier.CanWriteTo(_dir));
        Assert.False(UpdateApplier.CanWriteTo(PathOf("does-not-exist")));
        // The probe file must not linger.
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void CanWriteTo_false_when_writes_are_denied()
    {
        var denied = PathOf("denied");
        Directory.CreateDirectory(denied);
        var di = new DirectoryInfo(denied);
        var user = WindowsIdentity.GetCurrent().User!;
        var acl = di.GetAccessControl();
        var rule = new FileSystemAccessRule(user,
            FileSystemRights.CreateFiles | FileSystemRights.WriteData,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Deny);
        acl.AddAccessRule(rule);
        di.SetAccessControl(acl);
        try
        {
            var result = UpdateApplier.CanWriteTo(denied);
            _out.WriteLine($"deny-ACL dir writable: {result}");
            Assert.False(result);
        }
        finally
        {
            acl.RemoveAccessRule(rule);
            di.SetAccessControl(acl); // or the recursive cleanup delete fails
        }
    }
}
