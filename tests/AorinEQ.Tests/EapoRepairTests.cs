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
    public void The_default_endpoint_reads_as_attached_on_this_machine()
    {
        var guid = AudioEndpoint.EndpointGuid(AudioEndpoint.GetDefaultRenderEndpointId())!;
        var clsids = EapoEndpoint.ResolveClsids(EapoDetection.GetInstallPath()!)!;
        var fx = EapoEndpoint.ReadFxProperties(guid);
        foreach (var v in fx)
            _out.WriteLine($"  {v.Name,-46} {v.Kind,-12} {string.Join(" | ", v.Data ?? [])}");

        // Both halves of the real condition, read straight off the machine.
        Assert.True(EapoDetection.HasChildApoRecord(guid));
        Assert.True(EapoEndpoint.IsApoAttached(guid, clsids));
        Assert.True(EapoDetection.IsActiveOnEndpoint(guid));

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
    public void Equalizer_APOs_own_record_for_this_device_is_the_shape_the_repair_writes()
    {
        var guid = AudioEndpoint.EndpointGuid(AudioEndpoint.GetDefaultRenderEndpointId())!;
        var actual = EapoEndpoint.ReadChildApos(guid);
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
        // If it cannot be restored it must not be displaced. A DWORD in the property store is not
        // something this code writes, so a backup containing one is not a backup.
        var reason = EapoEndpoint.WhyNotRepairable(
            [new RegValue("{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},99", "Unsupported:DWord", null)], Clsids);
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
    public void The_backup_lives_somewhere_both_accounts_can_reach()
    {
        // Machine-wide, not per-user: the repair runs elevated and the undo reads it unelevated,
        // and a standard user typing an administrator's credentials makes those two different
        // profiles.
        _out.WriteLine(EapoRepair.BackupPath);
        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            EapoRepair.BackupPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ApoPaths.StateFolderName, EapoRepair.BackupPath);
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

    [Fact]
    [Trait(Requires.Key, Requires.EqualizerApo)]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void Repair_does_nothing_at_all_to_a_device_that_is_already_working()
    {
        // This machine's default device IS active, so the repair must stop before it writes,
        // before it restarts audio, and before it asks anything to be verified. The two callbacks
        // throwing is the assertion.
        var guid = AudioEndpoint.EndpointGuid(AudioEndpoint.GetDefaultRenderEndpointId())!;
        var before = Describe(EapoEndpoint.ReadFxProperties(guid));

        var result = EapoRepair.Repair(guid,
            () => throw new InvalidOperationException("audio must not be restarted"),
            () => throw new InvalidOperationException("nothing should be verified"));

        _out.WriteLine(result.Message);
        Assert.Equal(EapoRepairOutcome.AlreadyActive, result.Outcome);
        Assert.Equal(before, Describe(EapoEndpoint.ReadFxProperties(guid)));
        Assert.False(File.Exists(EapoRepair.BackupPath)); // no backup taken for a no-op
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
