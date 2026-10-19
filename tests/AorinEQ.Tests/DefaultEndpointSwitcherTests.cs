using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>Moving Windows' default playback device and putting it back — for real, through
/// IPolicyConfig, asserting what Windows reports per role. Every switching test restores each role
/// it found, the same trade the device-change tests make.</summary>
[Collection(RealAudioDeviceCollection.Name)]
public class DefaultEndpointSwitcherTests
{
    private readonly ITestOutputHelper _out;
    public DefaultEndpointSwitcherTests(ITestOutputHelper output) => _out = output;

    private const string NoSuchEndpoint = "{0.0.0.00000000}.{deadbeef-0000-0000-0000-000000000000}";
    private const string Cable = "{0.0.0.00000000}.{11111111-0000-0000-0000-000000000000}";
    private const string Speakers = "{0.0.0.00000000}.{22222222-0000-0000-0000-000000000000}";
    private const string Headphones = "{0.0.0.00000000}.{33333333-0000-0000-0000-000000000000}";
    private static readonly DefaultRenderRecord Recorded = new(Speakers, Speakers, Speakers);

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void Capture_reads_each_role_and_multimedia_is_the_app_wide_default()
    {
        var now = DefaultEndpointSwitcher.Capture();
        _out.WriteLine($"captured: {now}");
        Assert.NotNull(now.Console);
        Assert.NotNull(now.Communications);
        Assert.Equal(AudioEndpoint.GetDefaultRenderEndpointId(), now.Multimedia);
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    [Trait(Requires.Key, Requires.MultipleRenderEndpoints)]
    public void Switching_moves_every_role_and_restore_puts_each_one_back()
    {
        var before = DefaultEndpointSwitcher.Capture();
        var other = DefaultRenderDevice.Other(before.Multimedia);
        Assert.NotNull(other);
        _out.WriteLine($"before: {before}; switching to {other!.FriendlyName}");
        try
        {
            Assert.True(DefaultEndpointSwitcher.SetDefaultForAllRoles(other.Id));
            var during = DefaultEndpointSwitcher.Capture();
            _out.WriteLine($"during: {during}");
            Assert.Equal(new DefaultRenderRecord(other.Id, other.Id, other.Id), during);

            var (complete, moved) = DefaultEndpointSwitcher.Restore(before);
            var after = DefaultEndpointSwitcher.Capture();
            _out.WriteLine($"restore complete={complete} moved multimedia to {moved ?? "(no change)"}; after: {after}");
            Assert.True(complete);
            Assert.Equal(before, after);
            Assert.Equal(before.Multimedia, moved);
        }
        finally
        {
            DefaultRenderDevice.PutBack(before);
        }
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void Restoring_to_an_endpoint_that_no_longer_exists_leaves_windows_choice_alone()
    {
        var before = DefaultEndpointSwitcher.Capture();
        try
        {
            // The current default is not the cable, so a role whose device has gone stays on Windows' choice.
            var (complete, moved) = DefaultEndpointSwitcher.Restore(new DefaultRenderRecord(NoSuchEndpoint, NoSuchEndpoint, NoSuchEndpoint));
            var after = DefaultEndpointSwitcher.Capture();
            _out.WriteLine($"before {before}; restore of a vanished device: complete={complete} moved {moved ?? "(nothing)"}; after {after}");
            Assert.True(complete);
            Assert.Null(moved);
            Assert.Equal(before, after);
        }
        finally
        {
            DefaultRenderDevice.PutBack(before);
        }
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    [Trait(Requires.Key, Requires.MultipleRenderEndpoints)]
    public void Each_role_is_restored_only_when_its_own_endpoint_still_exists()
    {
        var before = DefaultEndpointSwitcher.Capture();
        var other = DefaultRenderDevice.Other(before.Multimedia);
        Assert.NotNull(other);
        try
        {
            var (complete, moved) = DefaultEndpointSwitcher.Restore(new DefaultRenderRecord(NoSuchEndpoint, other!.Id, NoSuchEndpoint));
            var after = DefaultEndpointSwitcher.Capture();
            _out.WriteLine($"before {before}; partial record -> complete={complete} moved {moved}; after {after}");
            Assert.True(complete);
            Assert.Equal(other.Id, after.Multimedia);
            Assert.Equal(before.Console, after.Console);
            Assert.Equal(before.Communications, after.Communications);
            Assert.Equal(other.Id, moved);
        }
        finally
        {
            DefaultRenderDevice.PutBack(before);
        }
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void Asking_windows_to_default_to_a_device_that_does_not_exist_fails_quietly()
    {
        var before = DefaultEndpointSwitcher.Capture();
        bool ok = DefaultEndpointSwitcher.SetDefaultForAllRoles(NoSuchEndpoint);
        _out.WriteLine($"SetDefaultForAllRoles(bogus) -> {ok}; default still {AudioEndpoint.GetDefaultRenderEndpointId()}");
        Assert.False(ok);
        Assert.Equal(before, DefaultEndpointSwitcher.Capture());
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    [Trait(Requires.Key, Requires.MultipleRenderEndpoints)]
    public void A_real_switch_by_the_user_mid_stream_reads_as_their_override_and_ours_as_ours()
    {
        // Spec §6 "user-change-mid-stream path", driven through real switches and real reads of
        // Windows' default rather than made-up ids. The other endpoint stands in for the cable:
        // to Windows, and to Classify, that is all the cable is.
        var before = DefaultEndpointSwitcher.Capture();
        var standIn = DefaultRenderDevice.Other(before.Multimedia);
        Assert.NotNull(standIn);
        try
        {
            // AorinEQ's switch, with `before` as the record it would hold: the default it lands on is ours.
            Assert.True(DefaultEndpointSwitcher.SetDefaultForAllRoles(standIn!.Id));
            var landed = AudioEndpoint.GetDefaultRenderEndpointId();
            var ours = DefaultEndpointSwitcher.Classify(landed, ownTargetId: standIn.Id, before, [standIn.Id]);
            _out.WriteLine($"our switch to {standIn.FriendlyName}: default now {landed} -> {ours}");
            Assert.Equal(standIn.Id, landed);
            Assert.Equal(DefaultChange.Ours, ours);

            // The user picks their own device back in Sound settings while the stream runs.
            DefaultRenderDevice.SetDefaultAndSettle(before.Multimedia!);
            var chosen = AudioEndpoint.GetDefaultRenderEndpointId();
            var theirs = DefaultEndpointSwitcher.Classify(chosen, ownTargetId: null, before, [standIn.Id]);
            _out.WriteLine($"user switched to {chosen} mid-stream -> {theirs}");
            Assert.Equal(before.Multimedia, chosen);
            Assert.Equal(DefaultChange.UserOverride, theirs);
        }
        finally
        {
            DefaultRenderDevice.PutBack(before);
        }
    }

    // ---------------------------------------------------------------- classification (pure)

    [Fact]
    public void Our_own_switch_arriving_is_ours_whatever_else_is_true()
    {
        Assert.Equal(DefaultChange.Ours, DefaultEndpointSwitcher.Classify(Cable, Cable, Recorded, [Cable]));
        Assert.Equal(DefaultChange.Ours, DefaultEndpointSwitcher.Classify(Speakers, Speakers, null, []));
        _out.WriteLine("a notification for the endpoint we just switched to is ours (switch AND restore)");
    }

    [Fact]
    public void Without_a_record_nothing_is_ours_to_manage()
    {
        var change = DefaultEndpointSwitcher.Classify(Headphones, null, null, [Cable]);
        _out.WriteLine($"no record -> {change}");
        Assert.Equal(DefaultChange.Unrelated, change);
    }

    [Fact]
    public void A_late_duplicate_for_the_cable_is_still_ours()
    {
        var change = DefaultEndpointSwitcher.Classify(Cable, null, Recorded, [Cable]);
        _out.WriteLine($"cable is default, record held, no switch in flight -> {change}");
        Assert.Equal(DefaultChange.Ours, change);
    }

    [Fact]
    public void The_user_picking_another_output_mid_stream_is_their_decision()
    {
        var change = DefaultEndpointSwitcher.Classify(Headphones, null, Recorded, [Cable]);
        _out.WriteLine($"user chose headphones mid-stream -> {change}");
        Assert.Equal(DefaultChange.UserOverride, change);
    }

    [Fact]
    public void Losing_the_cable_mid_stream_is_classified_as_cable_lost_so_the_default_is_restored()
    {
        // Review focus: the user disables or uninstalls VB-Cable while streaming. Windows moves the
        // default to whatever it likes; the user's ORIGINAL device is what should come back.
        var disabled = DefaultEndpointSwitcher.Classify(Headphones, null, Recorded, cableIds: []);
        var nothingLeft = DefaultEndpointSwitcher.Classify(null, null, Recorded, cableIds: []);
        _out.WriteLine($"cable gone -> {disabled}; cable gone and no device at all -> {nothingLeft}");
        Assert.Equal(DefaultChange.CableLost, disabled);
        Assert.Equal(DefaultChange.CableLost, nothingLeft);
    }

    // ---------------------------------------------------------------- our own switch is silent

    private const string CableGuid = "{aaaaaaaa-0000-0000-0000-000000000000}";
    private const string SpeakersGuid = "{bbbbbbbb-0000-0000-0000-000000000000}";

    private const string Cable16Guid = "{cccccccc-0000-0000-0000-000000000000}";

    [Theory]
    [InlineData(true, CableGuid, new[] { CableGuid }, true)]                                  // our record holds the default on the cable
    [InlineData(true, "{AAAAAAAA-0000-0000-0000-000000000000}", new[] { CableGuid }, true)]   // however the GUID is cased
    [InlineData(true, Cable16Guid, new[] { CableGuid, Cable16Guid }, true)]                   // the cable's second endpoint ("CABLE In 16ch") is the cable too
    [InlineData(true, Cable16Guid, new[] { CableGuid }, false)]                               // ...and only because it is in the set
    [InlineData(true, SpeakersGuid, new[] { CableGuid, Cable16Guid }, false)]                 // a reading of another device is news as usual
    [InlineData(false, CableGuid, new[] { CableGuid }, false)]                                // no record: the user chose the cable themselves
    [InlineData(true, CableGuid, new string[0], false)]                                       // the cable is gone
    [InlineData(true, null, new[] { CableGuid }, false)]                                      // no default device at all
    public void Only_a_reading_of_the_cable_while_our_record_holds_it_there_is_ours(
        bool recordHeld, string? readingGuid, string[] cableGuids, bool expected)
    {
        var record = recordHeld ? Recorded : null;
        bool ours = DefaultEndpointSwitcher.IsHeldOnCable(record, readingGuid, cableGuids);
        _out.WriteLine($"record={(recordHeld ? "held" : "none")} reading={readingGuid ?? "(none)"} cable=[{string.Join(", ", cableGuids)}] -> ours={ours}");
        Assert.Equal(expected, ours);
    }

    [Fact]
    public void Keeping_our_cable_readings_from_the_health_tracker_silences_the_warning_the_repair_and_the_recovered_balloon()
    {
        // The real tracker and the real balloon/auto-repair rules, fed the three readings one
        // AirPlay-only stream produces: the user's speakers (healthy), the cable AorinEQ switched to
        // (Equalizer APO not on it), the speakers again after the restore.
        var t0 = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        var speakers = new EapoHealthSnapshot(Installed: true, ActiveOnDevice: true, IncludeLinePresent: true, SpeakersGuid, t0);
        var cable = new EapoHealthSnapshot(Installed: true, ActiveOnDevice: false, IncludeLinePresent: true, CableGuid, t0.AddSeconds(5));
        var back = speakers with { CheckedAt = t0.AddSeconds(60) };

        // CheckEapoHealth's gate: a reading IsHeldOnCable claims never reaches the tracker.
        var gated = new EapoHealthTracker();
        EapoHealthUpdate? Feed(EapoHealthSnapshot reading, DefaultRenderRecord? held) =>
            DefaultEndpointSwitcher.IsHeldOnCable(held, reading.EndpointGuid, [CableGuid]) ? null : gated.Update(reading);
        var start = Feed(speakers, held: null);
        var onCable = Feed(cable, held: Recorded);   // stream running: record held, default on the cable
        var restored = Feed(back, held: null);       // stream ended: record cleared, default back
        _out.WriteLine($"gated: start={start?.Event} cable={(onCable is null ? "kept from the tracker" : onCable.Event.ToString())} restored={restored?.Event} notify={restored?.Notify}");
        Assert.Null(onCable);
        Assert.Equal(EapoHealthEvent.None, restored!.Event);
        Assert.False(restored.Notify);
        Assert.Null(EapoHealthCopy.Balloon(restored, VolumeModes.Eapo));

        // The same three readings WITHOUT the gate: the cable raises a warning AND an automatic
        // repair prompt, and the restore then announces "working again" — exactly what is suppressed.
        var open = new EapoHealthTracker();
        open.Update(speakers);
        var warned = open.Update(cable);
        var recovered = open.Update(back);
        _out.WriteLine($"ungated: cable={warned.Event} notify={warned.Notify} autoRepair={EapoAutoRepair.ShouldStart(warned, null)}; "
            + $"restored={recovered.Event} notify={recovered.Notify}");
        Assert.True(warned.Notify);
        Assert.NotNull(EapoHealthCopy.Balloon(warned, VolumeModes.Eapo));
        Assert.True(EapoAutoRepair.ShouldStart(warned, declined: null));
        Assert.Equal(EapoHealthEvent.Recovered, recovered.Event);
        Assert.NotNull(EapoHealthCopy.Balloon(recovered, VolumeModes.Eapo));
    }

    // ---------------------------------------------------------------- what a restore puts back

    private const string Desk = "{0.0.0.00000000}.{44444444-0000-0000-0000-000000000000}";
    private static readonly DefaultRenderRecord OnCable = new(Cable, Cable, Cable);

    private void Log(string label, RestorePlan plan) =>
        _out.WriteLine($"{label}: complete={plan.Complete} sets=[{string.Join(", ", plan.Sets.Select(s => $"{s.Role}->{s.EndpointId}"))}]");

    [Fact]
    public void Every_role_whose_recorded_device_is_active_goes_back_to_it()
    {
        var plan = DefaultEndpointSwitcher.PlanRestore(new DefaultRenderRecord(Speakers, Headphones, Speakers),
            [Cable, Speakers, Headphones], OnCable, [Cable]);
        Log("all recorded devices active", plan);
        Assert.True(plan.Complete);
        Assert.Equal(
            new (AudioEndpoint.ERole, string)[] { (AudioEndpoint.ERole.Console, Speakers), (AudioEndpoint.ERole.Multimedia, Headphones), (AudioEndpoint.ERole.Communications, Speakers) },
            plan.Sets);
    }

    [Fact]
    public void A_recorded_device_matches_its_active_endpoint_however_the_id_is_cased()
    {
        var plan = DefaultEndpointSwitcher.PlanRestore(new DefaultRenderRecord(Speakers, Speakers, Speakers),
            [Speakers.ToUpperInvariant()], OnCable, [Cable]);
        Log("recorded lower case, enumerated upper case", plan);
        Assert.True(plan.Complete);
        Assert.Equal(3, plan.Sets.Count);
        Assert.All(plan.Sets, s => Assert.Equal(Speakers, s.EndpointId));
    }

    [Fact]
    public void A_gone_device_whose_role_is_already_off_the_cable_stays_on_windows_choice()
    {
        // The user's speakers were unplugged; Windows already moved every role to the headphones.
        var plan = DefaultEndpointSwitcher.PlanRestore(Recorded, [Cable, Headphones],
            new DefaultRenderRecord(Headphones, Headphones, Headphones), [Cable]);
        Log("speakers gone, roles on headphones", plan);
        Assert.True(plan.Complete);
        Assert.Empty(plan.Sets);
    }

    [Fact]
    public void A_gone_device_whose_role_is_still_on_the_cable_moves_to_the_first_active_device_that_is_not_the_cable()
    {
        // Listed cable-first on purpose: the fallback must skip the cable, not take the first entry.
        var plan = DefaultEndpointSwitcher.PlanRestore(Recorded, [Cable, Headphones, Desk],
            OnCable, [Cable]);
        Log("speakers gone, every role still on the cable", plan);
        Assert.True(plan.Complete);
        Assert.Equal(
            new (AudioEndpoint.ERole, string)[] { (AudioEndpoint.ERole.Console, Headphones), (AudioEndpoint.ERole.Multimedia, Headphones), (AudioEndpoint.ERole.Communications, Headphones) },
            plan.Sets);
    }

    [Fact]
    public void A_gone_device_on_the_cable_with_nothing_else_to_move_to_is_incomplete_so_the_record_is_kept()
    {
        var plan = DefaultEndpointSwitcher.PlanRestore(Recorded, [Cable], OnCable, [Cable]);
        Log("speakers gone, only the cable left", plan);
        Assert.False(plan.Complete);
        Assert.Empty(plan.Sets);
    }

    private const string Cable16 = "{0.0.0.00000000}.{55555555-0000-0000-0000-000000000000}";

    [Fact]
    public void No_endpoint_of_the_cable_is_ever_the_fallback_and_any_of_them_counts_as_on_the_cable()
    {
        // A VB-Cable pack with two endpoints on its device ("CABLE Input" and "CABLE In 16ch"),
        // both listed ahead of the only real device. Console sits on the SECOND cable endpoint: it
        // is still on the cable, and neither cable endpoint may replace the gone speakers.
        var current = new DefaultRenderRecord(Cable16, Cable, Cable);
        var plan = DefaultEndpointSwitcher.PlanRestore(Recorded, [Cable, Cable16, Headphones], current, [Cable, Cable16]);
        Log("two cable endpoints listed first, speakers gone", plan);
        Assert.True(plan.Complete);
        Assert.DoesNotContain(plan.Sets, s => s.EndpointId == Cable || s.EndpointId == Cable16);
        Assert.Equal(
            new (AudioEndpoint.ERole, string)[] { (AudioEndpoint.ERole.Console, Headphones), (AudioEndpoint.ERole.Multimedia, Headphones), (AudioEndpoint.ERole.Communications, Headphones) },
            plan.Sets);

        var onlyCables = DefaultEndpointSwitcher.PlanRestore(Recorded, [Cable, Cable16], current, [Cable, Cable16]);
        Log("only the two cable endpoints left", onlyCables);
        Assert.False(onlyCables.Complete);
        Assert.Empty(onlyCables.Sets);
    }

    [Fact]
    public void A_default_on_the_cables_second_endpoint_is_ours_not_the_users()
    {
        var change = DefaultEndpointSwitcher.Classify(Cable16, null, Recorded, [Cable, Cable16]);
        _out.WriteLine($"default landed on the cable's 16ch endpoint -> {change}");
        Assert.Equal(DefaultChange.Ours, change);
    }

    [Fact]
    public void Each_role_is_judged_on_its_own()
    {
        // Console: device gone, still on the cable -> fallback. Multimedia: device active -> back.
        // Communications: device gone, Windows already chose the headphones -> left alone.
        var record = new DefaultRenderRecord(NoSuchEndpoint, Speakers, NoSuchEndpoint);
        var current = new DefaultRenderRecord(Cable, Cable, Headphones);
        var plan = DefaultEndpointSwitcher.PlanRestore(record, [Cable, Headphones, Speakers], current, [Cable]);
        Log("mixed roles", plan);
        Assert.True(plan.Complete);
        Assert.Equal(new (AudioEndpoint.ERole, string)[] { (AudioEndpoint.ERole.Console, Headphones), (AudioEndpoint.ERole.Multimedia, Speakers) }, plan.Sets);
    }

    [Fact]
    public void An_empty_endpoint_list_is_never_read_as_every_device_gone()
    {
        // GetRenderEndpoints returns [] when the enumeration itself fails: the record must survive.
        var plan = DefaultEndpointSwitcher.PlanRestore(Recorded, [], OnCable, [Cable]);
        var noCable = DefaultEndpointSwitcher.PlanRestore(Recorded, [], Recorded, cableIds: []);
        Log("empty enumeration, on the cable", plan);
        Log("empty enumeration, no cable", noCable);
        Assert.False(plan.Complete);
        Assert.Empty(plan.Sets);
        Assert.False(noCable.Complete);
        Assert.Empty(noCable.Sets);
    }

    [Fact]
    public void With_no_cable_left_a_gone_device_is_simply_windows_choice()
    {
        var plan = DefaultEndpointSwitcher.PlanRestore(Recorded, [Headphones],
            new DefaultRenderRecord(Headphones, Headphones, Headphones), cableIds: []);
        Log("speakers and cable gone", plan);
        Assert.True(plan.Complete);
        Assert.Empty(plan.Sets);
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void Applying_a_set_windows_refuses_reports_the_restore_incomplete()
    {
        // A real refusal: Windows is asked to make a device it does not have the default.
        var before = DefaultEndpointSwitcher.Capture();
        bool refused = DefaultEndpointSwitcher.Apply(new RestorePlan([(AudioEndpoint.ERole.Multimedia, NoSuchEndpoint)], Complete: true));
        bool accepted = DefaultEndpointSwitcher.Apply(new RestorePlan([(AudioEndpoint.ERole.Multimedia, before.Multimedia!)], Complete: true));
        bool incomplete = DefaultEndpointSwitcher.Apply(new RestorePlan([(AudioEndpoint.ERole.Multimedia, before.Multimedia!)], Complete: false));
        _out.WriteLine($"refused set -> {refused}; set to the current default -> {accepted}; incomplete plan -> {incomplete}; default {DefaultEndpointSwitcher.Capture()}");
        Assert.False(refused);
        Assert.True(accepted);
        Assert.False(incomplete);
        Assert.Equal(before, DefaultEndpointSwitcher.Capture());
    }

    // ---------------------------------------------------------------- starting AirPlay only

    /// <summary>A real settings.json in a fresh temp folder, deleted by the caller.</summary>
    private static string NewSettingsPath() =>
        Path.Combine(Path.GetTempPath(), "aorineq-tests-" + Guid.NewGuid().ToString("N"), "settings.json");

    private static void DeleteFolderOf(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void A_cable_that_already_is_the_default_records_nothing_and_moves_nothing()
    {
        var before = DefaultEndpointSwitcher.Capture();
        var settings = Settings.Default with { Percent = 37 };
        int saves = 0;
        var (after, outcome, movedTo) = DefaultEndpointSwitcher.BeginAirPlayOnly(settings, before.Multimedia!,
            _ => { saves++; return true; });
        _out.WriteLine($"cable = current default -> {outcome}; saves={saves}; moved {movedTo ?? "(nothing)"}");
        Assert.Equal(AirPlayOnlyStart.AlreadyDefault, outcome);
        Assert.Same(settings, after);
        Assert.Equal(0, saves);
        Assert.Null(movedTo);
        Assert.Equal(before, DefaultEndpointSwitcher.Capture());
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void A_record_that_cannot_be_saved_leaves_the_default_where_it_is()
    {
        // A real failing save: settings.json's path is a folder, so the write is refused.
        var path = NewSettingsPath();
        Directory.CreateDirectory(path);
        try
        {
            var before = DefaultEndpointSwitcher.Capture();
            var settings = Settings.Default with { Percent = 37 };
            var (after, outcome, movedTo) = DefaultEndpointSwitcher.BeginAirPlayOnly(settings, NoSuchEndpoint, s => s.TrySave(path));
            _out.WriteLine($"unsavable record -> {outcome}; record after: {after.AirPlayRestoreDefault?.ToString() ?? "(none)"}; default {DefaultEndpointSwitcher.Capture()}");
            Assert.Equal(AirPlayOnlyStart.NotSaved, outcome);
            Assert.Same(settings, after);
            Assert.Null(movedTo);
            Assert.Equal(before, DefaultEndpointSwitcher.Capture());
        }
        finally
        {
            DeleteFolderOf(path);
        }
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    [Trait(Requires.Key, Requires.MultipleRenderEndpoints)]
    public void A_record_that_cannot_be_saved_never_moves_a_default_that_could_have_moved()
    {
        var before = DefaultEndpointSwitcher.Capture();
        var standIn = DefaultRenderDevice.Other(before.Multimedia);
        Assert.NotNull(standIn);
        var path = NewSettingsPath();
        Directory.CreateDirectory(path);
        try
        {
            var (_, outcome, _) = DefaultEndpointSwitcher.BeginAirPlayOnly(Settings.Default, standIn!.Id, s => s.TrySave(path));
            _out.WriteLine($"switchable stand-in {standIn.FriendlyName}, unsavable record -> {outcome}; default {DefaultEndpointSwitcher.Capture()}");
            Assert.Equal(AirPlayOnlyStart.NotSaved, outcome);
            Assert.Equal(before, DefaultEndpointSwitcher.Capture());
        }
        finally
        {
            DefaultRenderDevice.PutBack(before);
            DeleteFolderOf(path);
        }
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void The_record_is_on_disk_before_the_switch_and_a_refused_switch_clears_it()
    {
        var path = NewSettingsPath();
        try
        {
            var before = DefaultEndpointSwitcher.Capture();
            DefaultRenderRecord? onDiskAtSave = null;
            DefaultRenderRecord? defaultAtSave = null;
            var (after, outcome, movedTo) = DefaultEndpointSwitcher.BeginAirPlayOnly(Settings.Default with { Percent = 37 }, NoSuchEndpoint, s =>
            {
                bool saved = s.TrySave(path);
                onDiskAtSave = Settings.Load(path).AirPlayRestoreDefault;
                defaultAtSave = DefaultEndpointSwitcher.Capture();
                return saved;
            });
            _out.WriteLine($"on disk at save: {onDiskAtSave}; default at save: {defaultAtSave}; outcome {outcome}; "
                + $"record after: {after.AirPlayRestoreDefault?.ToString() ?? "(none)"}; moved {movedTo ?? "(nothing)"}");
            Assert.Equal(before, onDiskAtSave);    // what was displaced, written first…
            Assert.Equal(before, defaultAtSave);   // …while nothing had moved yet
            Assert.Equal(AirPlayOnlyStart.Refused, outcome);
            Assert.Null(after.AirPlayRestoreDefault);
            Assert.Equal(37, after.Percent);
            Assert.Null(movedTo);
            Assert.Equal(before, DefaultEndpointSwitcher.Capture());
        }
        finally
        {
            DeleteFolderOf(path);
        }
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void A_record_already_held_is_kept_and_not_saved_again()
    {
        // Reconnecting mid-stream: the record describes the user's devices; re-capturing now
        // could record the cable. (The switch to a device Windows lacks is refused, so the held
        // record is restored — every device in it is active — and cleared.)
        var before = DefaultEndpointSwitcher.Capture();
        int saves = 0;
        var (after, outcome, movedTo) = DefaultEndpointSwitcher.BeginAirPlayOnly(
            Settings.Default with { AirPlayRestoreDefault = before }, NoSuchEndpoint, _ => { saves++; return true; });
        _out.WriteLine($"held record, refused switch -> {outcome}; saves={saves}; record after: {after.AirPlayRestoreDefault?.ToString() ?? "(none)"}");
        Assert.Equal(0, saves);
        Assert.Equal(AirPlayOnlyStart.Refused, outcome);
        Assert.Null(after.AirPlayRestoreDefault);
        Assert.Null(movedTo);
        Assert.Equal(before, DefaultEndpointSwitcher.Capture());
    }

    [Fact]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    [Trait(Requires.Key, Requires.MultipleRenderEndpoints)]
    public void A_saved_record_switches_every_role_to_the_cable()
    {
        var before = DefaultEndpointSwitcher.Capture();
        var standIn = DefaultRenderDevice.Other(before.Multimedia);
        Assert.NotNull(standIn);
        var path = NewSettingsPath();
        try
        {
            var (after, outcome, _) = DefaultEndpointSwitcher.BeginAirPlayOnly(Settings.Default, standIn!.Id, s => s.TrySave(path));
            var now = DefaultEndpointSwitcher.Capture();
            _out.WriteLine($"switch to {standIn.FriendlyName} -> {outcome}; default {now}; on disk {Settings.Load(path).AirPlayRestoreDefault}");
            Assert.Equal(AirPlayOnlyStart.Switched, outcome);
            Assert.Equal(before, after.AirPlayRestoreDefault);
            Assert.Equal(before, Settings.Load(path).AirPlayRestoreDefault);
            Assert.Equal(new DefaultRenderRecord(standIn.Id, standIn.Id, standIn.Id), now);
        }
        finally
        {
            DefaultRenderDevice.PutBack(before);
            DeleteFolderOf(path);
        }
    }

    // ---------------------------------------------------------------- who may restore

    [Theory]
    [InlineData(true, true, true)]     // the running instance, settings.json loaded as its own
    [InlineData(true, false, false)]   // first-run wizard declined: owns the mutex, never loaded its state
    [InlineData(false, false, false)]  // second launch / non-elevated half of the elevation bounce
    [InlineData(false, true, false)]   // never reachable, and still no
    public void Only_the_instance_that_owns_settings_may_put_the_default_back(bool ownsInstance, bool settingsAdopted, bool expected)
    {
        bool may = DefaultEndpointSwitcher.MayRestore(ownsInstance, settingsAdopted);
        _out.WriteLine($"owns instance={ownsInstance} settings adopted={settingsAdopted} -> may restore={may}");
        Assert.Equal(expected, may);
    }
}
