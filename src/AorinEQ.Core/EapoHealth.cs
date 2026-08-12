namespace AorinEQ.Core;

/// <summary>One reading of whether Equalizer APO is actually going to process this machine's
/// audio — not whether it is installed, which is the question that misleads people.
///
/// The registration that makes Equalizer APO run lives in the AUDIO ENDPOINT's property store,
/// not in Equalizer APO's own installation. A Windows update that replaces or reinstalls the
/// audio driver resets that property store, so Equalizer APO stays perfectly installed and
/// silently stops processing. Nothing about the install looks wrong; the sound just stops
/// changing. That is why this reading has three independent facts in it rather than one status.
///
/// <paramref name="IncludeLinePresent"/> is nullable on purpose: null means "could not read
/// config.txt right now" (another tool holds it for a moment), which must never be reported as a
/// fault — see <see cref="Healthy"/>.</summary>
public sealed record EapoHealthSnapshot(
    bool Installed,
    bool ActiveOnDevice,
    bool? IncludeLinePresent,
    string? EndpointGuid,
    DateTimeOffset CheckedAt)
{
    /// <summary>Everything AorinEQ needs is in place. An UNREADABLE config.txt counts as healthy:
    /// a transient lock is not a fault, and treating it as one would flash a banner at the user
    /// every time another tool saved a file.</summary>
    public bool Healthy => Installed && ActiveOnDevice && IncludeLinePresent != false;

    /// <summary>The same three-state summary <see cref="EapoDetection.Detect"/> reports, derived
    /// from this reading so the two can never disagree.</summary>
    public EapoStatus Status =>
        !Installed ? EapoStatus.NotInstalled
        : ActiveOnDevice ? EapoStatus.Active
        : EapoStatus.InstalledInactive;

    /// <summary>Reads all three facts off the machine: the install from the registry, the
    /// per-endpoint registration against the CURRENT default render device, and AorinEQ's Include
    /// line in config.txt. Never throws — every reader underneath it is already conservative, and
    /// a health probe that can crash the app is worse than the fault it looks for.</summary>
    public static EapoHealthSnapshot Read(DateTimeOffset now)
    {
        var install = EapoDetection.GetInstallPath();
        var guid = AudioEndpoint.EndpointGuid(AudioEndpoint.GetDefaultRenderEndpointId());
        bool active = install is not null && EapoDetection.IsActiveOnEndpoint(guid);
        // GetInstallPath only returns a path whose "config" subdirectory exists, so this is the
        // same directory ApoPaths.GetConfigDir resolves — without its throw-if-missing contract,
        // which a probe must not have.
        bool? include = install is null
            ? null
            : ApoWriter.HasIncludeLine(Path.Combine(install, "config", "config.txt"));
        return new EapoHealthSnapshot(install is not null, active, include, guid, now);
    }
}

/// <summary>Whether this user's setup actually depends on Equalizer APO — the gate in front of
/// every word AorinEQ says about it.
///
/// AorinEQ is three products in one box: a skinnable volume OSD, a volume control, and a
/// parametric equalizer. Only two of those need Equalizer APO, and plenty of people run the first
/// alone. Telling someone whose only interest is the OSD that "Equalizer APO isn't running on
/// your playback device" is a warning about a program they did not ask for, about a consequence
/// they will never notice — which is how a genuinely useful alert teaches people to ignore
/// alerts.
///
/// Two ways to depend on it, and both are read from the user's own settings rather than guessed:
/// APO preamp volume mode makes the volume keys write Equalizer APO's preamp, and any configured
/// EQ band is rendered into Equalizer APO's config in BOTH volume modes. A user in Windows volume
/// mode with no bands anywhere gets nothing: no balloon, no banner, not even a health row. The
/// moment they add a band or switch mode, it starts applying.</summary>
public static class EapoDependency
{
    /// <summary>The one test. Every surface that mentions Equalizer APO is behind this.</summary>
    public static bool Applies(Settings settings) =>
        settings.VolumeMode != VolumeModes.System || HasConfiguredEq(settings);

    /// <summary>Whether any EQ chain would actually reach the audio. Bands, not preset names: a
    /// scope can carry the name "(custom)" with an empty chain purely because the editor was
    /// opened once, and an empty chain applies nothing to anything.
    ///
    /// A BYPASSED chain still counts. Bypass is the editor's A/B toggle, so a user who has bands
    /// and has muted them for a moment is still an EQ user — and un-bypassing must not be the
    /// moment they discover Equalizer APO has been detached for a week.</summary>
    public static bool HasConfiguredEq(Settings settings) =>
        HasBands(settings.GlobalEq)
        || (settings.DeviceEq is not null && settings.DeviceEq.Values.Any(HasBands));

    private static bool HasBands(EqScopeSetting? scope) => scope?.Bands is { Count: > 0 };
}

/// <summary>What a new reading means for the user, as opposed to what it says.</summary>
public enum EapoHealthEvent
{
    /// <summary>Nothing worth saying: unchanged, or a change between two shades of the same
    /// fault.</summary>
    None,

    /// <summary>The first reading of the session, and it is already broken. The cause is unknown
    /// — it may have been broken for weeks — so the wording cannot blame anything.</summary>
    Unhealthy,

    /// <summary>It was working and now is not. This is the transition worth naming: the app
    /// watched it happen, so the message can say what usually causes it.</summary>
    Lost,

    /// <summary>It was broken and now works.</summary>
    Recovered,
}

/// <summary>One update's outcome: the reading, what it means, and whether the user should be
/// interrupted about it.</summary>
public sealed record EapoHealthUpdate(
    EapoHealthSnapshot Snapshot, EapoHealthEvent Event, bool Notify);

/// <summary>Turns a stream of readings into the handful of moments worth telling the user about.
///
/// Two separate jobs, both about not being a nuisance:
///
/// PROBE RATE. Readings are requested from four places — a default-device change, a session
/// unlock, a resume from sleep, and a periodic timer — and the first three routinely arrive
/// together (unlocking a laptop that resumed and re-enumerated its audio devices fires all
/// three within a second). <see cref="ShouldProbe"/> collapses that burst into one reading, so
/// the registry and COM work happens once. A caller that knows the user is looking (opening
/// Settings, finishing a repair) passes force and always gets a fresh one.
///
/// NOTIFICATION RATE. A balloon is raised on the EDGE into a fault, never while one persists, so
/// a permanently misconfigured machine is told once and then left alone. On top of that a
/// cooldown covers flapping: a Bluetooth headset that drops and reconnects changes the default
/// device both ways, and each round trip is a genuine fault-and-recovery pair that would
/// otherwise be a genuine pair of balloons. Recovery is announced only when this tracker actually
/// announced the loss — it closes a loop it opened, and never opens one of its own.
///
/// Pure and clock-free: every decision comes from <see cref="EapoHealthSnapshot.CheckedAt"/>, so
/// the whole policy is testable without waiting for real time to pass.</summary>
public sealed class EapoHealthTracker
{
    /// <summary>How often to re-read health when nothing has happened. Deliberately modest: the
    /// fault this looks for is caused by Windows updates and driver reinstalls, which happen at
    /// most a few times a month, and every probe touches the registry and COM.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    /// <summary>Minimum gap between unforced probes — the burst collapse described above.</summary>
    public TimeSpan MinProbeInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Minimum gap between fault balloons, whatever the transitions in between.</summary>
    public TimeSpan NotifyCooldown { get; init; } = TimeSpan.FromMinutes(10);

    private EapoHealthSnapshot? _last;
    private DateTimeOffset _lastProbe = DateTimeOffset.MinValue;
    private DateTimeOffset _lastNotified = DateTimeOffset.MinValue;
    private bool _lossAnnounced;

    /// <summary>The most recent reading, or null before the first one.</summary>
    public EapoHealthSnapshot? Current => _last;

    /// <summary>Whether a reading taken at <paramref name="now"/> is worth the work. The first
    /// one always is, and <paramref name="force"/> always is.</summary>
    public bool ShouldProbe(DateTimeOffset now, bool force = false) =>
        force || _last is null || now - _lastProbe >= MinProbeInterval;

    /// <summary>Forgets what it has SEEN without forgetting what it has SAID.
    ///
    /// Used when monitoring stops applying to this user at all (see
    /// <see cref="EapoDependency"/>) and later starts again: the next reading must be judged
    /// fresh — a fault that was already present while nobody was watching is news the first time
    /// it matters — while the notification cooldown deliberately survives, so toggling the
    /// equalizer on and off cannot be used to summon a balloon per toggle.</summary>
    public void Reset()
    {
        _last = null;
        _lastProbe = DateTimeOffset.MinValue;
    }

    /// <summary>Records a reading and reports what it means.</summary>
    public EapoHealthUpdate Update(EapoHealthSnapshot next)
    {
        var previous = _last;
        _last = next;
        _lastProbe = next.CheckedAt;

        var change =
            next.Healthy ? (previous is { Healthy: false } ? EapoHealthEvent.Recovered : EapoHealthEvent.None)
            : previous is null ? EapoHealthEvent.Unhealthy
            : previous.Healthy ? EapoHealthEvent.Lost
            : EapoHealthEvent.None;

        bool notify = false;
        switch (change)
        {
            case EapoHealthEvent.Unhealthy:
            case EapoHealthEvent.Lost:
                if (next.CheckedAt - _lastNotified >= NotifyCooldown)
                {
                    notify = true;
                    _lastNotified = next.CheckedAt;
                    _lossAnnounced = true;
                }
                break;
            case EapoHealthEvent.Recovered:
                if (_lossAnnounced)
                {
                    notify = true;
                    _lossAnnounced = false;
                }
                break;
        }
        return new EapoHealthUpdate(next, change, notify);
    }
}

/// <summary>Every sentence the health monitor says, in one place and free of jargon.
///
/// It lives in Core, next to the state machine that decides WHEN to say something, because what
/// is said depends entirely on which of the three facts failed and on which volume mode the user
/// is in — and getting that wrong is how an app tells someone their volume is broken when it is
/// not, or stays quiet when it is. That is a rule, so it is tested.</summary>
public static class EapoHealthCopy
{
    /// <summary>Headline for the Settings banner. Only meaningful for an unhealthy reading.</summary>
    public static string BannerTitle(EapoHealthSnapshot s) =>
        !s.Installed ? "Equalizer APO isn't installed"
        : !s.ActiveOnDevice ? "Equalizer APO isn't running on your playback device"
        : "Equalizer APO isn't reading AorinEQ's settings";

    /// <summary>The explanation under the headline. Says what happened, what it costs the user
    /// RIGHT NOW in the mode they are actually in, and — in APO preamp mode — states the trade
    /// the second button makes plainly, because "switch to Windows volume" is only good advice if
    /// the user knows they are giving up the equalizer's preamp and not the equalizer.</summary>
    public static string BannerBody(EapoHealthSnapshot s, string volumeMode)
    {
        bool eapo = volumeMode != VolumeModes.System;
        if (!s.Installed)
        {
            return eapo
                ? "Your volume keys write Equalizer APO's preamp, and there's no Equalizer APO to write to — "
                    + "so the keys move a number nothing reads. Switching to Windows volume mode makes your "
                    + "volume work again immediately; only the equalizer needs Equalizer APO."
                : "Your volume works — it's the normal Windows volume. The equalizer needs Equalizer APO, "
                    + "so no EQ is being applied until it's installed.";
        }
        if (!s.ActiveOnDevice)
        {
            const string cause = "Equalizer APO is installed, but it's no longer switched on for the device "
                + "you're listening through. This usually happens after a Windows update replaces the audio "
                + "driver: the setting lives on the device, not in Equalizer APO, so a new driver starts "
                + "without it. Nothing is broken — it just needs switching back on.";
            return eapo
                ? cause + " Until then your volume keys are writing a preamp nothing reads, which is why the "
                    + "volume doesn't move. Switching to Windows volume mode makes your volume work again "
                    + "immediately; only the equalizer needs Equalizer APO."
                : cause + " Your volume still works — it's the normal Windows volume — but no EQ is reaching "
                    + "your audio.";
        }
        return "Equalizer APO is running, but its configuration no longer points at AorinEQ's settings file, "
            + "so nothing AorinEQ writes reaches your audio. AorinEQ puts that line back automatically; if "
            + "this keeps coming back, another equalizer tool is rewriting the same file.";
    }

    /// <summary>Label for the banner's repair button. An install that is present but not switched
    /// on is a one-minute fix in Equalizer APO's own Configurator; a missing install is a
    /// different job, and saying "re-enable" would be a lie.</summary>
    public static string RepairButtonText(EapoHealthSnapshot s) =>
        s.Installed ? "Re-enable Equalizer APO" : "Set up Equalizer APO";

    /// <summary>The tray balloon, or null when this update is not worth interrupting for. Short
    /// enough to survive Windows' balloon truncation, and it always names the consequence rather
    /// than the mechanism.</summary>
    public static string? Balloon(EapoHealthUpdate update, string volumeMode)
    {
        if (!update.Notify) return null;
        bool eapo = volumeMode != VolumeModes.System;
        var s = update.Snapshot;

        if (update.Event == EapoHealthEvent.Recovered)
            return "Equalizer APO is working again on your playback device.";

        string consequence = eapo
            ? " Your volume keys won't change the loudness until it's fixed — open Settings to re-enable it, "
                + "or switch to Windows volume mode."
            : " Your EQ isn't reaching your audio — open Settings to fix it.";

        if (!s.Installed)
            return "Equalizer APO isn't installed." + consequence;
        if (!s.ActiveOnDevice)
            return (update.Event == EapoHealthEvent.Lost
                ? "Equalizer APO just stopped running on your playback device — a Windows update or driver "
                    + "change usually causes this."
                : "Equalizer APO isn't switched on for your current playback device.") + consequence;
        return "Equalizer APO stopped reading AorinEQ's settings file." + consequence;
    }
}
