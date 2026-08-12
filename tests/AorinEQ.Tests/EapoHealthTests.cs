using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>Equalizer APO health monitoring: the real machine's reading, the include-line probe
/// against real files, and the transition/notification policy.
///
/// WHY THE "REGISTRATION REMOVED" CASE IS NOT HERE. The obvious test is to delete this machine's
/// real HKLM\SOFTWARE\EqualizerAPO\Child APOs\{endpoint} key, assert the detector reports the
/// fault, and restore it in a finally. That key is writable only by Administrators (measured: the
/// ACL grants BUILTIN\Users ReadKey), so such a test would need the whole suite to run elevated —
/// and this repository bans runtime skip-guards, so it could not stand down when it is not. It is
/// therefore proven in the release's live verification instead, which removes the user's REAL
/// registration under one elevated helper, watches the RUNNING app notice it without a restart,
/// and restores it byte-identically. Everything below runs at the desk and on a hosted runner.</summary>
public class EapoHealthTests
{
    private readonly ITestOutputHelper _out;
    public EapoHealthTests(ITestOutputHelper output) => _out = output;

    private static readonly DateTimeOffset T0 = new(2026, 8, 12, 10, 0, 0, TimeSpan.Zero);

    private static EapoHealthSnapshot Snap(
        bool installed = true, bool active = true, bool? include = true, DateTimeOffset? at = null) =>
        new(installed, active, include, "{11111111-2222-3333-4444-555555555555}", at ?? T0);

    // ---------------------------------------------------------------- the real machine

    [Fact]
    [Trait(Requires.Key, Requires.EqualizerApo)]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void Read_reports_the_real_state_of_this_machine()
    {
        var snapshot = EapoHealthSnapshot.Read(T0);
        _out.WriteLine($"installed={snapshot.Installed} active={snapshot.ActiveOnDevice} "
            + $"include={snapshot.IncludeLinePresent?.ToString() ?? "<unreadable>"} "
            + $"guid={snapshot.EndpointGuid} status={snapshot.Status} healthy={snapshot.Healthy}");

        // This dev machine runs AorinEQ against a working Equalizer APO on the default device —
        // the same premise EapoOnboardingTests.Detect_reports_active_on_this_machine relies on.
        Assert.True(snapshot.Installed);
        Assert.True(snapshot.ActiveOnDevice);
        Assert.True(snapshot.IncludeLinePresent);
        Assert.NotNull(snapshot.EndpointGuid);
        Assert.Equal(EapoStatus.Active, snapshot.Status);
        Assert.True(snapshot.Healthy);
        Assert.Equal(T0, snapshot.CheckedAt);
    }

    [Fact]
    [Trait(Requires.Key, Requires.EqualizerApo)]
    [Trait(Requires.Key, Requires.AudioEndpoint)]
    public void Read_agrees_with_the_status_the_rest_of_the_app_already_uses()
    {
        // Two readers of the same machine that must never disagree — the Settings status line and
        // the startup gate have used Detect() since v1.5.0.
        Assert.Equal(EapoDetection.Detect(), EapoHealthSnapshot.Read(T0).Status);
    }

    [Fact]
    public void An_endpoint_with_no_registration_reads_as_inactive_from_the_real_registry()
    {
        // The real HKLM read, against a GUID that genuinely has no "Child APOs" subkey: this is
        // the exact call the snapshot composes, and it must return false rather than throw or
        // optimistically assume.
        Assert.False(EapoDetection.IsActiveOnEndpoint("{deadbeef-0000-0000-0000-000000000000}"));

        var snapshot = new EapoHealthSnapshot(
            Installed: true, ActiveOnDevice: EapoDetection.IsActiveOnEndpoint("{deadbeef-0000-0000-0000-000000000000}"),
            IncludeLinePresent: true, EndpointGuid: "{deadbeef-0000-0000-0000-000000000000}", CheckedAt: T0);
        Assert.False(snapshot.Healthy);
        Assert.Equal(EapoStatus.InstalledInactive, snapshot.Status);
    }

    // ---------------------------------------------------------------- the include line, real files

    [Fact]
    public void HasIncludeLine_reads_real_config_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aorineq-health-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "config.txt");

            Assert.False(ApoWriter.HasIncludeLine(path)); // no file at all

            File.WriteAllText(path, "Include: peace.txt\r\n\r\n" + ApoWriter.IncludeLine + "\r\n");
            Assert.True(ApoWriter.HasIncludeLine(path));

            // The very shape the fault takes: another tool rewrites config.txt and drops our line.
            File.WriteAllText(path, "Include: peace.txt\r\n");
            Assert.False(ApoWriter.HasIncludeLine(path));

            // Same trimmed, case-insensitive comparison EnsureInclude uses, so the health report
            // and the include guard can never disagree about the same file.
            File.WriteAllText(path, "   include: AORINEQ.TXT   \r\n");
            Assert.True(ApoWriter.HasIncludeLine(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void HasIncludeLine_reports_unknown_rather_than_missing_when_the_file_is_locked()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aorineq-health-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "config.txt");
            File.WriteAllText(path, ApoWriter.IncludeLine + "\r\n");
            // A real exclusive hold, the way another tool's save looks from here.
            using (var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Null(ApoWriter.HasIncludeLine(path));
                // …and "unknown" must NOT read as a fault, or every save by another tool would
                // raise an alarm.
                Assert.True(Snap(include: null).Healthy);
            }
            Assert.True(ApoWriter.HasIncludeLine(path)); // readable again the moment the hold goes
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------------------------------------------------------- what a reading means

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, null, true)]   // unreadable config.txt is not a fault
    [InlineData(true, true, false, false)] // our line really gone: nothing we write is read
    [InlineData(true, false, true, false)] // the Windows-update detach
    [InlineData(false, false, null, false)]
    public void Healthy_requires_installed_active_and_a_present_or_unknown_include(
        bool installed, bool active, bool? include, bool healthy)
    {
        Assert.Equal(healthy, Snap(installed, active, include).Healthy);
    }

    [Theory]
    [InlineData(false, false, EapoStatus.NotInstalled)]
    [InlineData(true, false, EapoStatus.InstalledInactive)]
    [InlineData(true, true, EapoStatus.Active)]
    public void Status_summarises_the_two_registry_facts(bool installed, bool active, EapoStatus expected)
    {
        Assert.Equal(expected, Snap(installed, active).Status);
    }

    // ---------------------------------------------------------------- transitions

    [Fact]
    public void First_reading_healthy_says_nothing()
    {
        var update = new EapoHealthTracker().Update(Snap());
        Assert.Equal(EapoHealthEvent.None, update.Event);
        Assert.False(update.Notify);
    }

    [Fact]
    public void First_reading_broken_is_reported_without_blaming_anything()
    {
        var update = new EapoHealthTracker().Update(Snap(active: false));
        Assert.Equal(EapoHealthEvent.Unhealthy, update.Event);
        Assert.True(update.Notify);
        var text = EapoHealthCopy.Balloon(update, VolumeModes.Eapo)!;
        _out.WriteLine(text);
        Assert.Contains("isn't switched on", text);
        Assert.DoesNotContain("just stopped", text); // it may have been broken for weeks
    }

    [Fact]
    public void Active_to_inactive_is_reported_as_the_transition_it_is()
    {
        var tracker = new EapoHealthTracker();
        tracker.Update(Snap());
        var update = tracker.Update(Snap(active: false, at: T0.AddMinutes(5)));

        Assert.Equal(EapoHealthEvent.Lost, update.Event);
        Assert.True(update.Notify);
        // The whole point of detecting the TRANSITION: the app watched it happen, so it can name
        // the usual cause instead of being generic.
        var text = EapoHealthCopy.Balloon(update, VolumeModes.Eapo)!;
        _out.WriteLine(text);
        Assert.Contains("just stopped", text);
        Assert.Contains("Windows update", text);
    }

    [Fact]
    public void Staying_broken_is_reported_once_not_on_every_check()
    {
        var tracker = new EapoHealthTracker();
        Assert.True(tracker.Update(Snap(active: false)).Notify);

        // Twelve hours of five-minute polls against a machine nobody has fixed.
        for (int i = 1; i <= 144; i++)
        {
            var update = tracker.Update(Snap(active: false, at: T0.AddMinutes(5 * i)));
            Assert.Equal(EapoHealthEvent.None, update.Event);
            Assert.False(update.Notify);
        }
    }

    [Fact]
    public void A_different_fault_replaces_the_banner_without_a_second_balloon()
    {
        var tracker = new EapoHealthTracker();
        Assert.True(tracker.Update(Snap(installed: false, active: false, include: null)).Notify);

        // They install it, and it lands not switched on for this device: still broken, differently.
        var update = tracker.Update(Snap(active: false, at: T0.AddMinutes(1)));
        Assert.Equal(EapoHealthEvent.None, update.Event);
        Assert.False(update.Notify);
        // The banner still has to tell the truth about the NEW fault…
        Assert.Equal("Equalizer APO isn't running on your playback device",
            EapoHealthCopy.BannerTitle(tracker.Current!));
    }

    [Fact]
    public void Recovery_closes_a_loop_this_tracker_opened()
    {
        var tracker = new EapoHealthTracker();
        Assert.True(tracker.Update(Snap(active: false)).Notify);

        var update = tracker.Update(Snap(at: T0.AddMinutes(2)));
        Assert.Equal(EapoHealthEvent.Recovered, update.Event);
        Assert.True(update.Notify);
        Assert.Equal("Equalizer APO is working again on your playback device.",
            EapoHealthCopy.Balloon(update, VolumeModes.Eapo));
    }

    [Fact]
    public void Recovery_nobody_was_told_about_is_not_announced()
    {
        var tracker = new EapoHealthTracker { NotifyCooldown = TimeSpan.FromHours(1) };
        // Broken twice with only a moment in between: the second break is inside the cooldown, so
        // it is never announced — and the recovery from it must not be either, or the user gets a
        // "fixed!" for something they were never told about.
        Assert.True(tracker.Update(Snap(active: false)).Notify);
        Assert.True(tracker.Update(Snap(at: T0.AddSeconds(10))).Notify); // recovery, announced
        Assert.False(tracker.Update(Snap(active: false, at: T0.AddSeconds(20))).Notify); // cooled down
        Assert.False(tracker.Update(Snap(at: T0.AddSeconds(30))).Notify);
    }

    [Fact]
    public void A_flapping_device_cannot_produce_a_stream_of_balloons()
    {
        // A Bluetooth headset dropping and reconnecting every 30 s: each round trip really is a
        // fault and a recovery, so 40 cycles are 80 genuine transitions and would be 80 balloons
        // without a cooldown. What the cooldown promises is that interruptions scale with TIME,
        // not with the fault count: at most one loss (and the recovery that closes it) per
        // 10-minute window, so 40 minutes of constant flapping costs four pairs.
        var tracker = new EapoHealthTracker();
        int balloons = 0;
        for (int i = 0; i < 40; i++)
        {
            if (tracker.Update(Snap(active: false, at: T0.AddSeconds(60 * i))).Notify) balloons++;
            if (tracker.Update(Snap(at: T0.AddSeconds(60 * i + 30))).Notify) balloons++;
        }
        _out.WriteLine($"40 drop/reconnect cycles over 40 minutes = 80 transitions -> {balloons} balloons");
        Assert.Equal(8, balloons);

        // And inside a single cooldown window it really is just the one pair.
        var quiet = new EapoHealthTracker();
        int inOneWindow = 0;
        for (int i = 0; i < 18; i++)
        {
            if (quiet.Update(Snap(active: false, at: T0.AddSeconds(30 * i))).Notify) inOneWindow++;
            if (quiet.Update(Snap(at: T0.AddSeconds(30 * i + 15))).Notify) inOneWindow++;
        }
        Assert.Equal(2, inOneWindow);
    }

    [Fact]
    public void The_cooldown_lets_a_genuinely_new_fault_through_later()
    {
        var tracker = new EapoHealthTracker();
        Assert.True(tracker.Update(Snap(active: false)).Notify);
        Assert.True(tracker.Update(Snap(at: T0.AddMinutes(1))).Notify);
        Assert.False(tracker.Update(Snap(active: false, at: T0.AddMinutes(2))).Notify);
        // …and the recovery from a loss nobody was told about is silent too, so a cooled-down
        // fault cannot leak a "working again" for something the user never saw break.
        Assert.False(tracker.Update(Snap(at: T0.AddMinutes(3))).Notify);

        // An hour later it breaks again. That is news.
        Assert.True(tracker.Update(Snap(active: false, at: T0.AddMinutes(63))).Notify);
    }

    // ---------------------------------------------------------------- probe rate

    [Fact]
    public void The_first_probe_always_runs()
    {
        Assert.True(new EapoHealthTracker().ShouldProbe(T0));
    }

    [Fact]
    public void A_burst_of_triggers_collapses_into_one_probe()
    {
        // Resume, device change and unlock all inside a second — the real shape of waking a laptop.
        var tracker = new EapoHealthTracker();
        Assert.True(tracker.ShouldProbe(T0));
        tracker.Update(Snap(at: T0));

        Assert.False(tracker.ShouldProbe(T0.AddMilliseconds(200)));
        Assert.False(tracker.ShouldProbe(T0.AddMilliseconds(900)));
        Assert.True(tracker.ShouldProbe(T0.AddSeconds(3)));
    }

    [Fact]
    public void A_forced_probe_is_never_collapsed()
    {
        // Opening Settings, or finishing a repair: the user is looking at it, so it is re-read
        // however recently the timer did.
        var tracker = new EapoHealthTracker();
        tracker.Update(Snap(at: T0));
        Assert.False(tracker.ShouldProbe(T0.AddMilliseconds(10)));
        Assert.True(tracker.ShouldProbe(T0.AddMilliseconds(10), force: true));
    }

    [Fact]
    public void The_poll_interval_is_modest()
    {
        // A guard on the constant itself: this probe touches the registry and COM, and the fault
        // it looks for happens a few times a year.
        Assert.True(EapoHealthTracker.PollInterval >= TimeSpan.FromMinutes(1));
        Assert.True(EapoHealthTracker.PollInterval <= TimeSpan.FromMinutes(15));
    }

    // ---------------------------------------------------------------- who is told anything at all

    private static Settings WithMode(string mode, params EqBand[] globalBands) =>
        Settings.Default with
        {
            VolumeMode = mode,
            GlobalEq = globalBands.Length == 0 ? null : new EqScopeSetting("(custom)", 0, true, globalBands),
        };

    private static EqBand Band() => new(EqBandType.Peak, 1000, 3, 1.0);

    [Theory]
    // mode                  has EQ   depends on Equalizer APO
    [InlineData(VolumeModes.Eapo, false, true)]   // the keys write its preamp
    [InlineData(VolumeModes.Eapo, true, true)]
    [InlineData(VolumeModes.System, true, true)]  // EQ renders through it in BOTH modes
    [InlineData(VolumeModes.System, false, false)] // skin-and-OSD user: none of their business
    public void Equalizer_APO_is_only_mentioned_to_users_who_depend_on_it(
        string mode, bool hasEq, bool applies)
    {
        var settings = hasEq ? WithMode(mode, Band()) : WithMode(mode);
        Assert.Equal(applies, EapoDependency.Applies(settings));
    }

    [Fact]
    public void A_device_scope_with_bands_counts_the_same_as_the_global_one()
    {
        var settings = Settings.Default with
        {
            VolumeMode = VolumeModes.System,
            DeviceEq = new Dictionary<string, EqScopeSetting>
            {
                ["{0.0.0.00000000}.{11111111-2222-3333-4444-555555555555}"] =
                    new("HD 650", -6.1, true, new[] { Band() }),
            },
        };
        Assert.True(EapoDependency.Applies(settings));
    }

    [Fact]
    public void An_empty_chain_is_not_an_equalizer_however_it_is_named()
    {
        // Opening the editor once leaves PresetName "(custom)" with no bands — the exact shape in
        // this machine's own settings.json. It applies nothing to anything, so it must not make a
        // Windows-volume-mode user start hearing about Equalizer APO.
        var settings = Settings.Default with
        {
            VolumeMode = VolumeModes.System,
            GlobalEq = new EqScopeSetting("(custom)", 0, true, Array.Empty<EqBand>()),
        };
        Assert.False(EapoDependency.HasConfiguredEq(settings));
        Assert.False(EapoDependency.Applies(settings));
    }

    [Fact]
    public void A_bypassed_chain_still_counts()
    {
        // Bypass is the editor's A/B toggle. Un-bypassing must not be the moment a user finds out
        // Equalizer APO has been detached for a week.
        var settings = Settings.Default with
        {
            VolumeMode = VolumeModes.System,
            GlobalEq = new EqScopeSetting("(custom)", 0, Enabled: false, new[] { Band() }),
        };
        Assert.True(EapoDependency.Applies(settings));
    }

    [Fact]
    public void Forgetting_what_it_saw_does_not_forget_what_it_said()
    {
        // A user toggles their only EQ band off and on again while Equalizer APO is detached.
        // The fault is news again (the tracker was reset), but the balloon is not — the cooldown
        // survives a reset, so the toggle cannot be used to summon one per toggle.
        var tracker = new EapoHealthTracker();
        Assert.True(tracker.Update(Snap(active: false)).Notify);

        tracker.Reset();
        Assert.Null(tracker.Current);

        var update = tracker.Update(Snap(active: false, at: T0.AddSeconds(5)));
        Assert.Equal(EapoHealthEvent.Unhealthy, update.Event); // judged fresh…
        Assert.False(update.Notify);                           // …but not announced again

        // An hour later, still broken and still freshly judged: now it is worth saying again.
        tracker.Reset();
        Assert.True(tracker.Update(Snap(active: false, at: T0.AddMinutes(61))).Notify);
    }

    // ---------------------------------------------------------------- the words

    [Fact]
    public void Eapo_mode_copy_states_the_trade_the_second_button_makes()
    {
        var body = EapoHealthCopy.BannerBody(Snap(active: false), VolumeModes.Eapo);
        _out.WriteLine(body);
        Assert.Contains("Windows volume mode", body);
        // The honest half: the user is giving up the preamp, not the equalizer.
        Assert.Contains("only the equalizer needs Equalizer APO", body);
        Assert.Contains("Windows update", body);
    }

    [Fact]
    public void System_mode_copy_never_claims_the_volume_is_broken()
    {
        // In Windows volume mode the loudness rides the endpoint, so it works regardless. Saying
        // otherwise would be crying wolf, and offering the mode switch would be a no-op.
        foreach (var snapshot in new[] { Snap(active: false), Snap(installed: false, active: false, include: null) })
        {
            var body = EapoHealthCopy.BannerBody(snapshot, VolumeModes.System);
            _out.WriteLine(body);
            Assert.Contains("volume", body);
            Assert.DoesNotContain("Switching to Windows volume mode", body);
            Assert.Contains("Your volume", body); // "Your volume works" / "Your volume still works"
        }
    }

    [Fact]
    public void The_repair_button_never_offers_to_re_enable_something_that_is_not_installed()
    {
        Assert.Equal("Re-enable Equalizer APO", EapoHealthCopy.RepairButtonText(Snap(active: false)));
        Assert.Equal("Set up Equalizer APO",
            EapoHealthCopy.RepairButtonText(Snap(installed: false, active: false, include: null)));
    }

    [Fact]
    public void Every_unhealthy_reading_has_a_title_a_body_and_a_balloon()
    {
        // No combination may fall through to an empty string — a banner with no words is worse
        // than no banner.
        foreach (var installed in new[] { true, false })
        foreach (var active in new[] { true, false })
        foreach (var include in new bool?[] { true, false, null })
        foreach (var mode in new[] { VolumeModes.Eapo, VolumeModes.System })
        {
            if (installed && !active && include == null) continue; // unreachable: include is read when installed
            var snapshot = Snap(installed, installed && active, include);
            if (snapshot.Healthy) continue;

            Assert.NotEmpty(EapoHealthCopy.BannerTitle(snapshot));
            Assert.NotEmpty(EapoHealthCopy.BannerBody(snapshot, mode));
            var update = new EapoHealthUpdate(snapshot, EapoHealthEvent.Unhealthy, Notify: true);
            Assert.NotEmpty(EapoHealthCopy.Balloon(update, mode)!);
        }
    }

    [Fact]
    public void Nothing_is_said_when_there_is_nothing_to_say()
    {
        Assert.Null(EapoHealthCopy.Balloon(
            new EapoHealthUpdate(Snap(active: false), EapoHealthEvent.None, Notify: false), VolumeModes.Eapo));
    }
}
