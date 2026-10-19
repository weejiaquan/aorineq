using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>The automatic endpoint repair — the only code in this project that writes to somebody
/// else's audio device.
///
/// WHAT RUNS HERE AND WHAT DOES NOT. Everything unprivileged: reading this machine's real endpoint
/// property stores, resolving Equalizer APO's real APO CLSIDs against its real installation, the
/// refusal rules, and the exact-restore arithmetic (against a real registry key this test owns
/// under HKCU, so no write is faked). The privileged end-to-end — remove this machine's real
/// registration, repair it, verify, restore, prove byte-identity — needs Administrators on HKLM
/// (measured: BUILTIN\Users has ReadKey only), and a test that needed elevation could not stand
/// down when it did not have it without a runtime skip-guard, which this repository bans. It is
/// therefore run in the release's live verification, elevated, once, against the user's real
/// device.</summary>
public class EapoRepairTests
{
    private readonly ITestOutputHelper _out;
    public EapoRepairTests(ITestOutputHelper output) => _out = output;

    // ---------------------------------------------------------------- this machine, for real

    [Fact]
    [Trait(Requires.Key, Requires.EqualizerApo)]
    public void Equalizer_APOs_own_class_ids_resolve_to_its_own_binary()
    {
        var install = EapoDetection.GetInstallPath();
        Assert.NotNull(install);

        var clsids = EapoEndpoint.ResolveClsids(install!);
        _out.WriteLine($"pre-mix  {clsids?.PreMix}  -> {EapoEndpoint.ServerPathFor(clsids?.PreMix ?? "")}");
        _out.WriteLine($"post-mix {clsids?.PostMix} -> {EapoEndpoint.ServerPathFor(clsids?.PostMix ?? "")}");
        Assert.NotNull(clsids);

        // The gate that makes writing them safe: each must be a registered in-process server whose
        // DLL lives inside the detected Equalizer APO install. A CLSID that resolves anywhere else
        // is the value that would leave an endpoint pointing at an effect that cannot load.
        foreach (var clsid in new[] { clsids!.PreMix, clsids.PostMix })
        {
            var server = EapoEndpoint.ServerPathFor(clsid);
            Assert.NotNull(server);
            Assert.StartsWith(install!, Path.GetFullPath(server!), StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(server));
        }
        Assert.NotEqual(clsids.PreMix, clsids.PostMix);
    }

    [Fact]
    [Trait(Requires.Key, Requires.EqualizerApo)]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void An_attached_endpoint_on_this_machine_is_exactly_what_the_repair_would_write()
    {
        // An endpoint that IS attached, asked for by name rather than assumed to be the default —
        // this machine has more than one playback device and only some of them are registered, so
        // "the default" made this assertion depend on which device happened to be selected.
        var guid = AnActiveEndpoint();
        Assert.NotNull(guid);
        var clsids = EapoEndpoint.ResolveClsids(EapoDetection.GetInstallPath()!)!;
        var fx = EapoEndpoint.ReadFxProperties(guid!);
        foreach (var v in fx)
            _out.WriteLine($"  {v.Name,-46} {v.Kind,-12} {string.Join(" | ", v.Data ?? [])}");

        // Both halves of the real condition, read straight off the machine.
        Assert.True(EapoDetection.HasChildApoRecord(guid!));
        Assert.True(EapoEndpoint.IsApoAttached(guid!, clsids));
        Assert.True(EapoDetection.IsActiveOnEndpoint(guid!));

        // And what the repair WOULD write is what is already there — the strongest available
        // check that this build replicates the Configurator rather than inventing a scheme.
        foreach (var expected in EapoEndpoint.RepairFxValues(clsids))
        {
            var actual = EapoEndpoint.Find(fx, expected.Name);
            Assert.NotNull(actual);
            Assert.Equal(expected.Kind, actual!.Kind);
            Assert.Equal(expected.Data, actual.Data);
        }
    }

    [Fact]
    [Trait(Requires.Key, Requires.EqualizerApo)]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void Equalizer_APOs_own_record_for_an_attached_device_is_the_shape_the_repair_writes()
    {
        var guid = AnActiveEndpoint();
        Assert.NotNull(guid);
        var actual = EapoEndpoint.ReadChildApos(guid!);
        Assert.NotNull(actual);
        foreach (var v in actual!)
            _out.WriteLine($"  {v.Name,-46} '{string.Join(" | ", v.Data ?? [])}'");

        foreach (var expected in EapoEndpoint.RepairChildApoValues())
        {
            var found = EapoEndpoint.Find(actual, expected.Name);
            Assert.NotNull(found);
            Assert.Equal(expected.Data, found!.Data);
        }
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void Reading_an_endpoint_that_does_not_exist_is_empty_not_an_error()
    {
        Assert.Empty(EapoEndpoint.ReadFxProperties("{deadbeef-0000-0000-0000-000000000000}"));
        Assert.Null(EapoEndpoint.ReadChildApos("{deadbeef-0000-0000-0000-000000000000}"));
        Assert.False(EapoDetection.IsActiveOnEndpoint("{deadbeef-0000-0000-0000-000000000000}"));
    }

    [Fact]
    public void An_unregistered_class_id_resolves_to_nothing()
    {
        Assert.Null(EapoEndpoint.ServerPathFor("{deadbeef-0000-0000-0000-000000000000}"));
    }

    [Fact]
    [Trait(Requires.Key, Requires.EqualizerApo)]
    public void A_class_id_registered_outside_the_install_is_refused()
    {
        // shell32 is registered and real, and is exactly the kind of value that must never be
        // accepted: resolvable, but not Equalizer APO's.
        Assert.Null(EapoEndpoint.ResolveClsids(Path.Combine(Path.GetTempPath(), "not-equalizer-apo")));
    }

    // ---------------------------------------------------------------- the refusal rules

    private static ApoClsids Clsids => new("{EACD2258-FCAC-4FF4-B36D-419E924A6D79}",
                                           "{EC1CC9CE-FAED-4822-828A-82A81A6F018F}");

    [Fact]
    public void A_bare_endpoint_is_repairable()
    {
        Assert.Null(EapoEndpoint.WhyNotRepairable([], Clsids));
        // The shape a driver reinstall leaves: the key is there, the effect slots are not.
        Assert.Null(EapoEndpoint.WhyNotRepairable(
            [RegValue.Str("{b725f130-47ef-101a-a5f1-02608c9eebac},10", "Something")], Clsids));
    }

    [Fact]
    public void An_endpoint_carrying_somebody_elses_effect_is_refused()
    {
        // A vendor APO (Realtek, Nahimic, Dolby) in any of the five slots. Equalizer APO CHAINS to
        // these; how it chains is not something to infer from one machine, so this is handed to
        // the Configurator instead of guessed at.
        foreach (var slot in EapoEndpoint.ChainedSlots)
        {
            var reason = EapoEndpoint.WhyNotRepairable(
                [RegValue.Str(slot, "{11111111-2222-3333-4444-555555555555}")], Clsids);
            _out.WriteLine($"{slot}: {reason}");
            Assert.NotNull(reason);
            Assert.Contains("Configurator", reason);
        }
    }

    [Fact]
    public void An_endpoint_already_carrying_Equalizer_APOs_own_effects_is_repairable()
    {
        // A half-finished repair, or one whose Child APOs record was lost: our own CLSIDs in the
        // slots must not read as "somebody else's effect" and block the fix.
        Assert.Null(EapoEndpoint.WhyNotRepairable(
        [
            RegValue.Str(EapoEndpoint.FxStreamEffectClsid, Clsids.PreMix.ToLowerInvariant()),
            RegValue.Str(EapoEndpoint.FxEndpointEffectClsid, Clsids.PostMix),
        ], Clsids));
    }

    [Fact]
    public void A_value_this_build_cannot_write_back_stops_the_repair()
    {
        // If it cannot be restored it must not be displaced. A REG_BINARY in the property store is
        // not something this code writes, so a backup containing one is not a backup.
        var reason = EapoEndpoint.WhyNotRepairable(
            [new RegValue("{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},99", "Unsupported:Binary", null)], Clsids);
        _out.WriteLine(reason);
        Assert.NotNull(reason);
        Assert.Contains("put back", reason);
    }

    [Fact]
    public void The_repair_only_ever_writes_the_values_it_declares()
    {
        var written = EapoEndpoint.RepairFxValues(Clsids).Select(v => v.Name).ToArray();
        Assert.Equal(EapoEndpoint.WrittenFxValues.OrderBy(x => x), written.OrderBy(x => x));
        // …and never the two legacy slots or the mode-effect slot, which Equalizer APO does not
        // take on this machine.
        Assert.DoesNotContain(EapoEndpoint.FxPreMixClsid, written);
        Assert.DoesNotContain(EapoEndpoint.FxPostMixClsid, written);
        Assert.DoesNotContain(EapoEndpoint.FxModeEffectClsid, written);
    }

    // ---------------------------------------------------------------- exact restore, real registry

    [Fact]
    public void Restoring_puts_back_what_was_there_and_removes_what_was_not()
    {
        // A REAL registry key this test owns, written and read through the real Registry API — the
        // same value kinds and the same "absent means delete" arithmetic the endpoint restore
        // uses, without needing HKLM.
        var path = @"Software\AorinEQ.Tests\" + Guid.NewGuid().ToString("N");
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(path)!;
        try
        {
            key.SetValue("kept", "original", Microsoft.Win32.RegistryValueKind.String);
            key.SetValue("modes", new[] { "a", "b" }, Microsoft.Win32.RegistryValueKind.MultiString);
            var before = Capture(key);
            _out.WriteLine("before: " + Describe(before));

            // The repair's shape: change one, add two.
            key.SetValue("kept", "changed", Microsoft.Win32.RegistryValueKind.String);
            key.SetValue("added", "new", Microsoft.Win32.RegistryValueKind.String);
            key.SetValue("added2", new[] { "x" }, Microsoft.Win32.RegistryValueKind.MultiString);
            Assert.NotEqual(Describe(before), Describe(Capture(key)));

            RestoreInto(key, before);

            var after = Capture(key);
            _out.WriteLine("after:  " + Describe(after));
            Assert.Equal(Describe(before), Describe(after));
            Assert.Empty(key.GetValueNames().Where(n => n is "added" or "added2"));
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
    }

    /// <summary>Mirrors EapoEndpoint's restore arithmetic against a key this test owns: every
    /// captured value written back, every value present now but not then deleted.</summary>
    private static void RestoreInto(Microsoft.Win32.RegistryKey key, IReadOnlyList<RegValue> captured)
    {
        var wanted = captured.Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in key.GetValueNames())
        {
            if (!wanted.Contains(name)) key.DeleteValue(name, throwOnMissingValue: false);
        }
        foreach (var value in captured)
        {
            if (value.Kind == RegValue.KindString)
                key.SetValue(value.Name, value.Data![0], Microsoft.Win32.RegistryValueKind.String);
            else if (value.Kind == RegValue.KindMultiString)
                key.SetValue(value.Name, value.Data!, Microsoft.Win32.RegistryValueKind.MultiString);
        }
    }

    private static IReadOnlyList<RegValue> Capture(Microsoft.Win32.RegistryKey key) =>
        key.GetValueNames().Select(n => key.GetValueKind(n) == Microsoft.Win32.RegistryValueKind.MultiString
            ? RegValue.Multi(n, (string[])key.GetValue(n)!)
            : RegValue.Str(n, (string)key.GetValue(n)!)).OrderBy(v => v.Name, StringComparer.Ordinal).ToList();

    private static string Describe(IReadOnlyList<RegValue> values) =>
        string.Join("; ", values.OrderBy(v => v.Name, StringComparer.Ordinal)
            .Select(v => $"{v.Name}={v.Kind}:{string.Join(",", v.Data ?? [])}"));

    // ---------------------------------------------------------------- the backup file

    [Fact]
    public void A_backup_round_trips_including_the_values_that_were_not_there()
    {
        var path = Path.Combine(Path.GetTempPath(), "aorineq-backup-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var backup = new EapoRepairBackup(
                "{11111111-2222-3333-4444-555555555555}", EapoRepairBackup.Applying, DateTimeOffset.UtcNow,
                [RegValue.Str("a", "one"), RegValue.Multi("b", "x", "y"), RegValue.Absent("c")],
                null);
            backup.Save(path);
            _out.WriteLine(File.ReadAllText(path));

            var loaded = EapoRepairBackup.Load(path)!;
            Assert.Equal(backup.EndpointGuid, loaded.EndpointGuid);
            Assert.True(loaded.IsInterrupted);
            Assert.Null(loaded.ChildApoValues); // "Equalizer APO had no record" must survive as null
            Assert.Equal(3, loaded.FxValues.Length);
            Assert.Equal(RegValue.KindAbsent, loaded.FxValues[2].Kind);
            Assert.Equal(new[] { "x", "y" }, loaded.FxValues[1].Data);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void An_applied_backup_is_not_an_interrupted_one()
    {
        var backup = new EapoRepairBackup("{1}", EapoRepairBackup.Applied, DateTimeOffset.UtcNow, [], null);
        Assert.False(backup.IsInterrupted);
        Assert.True((backup with { Stage = EapoRepairBackup.Applying }).IsInterrupted);
    }

    [Fact]
    public void A_backup_that_cannot_describe_what_it_displaced_is_treated_as_absent()
    {
        // Better no undo than an undo that writes a partial state.
        var path = Path.Combine(Path.GetTempPath(), "aorineq-backup-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"Stage\":\"applying\"}"); // no endpoint, no values
            Assert.Null(EapoRepairBackup.Load(path));
            File.WriteAllText(path, "not json at all");
            Assert.Null(EapoRepairBackup.Load(path));
            File.Delete(path);
            Assert.Null(EapoRepairBackup.Load(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Each_endpoint_has_its_own_slot_somewhere_both_accounts_can_reach()
    {
        // Machine-wide, not per-user: the repair runs elevated and the undo reads it unelevated,
        // and a standard user typing an administrator's credentials makes those two different
        // profiles. One slot PER ENDPOINT, so repairing VB-Cable never touches the speakers' undo.
        var a = EapoRepair.BackupPathFor("{9C1AF7FF-0000-0000-0000-000000000001}");
        var aLower = EapoRepair.BackupPathFor("{9c1af7ff-0000-0000-0000-000000000001}");
        var b = EapoRepair.BackupPathFor("{9c1af7ff-0000-0000-0000-000000000002}");
        _out.WriteLine($"{a}\n{b}\nlegacy: {EapoRepair.BackupPath}");

        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            a, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(EapoRepair.StateDirectory, Path.GetDirectoryName(a));
        Assert.Equal(EapoRepair.StateDirectory, Path.GetDirectoryName(EapoRepair.BackupPath));
        Assert.Contains(ApoPaths.StateFolderName, a);
        Assert.Equal("repair-backup-{9c1af7ff-0000-0000-0000-000000000001}.json", Path.GetFileName(a));
        Assert.Equal(a, aLower);   // one device, one slot, however its GUID was cased
        Assert.NotEqual(a, b);
        Assert.Throws<ArgumentException>(() => EapoRepair.BackupPathFor("..\\..\\evil"));
    }

    // ---------------------------------------------------------------- refusals, without writing

    [Fact]
    public void Repair_refuses_a_device_id_that_is_not_a_device_id_before_anything_else()
    {
        var result = EapoRepair.Repair("not-a-guid",
            () => throw new InvalidOperationException("audio must not be restarted"),
            () => throw new InvalidOperationException("nothing should be verified"));
        Assert.Equal(EapoRepairOutcome.Refused, result.Outcome);
        _out.WriteLine(result.Message);
    }

    /// <summary>A backup slot THIS TEST owns, in its own temp directory.
    ///
    /// The tests used to drive the real machine-wide slot under ProgramData, assert the machine
    /// started with no backup, and delete the file afterwards. That is a test reaching into shared
    /// state it does not own: run the suite while a real repair is pending and it fails on the
    /// assert, or worse, deletes the only record of how to put a device back. Nothing here needs
    /// the real path — only that a slot is one file, which a temp file models exactly.</summary>
    private static string OwnSlot() =>
        Path.Combine(Path.GetTempPath(), "AorinEQ.Tests", Guid.NewGuid().ToString("N") + ".json");

    /// <summary>An endpoint Equalizer APO is ACTUALLY active on, chosen by asking rather than by
    /// assuming the default device is one.
    ///
    /// The difference is not cosmetic. <see cref="EapoRepair.Repair"/> short-circuits on an active
    /// endpoint and writes nothing; on an INACTIVE one it proceeds to a real repair. A test that
    /// passed "whatever is default" therefore attempted a genuine repair — backup written, registry
    /// written — on any machine whose default device happened not to be registered, which is
    /// precisely the machine this feature exists for.</summary>
    private static string? AnActiveEndpoint() => AudioEndpoint.GetRenderEndpoints()
        .Select(e => e.Guid)
        .FirstOrDefault(EapoDetection.IsActiveOnEndpoint);

    [Fact]
    [Trait(Requires.Key, Requires.EqualizerApo)]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void Repair_does_nothing_at_all_to_a_device_that_is_already_working()
    {
        // An endpoint that IS active, so the repair must stop before it writes, before it restarts
        // audio, and before it asks anything to be verified. The two callbacks throwing is the
        // assertion.
        var guid = AnActiveEndpoint();
        Assert.NotNull(guid); // Equalizer APO is installed here, so some endpoint must be attached
        var before = Describe(EapoEndpoint.ReadFxProperties(guid!));
        var dir = OwnStateDirectory();
        var slot = EapoRepair.BackupPathFor(guid!, dir);
        // Whatever the user's own machine-wide slot holds, this test must leave it exactly so —
        // asserted as "unchanged", not as "absent", because a pending real repair is legitimate.
        var realSlotBefore = File.Exists(EapoRepair.BackupPathFor(guid!));
        try
        {
            var result = EapoRepair.Repair(guid!,
                () => throw new InvalidOperationException("audio must not be restarted"),
                () => throw new InvalidOperationException("nothing should be verified"),
                directory: dir);

            _out.WriteLine(result.Message);
            Assert.Equal(EapoRepairOutcome.AlreadyActive, result.Outcome);
            Assert.Equal(before, Describe(EapoEndpoint.ReadFxProperties(guid!)));
            Assert.False(File.Exists(slot)); // no backup taken for a no-op
            Assert.Equal(realSlotBefore, File.Exists(EapoRepair.BackupPathFor(guid!)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    [Trait(Requires.Key, Requires.EqualizerApo)]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void The_button_is_not_offered_for_a_device_that_is_already_working()
    {
        // WhyNotAvailable is the unprivileged preview the UI uses; on this machine the default
        // device is bare-and-attached, so the repair itself is available (the UI additionally
        // hides it while the device is active — see SettingsWindow.SetEapoHealth).
        var guid = AudioEndpoint.EndpointGuid(AudioEndpoint.GetDefaultRenderEndpointId())!;
        _out.WriteLine("why not: " + (EapoRepair.WhyNotAvailable(guid) ?? "<available>"));
        Assert.Null(EapoRepair.WhyNotAvailable(guid));

        Assert.NotNull(EapoRepair.WhyNotAvailable(null));
        Assert.NotNull(EapoRepair.WhyNotAvailable("not-a-guid"));
    }

    // ------------------------------------------------- codex round 1: recovery-record protection

    /// <summary>A device id that names no endpoint. The guard has to refuse BEFORE anything is
    /// read or written, and using this machine's real default device would short-circuit on
    /// "already working" — or, if the guard ever regressed, aim a write at the user's own
    /// hardware. This one can only ever be refused.</summary>
    private const string NoSuchDevice = "{deadbeef-0000-0000-0000-000000000000}";

    // This drives EapoRepair.Repair past its "is Equalizer APO installed?" gate, which comes
    // BEFORE the slot guard it is actually about. Without the install it is refused for the wrong
    // reason and asserts nothing - so it is labelled, like every other test here that needs the
    // real thing to talk to. See Requires.cs. (An_interrupted_repair_blocks_only_its_own_endpoint,
    // below, is the same guard seen across two slots.)
    [Fact]
    [Trait(Requires.Key, Requires.EqualizerApo)]
    public void An_unfinished_repair_is_never_overwritten_by_a_new_one()
    {
        // Each device has its own slot. A repair of THIS device that never reached its
        // verification left the only description of a device nothing has checked since; starting
        // another repair of it over that record would destroy it. The user has to undo first.
        var guid = NoSuchDevice;
        var dir = OwnStateDirectory();
        var path = EapoRepair.BackupPathFor(guid, dir);
        try
        {
            new EapoRepairBackup(guid, EapoRepairBackup.Applying, DateTimeOffset.UtcNow,
                [RegValue.Absent("x")], null).Save(path);

            var result = EapoRepair.Repair(guid,
                () => throw new InvalidOperationException("audio must not be restarted"),
                () => throw new InvalidOperationException("nothing should be verified"),
                directory: dir);

            _out.WriteLine(result.Message);
            Assert.Equal(EapoRepairOutcome.Refused, result.Outcome);
            Assert.Contains("didn't finish", result.Message);
            // …and the record it protects is still exactly where it was.
            Assert.True(EapoRepairBackup.Load(path)!.IsInterrupted);
            Assert.Equal("x", EapoRepairBackup.Load(path)!.FxValues[0].Name);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_record_that_could_not_be_written_back_stops_the_repair_too()
    {
        // Equalizer APO's own record is held to the same rule as the endpoint's values: a kind
        // this build cannot write would make the REVERT throw, at the one moment it must not.
        Assert.Null(EapoEndpoint.WhyNotRestorable([RegValue.Str("PreMixChild", "")]));
        var reason = EapoEndpoint.WhyNotRestorable(
            [new RegValue("Version", "Unsupported:DWord", null)]);
        _out.WriteLine(reason);
        Assert.NotNull(reason);
        Assert.Contains("put back", reason);
    }

    // ------------------------------------------------- codex round 1: the verdict belongs to a run

    [Fact]
    public void A_verdict_from_an_earlier_run_is_never_shown_as_this_ones()
    {
        // The result file is written by an elevated process into a machine-wide folder, so the
        // unelevated app may not be able to delete it. Matching on the run token is what makes a
        // leftover harmless without needing to.
        var mine = EapoRepair.NewToken();
        var theirs = EapoRepair.NewToken();
        Assert.NotEqual(mine, theirs);
        Assert.True(EapoRepair.IsValidToken(mine));

        // Against a verdict file this test owns. The real one is written by the ELEVATED helper,
        // so an unelevated test may not be able to overwrite it at all — driving it was both a
        // mutation of shared state and a failure waiting for any machine that had used the feature.
        var path = OwnSlot();
        try
        {
            EapoRepair.SaveResult(new EapoRepairResult(
                EapoRepairOutcome.Repaired, "stale success from an earlier run", theirs), path);
            Assert.Null(EapoRepair.ReadResult(mine, path));
            Assert.Equal("stale success from an earlier run", EapoRepair.ReadResult(theirs, path)!.Message);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("../../../windows/system32/config")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void A_token_that_is_not_a_token_is_refused(string? token)
    {
        // The only value the elevated helper takes from its command line. It is never a path, a
        // device id or a command — but it is still checked, because "only echoed back" is a
        // property of today's code and this is the boundary.
        Assert.False(EapoRepair.IsValidToken(token));
    }

    [Fact]
    public void PowerShell_is_named_by_full_path_never_by_search_path()
    {
        // One of the two callers is already elevated, and a bare "powershell.exe" resolves through
        // the search path.
        var exe = AudioServices.BuildStartInfo(elevate: false).FileName;
        _out.WriteLine(exe);
        Assert.True(Path.IsPathRooted(exe));
        Assert.True(File.Exists(exe));
        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.System), exe, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_revert_that_cannot_bring_audio_back_keeps_its_record_and_says_so()
    {
        // Restoring the registry while the audio stack stays down is not a completed undo, and
        // deleting the backup there would throw away the only record of what to put back.
        var path = OwnSlot();
        try
        {
            // A backup describing an endpoint that does not exist: the restore is a no-op that
            // cannot throw, so the audio restart's result is the only thing deciding the outcome.
            var backup = new EapoRepairBackup("{deadbeef-0000-0000-0000-000000000000}",
                EapoRepairBackup.Applied, DateTimeOffset.UtcNow, [], null);
            backup.Save(path);

            var failed = EapoRepair.Undo(backup, restartAudio: () => false, backupPath: path);
            _out.WriteLine(failed.Message);
            Assert.Equal(EapoRepairOutcome.RevertedButAudioNotRestarted, failed.Outcome);
            Assert.Contains("restart your PC", failed.Message);
            Assert.True(File.Exists(path), "the record must survive a failed restart");

            var ok = EapoRepair.Undo(backup, restartAudio: () => true, backupPath: path);
            Assert.Equal(EapoRepairOutcome.Undone, ok.Outcome);
            Assert.False(File.Exists(path), "a completed undo consumes its record");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ------------------------------------------------- codex round 2: the round-1 fixes, reviewed

    [Fact]
    [Trait(Requires.Key, Requires.EqualizerApo)]
    public void A_record_nothing_can_use_never_locks_the_user_out_of_repairing()
    {
        // The round-1 guard refuses a repair while an unfinished backup exists. A backup that
        // CANNOT be written back protects nothing — and if it also blocked repairs, the refusal
        // would be permanent with no way out from the UI, because Undo cannot clear it either.
        var dir = OwnStateDirectory();
        var path = EapoRepair.BackupPathFor(NoSuchDevice, dir);
        try
        {
            var unusable = new EapoRepairBackup(NoSuchDevice, EapoRepairBackup.Applying,
                DateTimeOffset.UtcNow, [new RegValue("x", "Unsupported:DWord", null)], null);
            unusable.Save(path);
            Assert.False(EapoRepairBackup.Load(path)!.IsRestorable);

            // Not "Refused because an earlier repair didn't finish" — it gets past the guard.
            var result = EapoRepair.Repair(NoSuchDevice, () => true, () => true, directory: dir);
            _out.WriteLine(result.Message);
            Assert.DoesNotContain("didn't finish", result.Message);

            // And Undo says so plainly instead of half-writing it.
            var undo = EapoRepair.Undo(unusable,
                () => throw new InvalidOperationException("must not restart"), backupPath: path);
            _out.WriteLine(undo.Message);
            Assert.Equal(EapoRepairOutcome.FailedAndNotReverted, undo.Outcome);
            Assert.Contains("can't write back", undo.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_usable_record_still_blocks_and_a_restorable_one_is_recognised()
    {
        var usable = new EapoRepairBackup(NoSuchDevice, EapoRepairBackup.Applying, DateTimeOffset.UtcNow,
            [RegValue.Absent("a"), RegValue.Str("b", "x"), RegValue.Multi("c", "y")],
            [RegValue.Str("Version", "2")]);
        Assert.True(usable.IsRestorable);

        Assert.False((usable with { ChildApoValues = [new RegValue("Version", "Unsupported:DWord", null)] })
            .IsRestorable);
    }

    /// <summary>The shape that refused a real USB DAC (HiBy FC5, 2026-10-16): its FxProperties
    /// held PKEY_AudioEndpoint_Disable_SysFx as a REG_DWORD 0, which the capture could not record,
    /// so the repair refused the device. Captured and written back through the PRODUCT's own
    /// primitives against a real key, including a value with the top bit set.</summary>
    [Fact]
    public void A_dword_is_captured_and_written_back_exactly()
    {
        var path = @"Software\AorinEQ.Tests\" + Guid.NewGuid().ToString("N");
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(path)!;
        try
        {
            const string sysFx = "{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5";
            key.SetValue(sysFx, 0, Microsoft.Win32.RegistryValueKind.DWord);
            key.SetValue("high", unchecked((int)0xFFFFFFFEu), Microsoft.Win32.RegistryValueKind.DWord);

            var zero = EapoEndpoint.Capture(key, sysFx);
            var high = EapoEndpoint.Capture(key, "high");
            _out.WriteLine($"captured: {zero.Name}={zero.Kind}:{zero.Data![0]}  high={high.Kind}:{high.Data![0]}");
            Assert.Equal(RegValue.KindDWord, zero.Kind);
            Assert.Equal(["0"], zero.Data);
            Assert.Equal(["4294967294"], high.Data);
            Assert.True(zero.IsRestorable);
            Assert.Null(EapoEndpoint.WhyNotRestorable([zero, high]));

            // The repair's FxProperties preflight now passes the DAC's real shape.
            Assert.Null(EapoEndpoint.WhyNotRepairable([
                RegValue.Str(EapoEndpoint.FxAssociation, "{00000000-0000-0000-0000-000000000000}"),
                zero,
            ], Clsids));

            key.SetValue(sysFx, 1, Microsoft.Win32.RegistryValueKind.DWord);
            key.DeleteValue("high");
            EapoEndpoint.WriteValues(key, [zero, high]);

            Assert.Equal(Microsoft.Win32.RegistryValueKind.DWord, key.GetValueKind(sysFx));
            Assert.Equal(0, key.GetValue(sysFx));
            Assert.Equal(Microsoft.Win32.RegistryValueKind.DWord, key.GetValueKind("high"));
            Assert.Equal(unchecked((int)0xFFFFFFFEu), key.GetValue("high"));
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("4294967296")]
    [InlineData("0x10")]
    public void A_dword_that_could_not_be_written_back_is_not_restorable(string? data)
    {
        // A hand-edited or corrupted backup. Writing it back would throw mid-revert, so it must be
        // refused at the preflight instead.
        var value = new RegValue("x", RegValue.KindDWord, data is null ? null : [data]);
        _out.WriteLine($"data={data ?? "<null>"} restorable={value.IsRestorable}");
        Assert.False(value.IsRestorable);
        Assert.NotNull(EapoEndpoint.WhyNotRestorable([value]));
    }

    [Fact]
    public void An_expandable_string_keeps_its_kind_through_a_backup()
    {
        // REG_EXPAND_SZ and REG_SZ are indistinguishable once read. Writing one back as the other
        // changes how Windows expands it — so an exact undo has to carry the kind, and the
        // preflight must not pass a value it would then corrupt.
        var path = @"Software\AorinEQ.Tests\" + Guid.NewGuid().ToString("N");
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(path)!;
        try
        {
            key.SetValue("expandable", "%SystemRoot%\\x", Microsoft.Win32.RegistryValueKind.ExpandString);
            key.SetValue("plain", "%SystemRoot%\\x", Microsoft.Win32.RegistryValueKind.String);
            Assert.Equal(Microsoft.Win32.RegistryValueKind.ExpandString, key.GetValueKind("expandable"));

            var expandable = RegValue.Str("expandable", "%SystemRoot%\\x") with { Kind = RegValue.KindExpandString };
            Assert.True(expandable.IsRestorable);
            Assert.Null(EapoEndpoint.WhyNotRestorable([expandable]));
            Assert.NotEqual(RegValue.KindString, expandable.Kind);
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void The_confirmation_says_what_it_will_actually_do()
    {
        var text = EapoRepair.ConfirmationText;
        _out.WriteLine(text);
        Assert.Contains("administrator", text);   // there will be a UAC prompt
        Assert.Contains("put back", text);        // it is undoable automatically
        Assert.Contains("restarts", text);        // sound will stop for a moment
        Assert.Contains("Only the device", text); // nothing else is touched
    }

    [Fact]
    public void An_endpoints_property_store_is_written_with_SetValue_alone_not_KEY_WRITE()
    {
        // THE DEFECT THIS LOCKS DOWN, found by writing to a real endpoint elevated and not by any
        // amount of reading: Windows grants BUILTIN\Administrators exactly "SetValue, ReadKey" on
        // an endpoint's FxProperties — CreateSubKey and Delete are reserved for Audiosrv,
        // AudioEndpointBuilder and TrustedInstaller. Every convenient spelling of "open this for
        // writing" asks for KEY_WRITE, which INCLUDES CreateSubKey, so the open is refused by an
        // ACL that would have allowed every write the repair actually performs. It failed as
        // Administrator with "Access to the registry key ... is denied" and looked exactly like a
        // missing privilege.
        //
        // Reproduced here against a key this test owns, carrying the same grant: only SetValue and
        // QueryValues, inheritance off, so nothing else can quietly widen it.
        var path = @"Software\AorinEQ.Tests\" + Guid.NewGuid().ToString("N");
        using (var seed = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(path)!)
        {
            seed.SetValue("existing", "before");
            var security = seed.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (System.Security.AccessControl.RegistryAccessRule rule in
                     security.GetAccessRules(true, false, typeof(System.Security.Principal.NTAccount)))
                security.RemoveAccessRuleAll(rule);
            security.AddAccessRule(new System.Security.AccessControl.RegistryAccessRule(
                System.Security.Principal.WindowsIdentity.GetCurrent().User!,
                System.Security.AccessControl.RegistryRights.SetValue
                    | System.Security.AccessControl.RegistryRights.QueryValues
                    | System.Security.AccessControl.RegistryRights.ReadKey
                    | System.Security.AccessControl.RegistryRights.ChangePermissions,
                System.Security.AccessControl.AccessControlType.Allow));
            seed.SetAccessControl(security);
        }
        try
        {
            // What the product asks for: enough to set a value and to delete one, and no more.
            // ReadWriteSubTree is what the HANDLE may do, the rights mask is what is REQUESTED of
            // Windows — the two-argument overload requests the narrow mask but returns a wrapper
            // marked read-only, and SetValue then fails on a handle allowed to write. That is a
            // second way to get this wrong and it is asserted below.
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path,
                       Microsoft.Win32.RegistryKeyPermissionCheck.ReadWriteSubTree,
                       System.Security.AccessControl.RegistryRights.SetValue
                           | System.Security.AccessControl.RegistryRights.QueryValues))
            {
                Assert.NotNull(key);
                key!.SetValue("written", "after");                        // the repair's writes
                key.DeleteValue("existing", throwOnMissingValue: false);  // a backup's "was absent"
            }

            // ...and what it must NOT ask for. Both of these are the old code, and both are how
            // the repair failed on a real device: KEY_WRITE includes CreateSubKey, which is not
            // granted, so the OPEN itself is refused. Which refusal .NET raises depends on the
            // hive — UnauthorizedAccessException on the real HKLM endpoint, SecurityException
            // here — so the assertion is on the refusal, not on one spelling of it.
            static Exception Refused(Action act)
            {
                var ex = Record.Exception(act);
                Assert.True(ex is UnauthorizedAccessException or System.Security.SecurityException,
                    "expected the open to be refused, got: " + (ex?.ToString() ?? "no exception"));
                return ex!;
            }
            _out.WriteLine("OpenSubKey(writable) -> " + Refused(
                () => Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path, writable: true)).GetType().Name);
            _out.WriteLine("CreateSubKey(writable) -> " + Refused(
                () => Microsoft.Win32.Registry.CurrentUser.CreateSubKey(path, writable: true)).GetType().Name);

            // The second trap: the right mask, but the wrapper inherits the base key's read-only
            // permission check, so the open SUCCEEDS and the write fails afterwards.
            using (var readOnlyWrapper = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path,
                       System.Security.AccessControl.RegistryRights.SetValue
                           | System.Security.AccessControl.RegistryRights.QueryValues))
            {
                Assert.NotNull(readOnlyWrapper);
                Assert.Throws<UnauthorizedAccessException>(() => readOnlyWrapper!.SetValue("nope", "x"));
                _out.WriteLine("narrow mask without ReadWriteSubTree: opened, then refused the write");
            }

            using var check = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path)!;
            _out.WriteLine("values after the narrow write: " + string.Join(",", check.GetValueNames()));
            Assert.Equal("after", check.GetValue("written"));
            Assert.Null(check.GetValue("existing"));
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void A_backup_anything_unprivileged_could_have_written_is_not_trusted()
    {
        // The backup is INSTRUCTIONS: the elevated undo writes the values it names into the
        // endpoint it names. %ProgramData% grants BUILTIN\Users write by default, and on this
        // machine an unelevated process could overwrite the real backup file outright — which
        // turns one UAC prompt into an arbitrary write into HKLM. So the helper checks who could
        // have authored the file before it acts on it.
        var dir = Path.Combine(Path.GetTempPath(), "AorinEQ.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var open = Path.Combine(dir, "open.json");
        var locked = Path.Combine(dir, "locked.json");
        try
        {
            File.WriteAllText(open, "{}");
            File.WriteAllText(locked, "{}");

            // An ordinary file under an ordinary user-writable directory: refused.
            Assert.False(EapoRepair.IsTrustworthyStateFile(open));

            // The same file granting write to administrators and SYSTEM only: accepted.
            var security = new System.Security.AccessControl.FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[]
                     {
                         new System.Security.Principal.SecurityIdentifier(
                             System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
                         new System.Security.Principal.SecurityIdentifier(
                             System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
                     })
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    sid, System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.AccessControlType.Allow));
            new FileInfo(locked).SetAccessControl(security);
            Assert.True(EapoRepair.IsTrustworthyStateFile(locked));

            // A file that is not there is not trustworthy either — "nothing to undo" is a
            // different answer, and the caller distinguishes them.
            Assert.False(EapoRepair.IsTrustworthyStateFile(Path.Combine(dir, "absent.json")));
        }
        finally
        {
            foreach (var f in new[] { open, locked })
            {
                if (!File.Exists(f)) continue;
                var reset = new System.Security.AccessControl.FileSecurity();
                reset.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
                try { new FileInfo(f).SetAccessControl(reset); } catch (UnauthorizedAccessException) { }
                try { File.Delete(f); } catch (UnauthorizedAccessException) { }
            }
            try { Directory.Delete(dir, recursive: true); } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void Rewriting_the_backup_never_leaves_a_reader_with_half_a_record()
    {
        // applying -> applied rewrites the ONLY description of how to undo a repair that has by
        // then already happened. Truncating in place would mean a crash mid-write loses it, and
        // Load reports a truncated file as "nothing to undo". The write goes to a side file and
        // is moved into place, so a reader sees the old record or the new one.
        var path = OwnSlot();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var first = new EapoRepairBackup(NoSuchDevice, EapoRepairBackup.Applying,
                DateTimeOffset.UtcNow, [RegValue.Str("a", "1")], null);
            first.Save(path);
            Assert.Equal(EapoRepairBackup.Applying, EapoRepairBackup.Load(path)!.Stage);

            (first with { Stage = EapoRepairBackup.Applied }).Save(path);
            var reloaded = EapoRepairBackup.Load(path)!;
            Assert.Equal(EapoRepairBackup.Applied, reloaded.Stage);
            Assert.Equal("a", reloaded.FxValues[0].Name);
            Assert.False(reloaded.IsInterrupted);

            // And the side file is not left behind next to the record.
            Assert.False(File.Exists(path + ".new"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ------------------------------------------------- one slot per endpoint

    private const string DeviceA = "{aaaaaaaa-1111-2222-3333-444444444444}";
    private const string DeviceB = "{bbbbbbbb-1111-2222-3333-444444444444}";

    /// <summary>A state directory THIS TEST owns, in its own temp folder — never ProgramData.</summary>
    private static string OwnStateDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "AorinEQ.Tests", "state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static EapoRepairBackup RecordFor(string guid, string stage) =>
        new(guid, stage, DateTimeOffset.UtcNow, [RegValue.Absent("x")], null);

    [Fact]
    [Trait(Requires.Key, Requires.EqualizerApo)]
    public void An_interrupted_repair_blocks_only_its_own_endpoint()
    {
        // Review focus: a repair of A was interrupted (helper killed) while B holds its own undo.
        // A must refuse; B must get past the guard. B's slot is made read-only so its repair fails
        // at the backup save — the first step AFTER the guard — with no registry write at all.
        var dir = OwnStateDirectory();
        var slotA = EapoRepair.BackupPathFor(DeviceA, dir);
        var slotB = EapoRepair.BackupPathFor(DeviceB, dir);
        try
        {
            RecordFor(DeviceA, EapoRepairBackup.Applying).Save(slotA);
            RecordFor(DeviceB, EapoRepairBackup.Applied).Save(slotB);
            File.SetAttributes(slotB, FileAttributes.ReadOnly);

            var a = EapoRepair.Repair(DeviceA,
                () => throw new InvalidOperationException("audio must not be restarted"),
                () => throw new InvalidOperationException("nothing should be verified"),
                directory: dir);
            var b = EapoRepair.Repair(DeviceB,
                () => throw new InvalidOperationException("audio must not be restarted"),
                () => throw new InvalidOperationException("nothing should be verified"),
                directory: dir);
            _out.WriteLine($"A (interrupted slot): {a.Outcome} - {a.Message}");
            _out.WriteLine($"B (applied slot, other device interrupted): {b.Outcome} - {b.Message}");

            Assert.Equal(EapoRepairOutcome.Refused, a.Outcome);
            Assert.Contains("didn't finish", a.Message);
            Assert.True(EapoRepairBackup.Load(slotA)!.IsInterrupted);
            Assert.Equal(EapoRepairOutcome.Refused, b.Outcome);
            Assert.Contains("couldn't save a record", b.Message);  // past the guard, stopped before any write
            Assert.DoesNotContain("different playback device", b.Message);
        }
        finally
        {
            if (File.Exists(slotB)) File.SetAttributes(slotB, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Two_endpoints_are_undone_independently()
    {
        // Undo of fake devices with no captured values is a registry no-op (see
        // A_revert_that_cannot_bring_audio_back...), so the slot bookkeeping runs for real
        // without touching anybody's hardware.
        var dir = OwnStateDirectory();
        var slotA = EapoRepair.BackupPathFor(DeviceA, dir);
        var slotB = EapoRepair.BackupPathFor(DeviceB, dir);
        try
        {
            var a = new EapoRepairBackup(DeviceA, EapoRepairBackup.Applied, DateTimeOffset.UtcNow, [], null);
            var b = new EapoRepairBackup(DeviceB, EapoRepairBackup.Applied, DateTimeOffset.UtcNow, [], null);
            a.Save(slotA);
            b.Save(slotB);

            var undoA = EapoRepair.Undo(a, restartAudio: () => true, backupPath: slotA);
            _out.WriteLine($"undo A: {undoA.Outcome}; A slot exists={File.Exists(slotA)}; B slot exists={File.Exists(slotB)}");
            Assert.Equal(EapoRepairOutcome.Undone, undoA.Outcome);
            Assert.False(File.Exists(slotA));
            Assert.Equal(DeviceB, EapoRepair.LoadBackupFor(DeviceB, dir)!.EndpointGuid);

            var undoB = EapoRepair.Undo(b, restartAudio: () => true, backupPath: slotB);
            _out.WriteLine($"undo B: {undoB.Outcome}; remaining records: {EapoRepair.LoadAllBackups(dir).Count}");
            Assert.Equal(EapoRepairOutcome.Undone, undoB.Outcome);
            Assert.Empty(EapoRepair.LoadAllBackups(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_legacy_single_slot_moves_into_its_devices_own_slot_once()
    {
        var dir = OwnStateDirectory();
        var legacy = Path.Combine(dir, Path.GetFileName(EapoRepair.BackupPath));
        try
        {
            RecordFor(DeviceA, EapoRepairBackup.Applied).Save(legacy);
            Assert.Equal(DeviceA, EapoRepair.LoadBackupFor(DeviceA, dir)!.EndpointGuid); // readable before migration
            Assert.Null(EapoRepair.LoadBackupFor(DeviceB, dir));                         // and only for its device

            bool moved = EapoRepair.MigrateLegacyBackup(dir);
            _out.WriteLine($"migrated={moved}; files now: {string.Join(", ", Directory.GetFiles(dir).Select(Path.GetFileName))}");
            Assert.True(moved);
            Assert.False(File.Exists(legacy));
            Assert.Equal(DeviceA, EapoRepairBackup.Load(EapoRepair.BackupPathFor(DeviceA, dir))!.EndpointGuid);
            Assert.False(EapoRepair.MigrateLegacyBackup(dir)); // nothing left to move
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Migration_never_overwrites_a_slot_that_already_holds_a_record()
    {
        var dir = OwnStateDirectory();
        var legacy = Path.Combine(dir, Path.GetFileName(EapoRepair.BackupPath));
        var slot = EapoRepair.BackupPathFor(DeviceA, dir);
        try
        {
            RecordFor(DeviceA, EapoRepairBackup.Applied).Save(legacy);
            RecordFor(DeviceA, EapoRepairBackup.Applying).Save(slot);

            bool moved = EapoRepair.MigrateLegacyBackup(dir);
            _out.WriteLine($"migrated={moved}; legacy still there={File.Exists(legacy)}; slot stage={EapoRepairBackup.Load(slot)!.Stage}");
            Assert.False(moved);
            Assert.True(File.Exists(legacy));
            Assert.True(EapoRepairBackup.Load(slot)!.IsInterrupted);
            Assert.Equal(2, EapoRepair.LoadAllBackups(dir).Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void An_unreadable_legacy_record_is_left_where_it_is()
    {
        var dir = OwnStateDirectory();
        var legacy = Path.Combine(dir, Path.GetFileName(EapoRepair.BackupPath));
        try
        {
            File.WriteAllText(legacy, "{ not json");
            Assert.False(EapoRepair.MigrateLegacyBackup(dir));
            Assert.True(File.Exists(legacy));
            Assert.Empty(EapoRepair.LoadAllBackups(dir));
            _out.WriteLine("corrupt legacy record: not migrated, not counted, not deleted");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_state_directory_that_does_not_exist_holds_no_records()
    {
        var dir = Path.Combine(Path.GetTempPath(), "AorinEQ.Tests", "absent-" + Guid.NewGuid().ToString("N"));
        Assert.Empty(EapoRepair.LoadAllBackups(dir));
        Assert.Null(EapoRepair.LoadBackupFor(DeviceA, dir));
        Assert.Null(EapoRepair.LoadBackupFor("not-a-guid", dir));
        Assert.False(EapoRepair.MigrateLegacyBackup(dir));
        Assert.Null(EapoRepair.LoadSlotFor(DeviceA, dir));
        Assert.Null(EapoRepair.LoadSlotFor("not-a-guid", dir));
        Assert.False(Directory.Exists(dir));
        _out.WriteLine("absent directory: no records, nothing migrated");
    }

    [Fact]
    public void A_slot_whose_record_names_another_device_is_refused_and_nothing_is_written()
    {
        // Undo writes to whichever device a record NAMES. A record for B sitting in A's slot would
        // turn "undo A" into an elevated write to B, which nobody asked for — so it reads as no
        // record at all, everywhere: LoadSlotFor (what the elevated undo hands to Undo — it has
        // nothing else to write from), LoadBackupFor (Settings' Undo button and the repair guard)
        // and the startup scan. The control half proves the mismatch is what refused it: the same
        // slot holding A's own record is found by every one of them.
        var dir = OwnStateDirectory();
        var slotA = EapoRepair.BackupPathFor(DeviceA, dir);
        try
        {
            RecordFor(DeviceB, EapoRepairBackup.Applying).Save(slotA);
            var before = File.ReadAllText(slotA);

            _out.WriteLine($"B's record in A's slot: slot={EapoRepair.LoadSlotFor(DeviceA, dir)?.EndpointGuid ?? "<none>"}; "
                + $"lookup={EapoRepair.LoadBackupFor(DeviceA, dir)?.EndpointGuid ?? "<none>"}; "
                + $"scan={EapoRepair.LoadAllBackups(dir).Count}");
            Assert.Null(EapoRepair.LoadSlotFor(DeviceA, dir));
            Assert.Null(EapoRepair.LoadBackupFor(DeviceA, dir));
            Assert.Null(EapoRepair.LoadBackupFor(DeviceB, dir));
            Assert.Empty(EapoRepair.LoadAllBackups(dir));
            Assert.Equal(before, File.ReadAllText(slotA)); // left on disk exactly as it was

            // Control: A's own record in A's slot is found by all three.
            RecordFor(DeviceA, EapoRepairBackup.Applied).Save(slotA);
            _out.WriteLine($"A's record in A's slot: slot={EapoRepair.LoadSlotFor(DeviceA, dir)?.EndpointGuid ?? "<none>"}");
            Assert.Equal(DeviceA, EapoRepair.LoadSlotFor(DeviceA, dir)!.EndpointGuid);
            Assert.Equal(DeviceA, EapoRepair.LoadBackupFor(DeviceA, dir)!.EndpointGuid);
            Assert.Single(EapoRepair.LoadAllBackups(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_legacy_record_is_matched_to_its_device_by_GUID_not_by_spelling()
    {
        // Releases with one slot wrote the GUID however Windows spelled it. Upper case and no
        // braces is still device A.
        var dir = OwnStateDirectory();
        var legacy = Path.Combine(dir, Path.GetFileName(EapoRepair.BackupPath));
        try
        {
            var spelled = DeviceA.Trim('{', '}').ToUpperInvariant();
            RecordFor(spelled, EapoRepairBackup.Applied).Save(legacy);
            var found = EapoRepair.LoadBackupFor(DeviceA, dir);
            _out.WriteLine($"legacy record names '{spelled}'; LoadBackupFor({DeviceA}) -> {found?.EndpointGuid ?? "<none>"}");
            Assert.Equal(spelled, found?.EndpointGuid);
            Assert.Null(EapoRepair.LoadBackupFor(DeviceB, dir));
            Assert.Null(EapoRepair.LoadSlotFor(DeviceA, dir)); // the elevated undo only reads slots
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    [Trait(Requires.Key, Requires.EqualizerApo)]
    public void An_interrupted_legacy_record_not_yet_migrated_still_blocks_its_device()
    {
        // The guard looks the record up the way Undo finds it. Before the helper has migrated it,
        // an interrupted legacy record is still the only description of its device — a repair of
        // that device must not start a fresh slot beside it.
        var dir = OwnStateDirectory();
        var legacy = Path.Combine(dir, Path.GetFileName(EapoRepair.BackupPath));
        try
        {
            RecordFor(DeviceA.ToUpperInvariant(), EapoRepairBackup.Applying).Save(legacy);

            var result = EapoRepair.Repair(DeviceA,
                () => throw new InvalidOperationException("audio must not be restarted"),
                () => throw new InvalidOperationException("nothing should be verified"),
                directory: dir);
            _out.WriteLine($"{result.Outcome} - {result.Message}; files: "
                + string.Join(", ", Directory.GetFiles(dir).Select(Path.GetFileName)));
            Assert.Equal(EapoRepairOutcome.Refused, result.Outcome);
            Assert.Contains("didn't finish", result.Message);
            Assert.True(EapoRepairBackup.Load(legacy)!.IsInterrupted);
            Assert.False(File.Exists(EapoRepair.BackupPathFor(DeviceA, dir)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_unfinished_repair_balloon_is_only_for_a_device_something_can_undo()
    {
        const string Default = "{dddddddd-1111-2222-3333-444444444444}";
        const string Cable = "{cccccccc-1111-2222-3333-444444444444}";
        const string Other = "{eeeeeeee-1111-2222-3333-444444444444}";
        static EapoRepairBackup Interrupted(string g) => RecordFor(g, EapoRepairBackup.Applying);
        static EapoRepairBackup Applied(string g) => RecordFor(g, EapoRepairBackup.Applied);

        void Case(string name, string? expected, EapoRepairBackup[] records,
            string? defaultGuid = Default, string? cableGuid = Cable)
        {
            var page = EapoRepair.InterruptedRepairPage(records, defaultGuid, cableGuid);
            _out.WriteLine($"{name}: {page ?? "<no balloon>"}");
            Assert.Equal(expected, page);
        }

        Case("nothing on disk", null, []);
        Case("default's and cable's repairs finished", null, [Applied(Default), Applied(Cable)]);
        Case("default interrupted", SettingsSections.Volume, [Interrupted(Default)]);
        Case("default interrupted, spelled differently", SettingsSections.Volume,
            [Interrupted(Default.Trim('{', '}').ToUpperInvariant())]);
        Case("cable interrupted", SettingsSections.AirPlay, [Interrupted(Cable)]);
        Case("both interrupted: the default's comes first", SettingsSections.Volume,
            [Interrupted(Cable), Interrupted(Default)]);
        Case("another device interrupted: nothing can undo it", null, [Interrupted(Other)]);
        Case("another device beside the cable", SettingsSections.AirPlay,
            [Interrupted(Other), Interrupted(Cable)]);
        Case("no cable on this PC", null, [Interrupted(Cable)], cableGuid: null);
        Case("no default device reported", SettingsSections.AirPlay,
            [Interrupted(Default), Interrupted(Cable)], defaultGuid: null);
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void The_machines_own_default_endpoint_is_present_and_a_made_up_one_is_not()
    {
        // The real "does Windows still have this playback device?" predicate. The default endpoint
        // is by definition still attached; the deadbeef GUID never is. (When the device list cannot
        // be read at all the answer is "present" — an unreadable list must not read as "gone" —
        // but on a machine with an endpoint the list IS readable, which is what the trait says.)
        var real = AudioEndpoint.EndpointGuid(AudioEndpoint.GetDefaultRenderEndpointId())!;
        Assert.True(AudioEndpoint.IsEndpointPresent(real));
        Assert.False(AudioEndpoint.IsEndpointPresent(NoSuchDevice));
    }

    [Fact]
    public void Only_a_completed_outcome_reads_as_success()
    {
        // The one set both the helper's exit code and the launcher's "does this deserve a dialog?"
        // decision are derived from. A refusal that is only whispered into a status line is how a
        // user reports "I clicked repair and nothing happened" — so everything that is not one of
        // the three completed outcomes must read as a failure the UI has to say out loud.
        foreach (var outcome in Enum.GetValues<EapoRepairOutcome>())
        {
            bool expected = outcome is EapoRepairOutcome.Repaired
                or EapoRepairOutcome.AlreadyActive or EapoRepairOutcome.Undone;
            Assert.Equal(expected, new EapoRepairResult(outcome, "m").IsSuccess);
        }
    }

    [Fact]
    public void The_audio_restart_helper_is_the_one_the_setup_guide_already_uses()
    {
        // Elevated callers must not raise a second prompt — a prompt between a write and its
        // revert is how a user gets stuck half-repaired.
        Assert.Equal("runas", AudioServices.BuildStartInfo(elevate: true).Verb);
        Assert.Equal("", AudioServices.BuildStartInfo(elevate: false).Verb);
        var args = AudioServices.BuildStartInfo(elevate: false).Arguments;
        _out.WriteLine(args);
        Assert.Contains("Restart-Service AudioEndpointBuilder -Force", args);
        Assert.Contains("Start-Service Audiosrv", args);
    }
}
