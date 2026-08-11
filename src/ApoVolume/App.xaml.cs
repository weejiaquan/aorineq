using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows;
using ApoVolume.Core;
using ApoVolume.Input;
using ApoVolume.UI;

namespace ApoVolume;

public partial class App : System.Windows.Application
{
    private const string MutexName = "ApoVolume_SingleInstance";
    private const string ShowEventName = "ApoVolume_ShowOsd";

    private Mutex? _mutex;
    private bool _ownsMutex;
    private EventWaitHandle? _showEvent;
    private KeyboardHook? _hook;
    private ApoWriter? _writer;
    private TrayIcon? _tray;
    private OsdWindow? _osd;
    private SkinOsdWindow? _skinOsd;
    private bool _useSkinOsd;
    private string? _loadedSkinFolder;
    private string? _loadedSkinStamp;
    private SettingsWindow? _settingsWindow;
    private SkinDesignerWindow? _skinDesigner;
    private OnboardingWindow? _onboarding;
    private VolumeState _state = new();
    private string _settingsPath = "";
    // Single source of truth for everything persisted to settings.json. Every field (volume
    // percent/mute, RunAsAdmin, all OSD fields) is updated here via `with { }` before SaveSettings
    // serializes it — SaveSettings must never build a fresh Settings from a handful of fields, or
    // every change clobbers the rest of what's on disk.
    private Settings _settings = Settings.Default;
    private bool _uacDeclined;
    private bool _togglingRunAsAdmin;
    private bool _togglingAutostart;
    // Monotonic token for SettingsWindow state syncs (dispatcher thread only): each sync bumps it
    // before its async autostart query, and only the newest token may apply its result — an older
    // in-flight query (e.g. from opening Settings mid-toggle) can no longer overwrite fresher state.
    private int _stateSyncVersion;
    private readonly Coalescer _settingsSaver = new(TimeSpan.FromMilliseconds(50));

    private static string ExePath => Environment.ProcessPath!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--setup"))
        {
            try
            {
                RunElevatedSetup();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "apo-volume", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }
            Shutdown();
            return;
        }

        // Settings must be loaded before the elevation bounce below decides whether to relaunch.
        _settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "apo-volume", "settings.json");
        var settings = Settings.Load(_settingsPath);
        _settings = settings;

        // Cheap probe so a second launch doesn't pay for a pointless UAC prompt via the
        // elevated bounce below: if an instance is already running, its named event exists
        // and we can just signal it and exit. The mutex below remains the authoritative
        // single-instance check — this is purely an optimization for the common case.
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var existing))
            {
                existing.Set();
                existing.Dispose();
                Shutdown();
                return;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // TryOpenExisting throws (rather than returning false) when the named event
            // exists but access is denied — same denial shape as the mutex/event creation
            // below, so it gets the same fail-fast treatment instead of an unhandled crash.
            ShowInstanceConflictDialogAndShutdown();
            return;
        }

        // Must run before the mutex is created: the elevated child claims the mutex itself,
        // so the exiting (non-elevated) parent must not hold it.
        if (BounceToElevatedOrContinue(e.Args))
            return;

        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);
            if (!isFirstInstance)
            {
                // Losing the mutex race with the show-event probe above having found nothing
                // usually means the old instance is mid-teardown: its event is already disposed
                // (so signaling would create a fresh event nobody listens to and this launch
                // would exit alongside the dying one — leaving nothing running), but it still
                // holds the mutex for a few more milliseconds. Waiting briefly acquires the mutex
                // the moment teardown finishes, letting this launch take over as the instance.
                try
                {
                    isFirstInstance = _mutex.WaitOne(TimeSpan.FromSeconds(3));
                }
                catch (AbandonedMutexException)
                {
                    isFirstInstance = true; // old instance died holding it; ownership transferred
                }
            }
            _ownsMutex = isFirstInstance;
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            if (!isFirstInstance)
            {
                // A healthy instance really is running (simultaneous-start race past the probe):
                // its event exists by now, so this opens rather than creates it.
                _showEvent.Set(); // ask the running instance to show its slider
                Shutdown();
                return;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // A restrictive token-owner policy (e.g. the named objects already exist, owned
            // by a different account/integrity level) can deny access outright here. Fail
            // fast instead of letting this surface as an unhandled-exception crash.
            ShowInstanceConflictDialogAndShutdown();
            return;
        }

        // First-run onboarding: without Equalizer APO the app cannot function at all, so a
        // missing install gets the guided wizard instead of the old fail-fast error box. The
        // wizard downloads/starts the official installer and verifies; declining exits.
        if (EapoDetection.Detect() == EapoStatus.NotInstalled)
        {
            bool proceed = false;
            var wizard = new OnboardingWindow(blocking: true);
            wizard.Completed += p => proceed = p;
            wizard.ShowDialog();
            if (!proceed)
            {
                Shutdown();
                return;
            }
        }

        string configDir;
        try
        {
            configDir = ApoPaths.GetConfigDir();
            _writer = new ApoWriter(configDir);
            // The probe reuses _writer (its EnsureInclude doubles as the config.txt write check),
            // so startup needs exactly one ApoWriter and one include pass. On the elevated-setup
            // path the child process added the include line itself, so no retry is needed here.
            EnsureWritableOrElevate(_writer);

            _state = new VolumeState(settings.Percent, settings.Muted);
            _state.StepPercent = settings.StepPercent;

            _writer.WriteFailing += () => Dispatcher.BeginInvoke(() =>
                _tray?.ShowWarning("Volume changes are not reaching Equalizer APO (apo-volume.txt is not writable)."));
            _writer.StartIncludeGuard();

            _osd = new OsdWindow();
            _osd.PercentChangedByUser += OnOsdPercentChanged;

            _tray = new TrayIcon();
            // Opening the slider changes no state, so it only shows the OSD — a full Render here
            // would pointlessly rewrite apo-volume.txt and re-persist settings on every tray click.
            _tray.OpenRequested += () => ShowOsd(interactive: true);
            _tray.MuteToggleRequested += () => { _state.ToggleMute(); Render(interactive: false); };
            _tray.SettingsRequested += OpenSettings;
            _tray.ExitRequested += BeginShutdown;

            ApplyOsdConfig(settings); // needs _tray to exist first (skin-load failure balloons a warning)

            if (_uacDeclined)
                _tray.ShowWarning("Not elevated — volume keys won't work in elevated games.");

            // Installed-but-inactive is NOT blocking (running EAPO on a non-default device is
            // legitimate) — one balloon pointing at the Settings setup guide.
            if (EapoDetection.Detect() == EapoStatus.InstalledInactive)
                _tray.ShowWarning("Equalizer APO isn't enabled on your current playback device — "
                    + "volume changes won't be audible there. See Settings → Setup guide.");

            // Elevated-startup reconciliation.
            if (Elevation.IsElevated && _settings.RunAsAdmin)
            {
                if (new ScheduledTaskAutostart().IsEnabled())
                {
                    // Self-heal: schtasks /Create /F is idempotent, so re-running Enable
                    // refreshes the task's target path in case the exe moved since it was
                    // created. Comparing against the task's registered command line isn't
                    // cheaply checkable (would need another schtasks query + XML parse), so
                    // this re-Enable is unconditional — elevated startup only, so it's rare.
                    try
                    {
                        new ScheduledTaskAutostart().Enable(ExePath);
                    }
                    catch (InvalidOperationException ex)
                    {
                        _tray?.ShowWarning(ex.Message);
                    }
                }
                else if (new Autostart().IsEnabled())
                {
                    // Run-key autostart is on but the scheduled task isn't, and we actually
                    // have the rights to create it now — migrate it over. Gated behind the
                    // Run-key check so this doesn't create needless schtasks churn when
                    // autostart was never on at all.
                    ApplyAutostart(true);
                }
            }
            else if (Elevation.IsElevated && !_settings.RunAsAdmin && new ScheduledTaskAutostart().IsEnabled())
            {
                // Reverse case: RunAsAdmin is off (or was turned off) but a scheduled task
                // from an earlier elevated/RunAsAdmin session is still registered. We're
                // elevated right now, so we have the rights to remove it — register the Run
                // key instead, under the current (non-admin) mode.
                ApplyAutostart(true);
            }

            // Hook construction can fail (e.g. another process/policy blocks WH_KEYBOARD_LL);
            // kept inside this try so that failure hits the same friendly fail-fast dialog below.
            _hook = new KeyboardHook();
            _hook.VolumeUp += () => { _state.Up(); Render(interactive: false); };
            _hook.VolumeDown += () => { _state.Down(); Render(interactive: false); };
            _hook.MuteToggle += () => { _state.ToggleMute(); Render(interactive: false); };
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or InvalidOperationException or IOException
            or System.ComponentModel.Win32Exception)
        {
            System.Windows.MessageBox.Show(ex.Message, "apo-volume", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // second-instance listener
        var waiter = new Thread(() =>
        {
            try
            {
                while (_showEvent.WaitOne())
                {
                    if (Dispatcher.HasShutdownStarted) break;
                    // Same as the tray's OpenRequested: a second launch just wants the slider
                    // visible — no state changed, so no Render.
                    Dispatcher.BeginInvoke(() => ShowOsd(interactive: true));
                }
            }
            catch (Exception) { } // shutdown races (disposed handle / stopped dispatcher) must not crash the process
        }) { IsBackground = true };
        waiter.Start();

        // apply persisted volume to APO immediately at startup. Null-forgiving: same
        // construction-order guarantee as Render() above — the try block either assigns
        // both fields or returns before reaching here.
        _writer!.WriteVolume(_state.CurrentDb);
        _tray!.Update(_state.Percent, _state.Muted);

        // Fresh-launch flags only (also used by E2E automation): when an instance is already
        // running, a second launch signals the OSD as usual — these are not IPC commands.
        if (e.Args.Contains("--settings"))
            OpenSettings();
        if (e.Args.Contains("--onboarding"))
            OpenOnboarding();
    }

    /// <summary>
    /// If RunAsAdmin is on, we're not already elevated, and the caller didn't pass the
    /// "--no-elevate" escape hatch, relaunches this executable elevated (no extra args — the
    /// elevated instance just starts normally) and shuts this instance down. Returns true if
    /// OnStartup should stop here (either handed off to the elevated child, or shutting down).
    /// On UAC decline, continues non-elevated and remembers to balloon once the tray exists.
    /// </summary>
    private bool BounceToElevatedOrContinue(string[] args)
    {
        if (!_settings.RunAsAdmin || Elevation.IsElevated || args.Contains("--no-elevate"))
            return false;

        if (TryRelaunchElevatedAndShutdown(forwardSettingsFlag: args.Contains("--settings")))
            return true;

        _uacDeclined = true;
        return false;
    }

    /// <summary>Relaunches this executable elevated via UAC and shuts this instance down.
    /// Returns false, having taken no action, if UAC was declined. Only the --settings flag is
    /// forwarded (whitelisted, so no argument-quoting concerns) — the elevated child otherwise
    /// starts normally.</summary>
    private bool TryRelaunchElevatedAndShutdown(bool forwardSettingsFlag = false)
    {
        System.Diagnostics.Process? proc;
        try
        {
            proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ExePath)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = forwardSettingsFlag ? "--settings" : "",
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // UAC declined
        }

        if (proc is null)
            return false; // couldn't start — treat like a decline, stay running non-elevated

        BeginShutdown();
        return true;
    }

    /// <summary>Intentional-exit path (tray Exit, elevated relaunch): disposes the show event
    /// BEFORE Shutdown() starts draining the dispatcher. The waiter thread ignores signals once
    /// shutdown has started, so a relaunch probing the still-alive event during that drain would
    /// signal a corpse and exit — leaving nothing running. With the event gone up front, such a
    /// relaunch falls through to the mutex and takes over via its teardown-takeover wait instead.
    /// OnExit's dispose remains for the other paths (Dispose is idempotent).</summary>
    private void BeginShutdown()
    {
        _showEvent?.Dispose();
        Shutdown();
    }

    /// <summary>Shown when a named single-instance object (mutex/event) already exists but is
    /// owned by a different account/security context within this session — the no-prefix
    /// object names here are session-local, so this is never literally "another session,"
    /// just a same-session/different-token-owner denial.</summary>
    private void ShowInstanceConflictDialogAndShutdown()
    {
        System.Windows.MessageBox.Show(
            "apo-volume appears to be running under a different account or security context in this session.",
            "apo-volume", MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(1);
    }

    /// <summary>Raw, blocking mechanism call selected by <see cref="_settings"/>.RunAsAdmin — schtasks calls
    /// can take ~100ms, so callers on the dispatcher thread must run this via Task.Run.</summary>
    private void ApplyAutostartMechanism(bool enable)
    {
        if (enable)
        {
            if (_settings.RunAsAdmin) new ScheduledTaskAutostart().Enable(ExePath);
            else new Autostart().Enable(ExePath);
        }
        else
        {
            if (_settings.RunAsAdmin) new ScheduledTaskAutostart().Disable();
            else new Autostart().Disable();
        }
    }

    /// <summary>Raw, blocking disable of the mechanism NOT selected by <see cref="_settings"/>.RunAsAdmin.</summary>
    private void DisableOtherMechanism()
    {
        if (_settings.RunAsAdmin) new Autostart().Disable();
        else new ScheduledTaskAutostart().Disable();
    }

    /// <summary>
    /// Enables/disables autostart via whichever mechanism <see cref="_settings"/>.RunAsAdmin selects
    /// (scheduled task when elevated-on-logon is wanted, Run key otherwise). Enabling also
    /// disables the other mechanism, but only once the new one is confirmed registered — this is
    /// how migration between mechanisms happens (e.g. from <see cref="OnRunAsAdminToggled"/>)
    /// without ever losing a working autostart entry if the new one couldn't be created (e.g. no
    /// elevation yet). Disabling always attempts both mechanisms, independently, so that turning
    /// autostart off actually turns it off regardless of which one is currently registered. Every
    /// operation is best-effort: schtasks operations can fail without elevation, and failures are
    /// reported via a tray balloon rather than thrown.
    /// </summary>
    /// <remarks>Synchronous — only safe to call before the dispatcher message loop is pumping
    /// (i.e. from <see cref="OnStartup"/>). UI-triggered call sites must use
    /// <see cref="ApplyAutostartAsync"/> instead so schtasks work doesn't block the UI thread.</remarks>
    private void ApplyAutostart(bool enable)
    {
        bool enabled = true;
        try
        {
            ApplyAutostartMechanism(enable);
        }
        catch (InvalidOperationException ex)
        {
            enabled = false;
            _tray?.ShowWarning(ex.Message);
        }

        // Enabling: only touch the other mechanism once the new one is confirmed registered.
        // Disabling: always attempt the other mechanism too, regardless of whether the first
        // disable succeeded — each is independent and best-effort.
        if (enable && !enabled) return;

        try
        {
            DisableOtherMechanism();
        }
        catch (InvalidOperationException ex)
        {
            _tray?.ShowWarning(ex.Message);
        }
    }

    /// <summary>Same contract as <see cref="ApplyAutostart"/>, but runs the blocking schtasks/registry
    /// work on a thread-pool thread. Safe to call from dispatcher-thread event handlers: the awaits
    /// resume back on the calling (UI) thread, so the catch blocks' tray balloons run there too.</summary>
    private async Task ApplyAutostartAsync(bool enable)
    {
        bool enabled = true;
        try
        {
            await Task.Run(() => ApplyAutostartMechanism(enable));
        }
        catch (InvalidOperationException ex)
        {
            enabled = false;
            _tray?.ShowWarning(ex.Message);
        }

        // See ApplyAutostart's remarks: enabling only cascades on success; disabling always
        // attempts both mechanisms.
        if (enable && !enabled) return;

        try
        {
            await Task.Run(DisableOtherMechanism);
        }
        catch (InvalidOperationException ex)
        {
            _tray?.ShowWarning(ex.Message);
        }
    }

    /// <summary>Blocking — only safe pre-loop (see <see cref="ApplyAutostart"/>'s remarks).</summary>
    private static bool IsAutostartEnabled() => new Autostart().IsEnabled() || new ScheduledTaskAutostart().IsEnabled();

    private static Task<bool> IsAutostartEnabledAsync() => Task.Run(IsAutostartEnabled);

    /// <summary>Re-queries autostart state and re-syncs the SettingsWindow, unless a newer sync
    /// started while this one's query was in flight (see <see cref="_stateSyncVersion"/>).</summary>
    private async Task SyncSettingsWindowStateAsync()
    {
        int version = ++_stateSyncVersion;
        bool autostartEnabled = await IsAutostartEnabledAsync();
        if (version != _stateSyncVersion) return; // superseded — the newer sync's result wins
        _settingsWindow?.SyncState(autostartEnabled, _settings.RunAsAdmin, Elevation.IsElevated, _settings);
    }

    /// <summary>Handles the SettingsWindow RunAsAdminChanged event: persists the choice, offers an
    /// immediate elevated relaunch when turning on, migrates the autostart mechanism to match, and
    /// suggests a restart when turning off while currently elevated.</summary>
    private async void OnRunAsAdminToggled(bool on)
    {
        // The modal MessageBox below pumps messages, so a second checkbox click can re-enter this
        // handler before the first call finishes; defuse that instead of racing two toggles.
        if (_togglingRunAsAdmin) return;
        _togglingRunAsAdmin = true;
        try
        {
            _settings = _settings with { RunAsAdmin = on };

            // Through the coalescer, then flushed: posting replaces any stale pending save
            // (latest-wins), so an older snapshot queued just before this toggle can never land
            // after — and overwrite — this one; Flush() then writes synchronously, so the settings
            // are on disk before TryRelaunchElevatedAndShutdown below can start an elevated child
            // that reads them. Saves the full _settings (not a partial reconstruction), so every
            // other persisted field — OSD style/position/etc. — survives an admin-toggle.
            SaveSettings();
            _settingsSaver.Flush();

            bool autostartEnabled = await IsAutostartEnabledAsync();

            if (on)
            {
                if (!Elevation.IsElevated)
                {
                    var choice = System.Windows.MessageBox.Show(
                        "Restart apo-volume elevated now?", "apo-volume", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (choice == MessageBoxResult.Yes && TryRelaunchElevatedAndShutdown())
                        return;
                }

                // Migrate Run-key -> scheduled task. If we're still not elevated this is
                // best-effort: ApplyAutostartAsync balloons the failure and leaves the Run key
                // registration intact (see its remarks), so it self-corrects on the next elevated start.
                if (autostartEnabled) await ApplyAutostartAsync(true);
            }
            else
            {
                // Migrate scheduled task -> Run key. Deleting the task may need elevation; best-effort.
                if (autostartEnabled) await ApplyAutostartAsync(true);

                if (Elevation.IsElevated)
                    System.Windows.MessageBox.Show(
                        "Restart apo-volume without elevation for this change to take full effect.",
                        "apo-volume", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            await SyncSettingsWindowStateAsync();
        }
        finally
        {
            _togglingRunAsAdmin = false;
        }
    }

    /// <summary>Handles the SettingsWindow AutostartChanged event: applies the change and
    /// re-syncs the checkbox against the actual resulting state, so a failed enable (e.g. no
    /// elevation yet for the scheduled task) un-checks the box instead of leaving it stuck on.</summary>
    private async void OnAutostartToggled(bool on)
    {
        // Same reentrancy concern as OnRunAsAdminToggled: guard against a second checkbox
        // click re-entering before the first async call finishes.
        if (_togglingAutostart) return;
        _togglingAutostart = true;
        try
        {
            await ApplyAutostartAsync(on);
            await SyncSettingsWindowStateAsync();
        }
        finally
        {
            _togglingAutostart = false;
        }
    }

    /// <summary>Prefers the informational version (includes the git commit hash after '+' when
    /// built from a git checkout), trimmed to just the version proper; falls back to the
    /// four-part assembly version if no informational version attribute is present.</summary>
    private static string GetVersionString()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString() ?? "dev";
    }

    /// <summary>Lazily creates (or re-syncs and shows) the single SettingsWindow instance.
    /// Queries autostart state off the dispatcher thread since ScheduledTaskAutostart.IsEnabled()
    /// shells out to schtasks (~100ms); the await resumes back here on the UI thread.</summary>
    private async void OpenSettings()
    {
        int version = ++_stateSyncVersion;
        bool autostartEnabled = await IsAutostartEnabledAsync();

        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(
                autostartEnabled, _settings.RunAsAdmin, Elevation.IsElevated, GetVersionString(), _settings);
            _settingsWindow.AutostartChanged += OnAutostartToggled;
            _settingsWindow.RunAsAdminChanged += OnRunAsAdminToggled;
            _settingsWindow.OsdSettingsChanged += OnOsdSettingsChanged;
            _settingsWindow.SkinDesignerRequested += OpenSkinDesigner;
            _settingsWindow.SetupGuideRequested += OpenOnboarding;
        }
        else if (version == _stateSyncVersion) // an in-flight toggle sync supersedes this open
        {
            // Also rescans the skins folder (inside SyncState -> ApplyOsdSettings/PopulateSkins).
            _settingsWindow.SyncState(autostartEnabled, _settings.RunAsAdmin, Elevation.IsElevated, _settings);
        }

        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    /// <summary>Opens the setup wizard in informational mode (Settings "Setup guide…" and the
    /// --onboarding flag). One window at a time; it closes for real, so no hide/show dance.</summary>
    private void OpenOnboarding()
    {
        if (_onboarding is null)
        {
            _onboarding = new OnboardingWindow(blocking: false);
            _onboarding.Closed += (_, _) => _onboarding = null;
            _onboarding.Show();
        }
        _onboarding.Activate();
    }

    /// <summary>Lazily creates (or re-shows) the single skin designer window.</summary>
    private void OpenSkinDesigner()
    {
        if (_skinDesigner is null)
        {
            _skinDesigner = new SkinDesignerWindow(() => _settings);
            _skinDesigner.SkinSaved += OnSkinSaved;
        }
        _skinDesigner.Show();
        _skinDesigner.Activate();
    }

    /// <summary>A designer save refreshes the Settings picker, and — when the saved skin is the
    /// one currently rendering — hot-reloads the live OSD (ApplyOsdConfig's content stamp sees
    /// the new file timestamps and rebuilds the window) with a non-interactive show for feedback.</summary>
    private void OnSkinSaved(string name)
    {
        _settingsWindow?.RefreshSkins();
        if (_settings.OsdStyle == OsdStyles.Skin
            && string.Equals(_settings.SkinName, name, StringComparison.OrdinalIgnoreCase))
        {
            ApplyOsdConfig(_settings);
            ShowOsd(interactive: false);
        }
    }

    /// <summary>One render path for every change: APO file, OSD, tray, settings.</summary>
    // Null-forgiving below relies on OnStartup's construction order: _writer/_osd/_tray are all
    // assigned before the keyboard hook, tray, or second-instance listener can dispatch into Render.
    private void Render(bool interactive)
    {
        _writer!.WriteVolume(_state.CurrentDb);
        ShowOsd(interactive);
        _tray!.Update(_state.Percent, _state.Muted);
        SaveSettings();
    }

    /// <summary>Shows the currently active OSD window (skin-driven or the standard OsdWindow) for
    /// the current volume state. Shared by Render (volume/mute changes) and OnOsdSettingsChanged
    /// (so an OSD-only settings change is immediately visible without needing a volume keypress).</summary>
    private void ShowOsd(bool interactive)
    {
        if (_useSkinOsd && _skinOsd is not null)
            _skinOsd.ShowVolume(_state.Percent, _state.Muted, interactive);
        else
            _osd!.ShowVolume(_state.Percent, _state.Muted, interactive);
    }

    /// <summary>Shared handler for both OsdWindow's and SkinOsdWindow's PercentChangedByUser —
    /// same contract either way: only ever raised by direct user interaction with the OSD, never
    /// by ShowVolume's own programmatic updates.</summary>
    private void OnOsdPercentChanged(int percent)
    {
        _state.SetPercent(percent);
        Render(interactive: true);
    }

    /// <summary>Handles the SettingsWindow OsdSettingsChanged event: merges the OSD-only fields
    /// into <see cref="_settings"/> (Percent/Muted/RunAsAdmin are untouched — see <see
    /// cref="OsdSettings"/>'s remarks), keeps VolumeState.StepPercent in sync so the global hotkeys
    /// immediately use the new step size, applies the config live (style/position/animation, and
    /// rebuilding the skin window when the skin selection changed), persists via the existing
    /// coalesced save, and shows the OSD once non-interactively so the effect is visible without a
    /// volume keypress.</summary>
    private void OnOsdSettingsChanged(OsdSettings o)
    {
        _settings = _settings with
        {
            OsdStyle = o.Style,
            SkinName = o.SkinName,
            OsdAnchor = o.Anchor,
            OsdOffsetX = o.OffsetX,
            OsdOffsetY = o.OffsetY,
            HideDelaySeconds = o.HideDelaySeconds,
            AnimationEnabled = o.AnimationEnabled,
            AnimationMs = o.AnimationMs,
            StepPercent = o.StepPercent,
        };
        _state.StepPercent = _settings.StepPercent;
        ApplyOsdConfig(_settings);
        SaveSettings();
        ShowOsd(interactive: false);
    }

    /// <summary>
    /// Applies OSD style/position/behavior settings and decides which window renders volume
    /// changes: the standard <see cref="OsdWindow"/> (always kept up to date via ApplyConfig), or
    /// a skin-driven <see cref="SkinOsdWindow"/> when <c>OsdStyle == "skin"</c> and the named skin
    /// loads successfully. Safe to call repeatedly (e.g. whenever settings change) — the skin
    /// window is only recreated when the folder it points at, or that folder's content (per
    /// <see cref="GetSkinContentStamp"/>), actually changes. On a missing or
    /// invalid skin — including one that passes <see cref="SkinLoader"/>'s header-only validation
    /// but fails to actually decode (truncated/corrupt PNG data) — balloons the reason via the
    /// tray and falls back to dark-pill in memory only; <c>Settings.OsdStyle</c>/<c>SkinName</c>
    /// on disk are never touched here, so a later fix to the skin folder (or a restart after
    /// Equalizer APO etc. becomes available) retries cleanly.
    /// </summary>
    private void ApplyOsdConfig(Settings s)
    {
        _osd!.ApplyConfig(s);

        if (s.OsdStyle != OsdStyles.Skin || string.IsNullOrEmpty(s.SkinName))
        {
            _useSkinOsd = false;
            _loadedSkinFolder = null;
            _loadedSkinStamp = null;
            _skinOsd?.Hide();
            return;
        }

        var info = SkinLoader.Load(Path.Combine(ApoPaths.GetSkinsRoot(), s.SkinName));
        if (!info.IsValid)
        {
            _tray?.ShowWarning(info.Error ?? "Skin not found.");
            _useSkinOsd = false; // in-memory fallback only — see remarks above
            _loadedSkinFolder = null;
            _loadedSkinStamp = null;
            _skinOsd?.Hide();
            return;
        }

        string? stamp = GetSkinContentStamp(info);
        if (_skinOsd is null || _loadedSkinFolder != info.Folder || _loadedSkinStamp != stamp)
        {
            // SkinLoader only validates the PNG header (signature + IHDR), not the full image
            // data — a truncated/corrupt file still passes info.IsValid but can throw from
            // BitmapImage.EndInit inside SkinOsdWindow's constructor (imaging can raise several
            // exception types: NotSupportedException, FileFormatException, etc.). That must be
            // contained exactly like an invalid SkinInfo, not left to crash the process.
            try
            {
                var next = new SkinOsdWindow(info);
                _skinOsd?.Close(); // real teardown, unlike OsdWindow's Close-cancels-and-Hides pattern
                _skinOsd = next;
                _skinOsd.PercentChangedByUser += OnOsdPercentChanged;
                _loadedSkinFolder = info.Folder;
                _loadedSkinStamp = stamp;
            }
            catch (Exception ex)
            {
                _tray?.ShowWarning(ex.Message);
                _useSkinOsd = false; // in-memory fallback only — see remarks above
                _loadedSkinFolder = null;
                _loadedSkinStamp = null;
                _skinOsd?.Hide();
                return;
            }
        }
        _skinOsd.ApplyConfig(s);
        _useSkinOsd = true;
        _osd!.Hide(); // symmetric with the skin->standard branches above: only one OSD window is
                      // ever visible at a time, so switching TO skin must hide the standard one too.
    }

    /// <summary>Content stamp for a skin folder: the max last-write-time (in ticks, as a string) across
    /// empty.png, full.png, and skin.json (if present). Used alongside the folder path in
    /// <see cref="ApplyOsdConfig"/> so in-place edits to a skin's files (via Rescan or a style
    /// round-trip) are detected and the <see cref="SkinOsdWindow"/> is recreated instead of reused.
    /// Returns null on any I/O failure, which forces a reload attempt on the next call.</summary>
    private static string? GetSkinContentStamp(SkinInfo info)
    {
        try
        {
            long stamp = Math.Max(
                File.GetLastWriteTimeUtc(info.EmptyPath).Ticks,
                File.GetLastWriteTimeUtc(info.FullPath).Ticks);
            string jsonPath = Path.Combine(info.Folder, "skin.json");
            if (File.Exists(jsonPath))
                stamp = Math.Max(stamp, File.GetLastWriteTimeUtc(jsonPath).Ticks);
            return stamp.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Merges the live volume state into <see cref="_settings"/> and (coalesced) persists
    /// the full, up-to-date Settings — every field, not just Percent/Muted/RunAsAdmin, so an OSD
    /// settings change followed shortly by a volume change (or vice versa) never loses the other.</summary>
    private void SaveSettings()
    {
        _settings = _settings with { Percent = _state.Percent, Muted = _state.Muted, StepPercent = _state.StepPercent };
        var s = _settings;
        _settingsSaver.Post(() =>
        {
            try
            {
                s.Save(_settingsPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        });
    }

    /// <summary>Normal runs must be able to write apo-volume.txt and config.txt. If not, self-elevate once.</summary>
    // Note: if this method itself runs elevated (e.g. a normal-mode session started via "Run as
    // administrator" rather than the RunAsAdmin/ScheduledTask path), the probe below trivially
    // succeeds without ever granting the Users ACL that RunElevatedSetup would add — a later
    // non-elevated run would then fail the probe and self-correct via --setup as usual.
    private static void EnsureWritableOrElevate(ApoWriter writer)
    {
        try
        {
            File.AppendAllText(writer.VolumeFilePath, ""); // create-or-touch; throws if unwritable
            writer.EnsureInclude();
            return; // all writable — no elevation ever needed
        }
        catch (UnauthorizedAccessException)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(ExePath, "--setup")
            {
                UseShellExecute = true,
                Verb = "runas",
            };
            var proc = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start elevated setup.");
            proc.WaitForExit();
            if (proc.ExitCode != 0)
                throw new InvalidOperationException(
                    "Elevated setup failed — make the Equalizer APO config folder writable and retry.");
        }
    }

    /// <summary>Runs elevated (--setup): create volume file, grant Users modify on it and on
    /// config.txt (the include-guard watcher must be able to re-append the Include line from a
    /// non-elevated session when an external tool rewrites config.txt), add include line.</summary>
    private static void RunElevatedSetup()
    {
        var configDir = ApoPaths.GetConfigDir();
        // The grant goes on the config DIRECTORY, inheritable to children: external tools (Peace)
        // rewrite config.txt via temp-file-and-rename, and a replacement file carries no file-level
        // ACE — it inherits from the directory, so only a directory grant survives that pattern.
        GrantUsersModifyOnDirectory(configDir);

        var volumePath = Path.Combine(configDir, ApoWriter.VolumeFileName);
        if (!File.Exists(volumePath))
            File.WriteAllText(volumePath, ApoWriter.FormatPreamp(0) + Environment.NewLine);
        GrantUsersModify(volumePath);

        using var w = new ApoWriter(configDir);
        // Created empty (if absent) before the ACL grant so EnsureInclude below — and every later
        // non-elevated write — happens against a file Users can already modify.
        if (!File.Exists(w.ConfigTxtPath))
            File.WriteAllText(w.ConfigTxtPath, "");
        GrantUsersModify(w.ConfigTxtPath);
        w.EnsureInclude();
    }

    /// <summary>File-level grant for files that already exist (directory inheritance only applies
    /// to files created after <see cref="GrantUsersModifyOnDirectory"/> ran).</summary>
    private static void GrantUsersModify(string path)
    {
        var fi = new FileInfo(path);
        var acl = fi.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Modify, AccessControlType.Allow));
        fi.SetAccessControl(acl);
    }

    private static void GrantUsersModifyOnDirectory(string dir)
    {
        var di = new DirectoryInfo(dir);
        var acl = di.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        di.SetAccessControl(acl);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hook?.Dispose();
        _tray?.Dispose();
        _writer?.Dispose();
        // The show event must go away BEFORE the mutex is released: a relaunch probes the named
        // event first, and while it still exists the new process would signal this dying instance
        // and exit — leaving nothing running. With the event gone first, the relaunch falls
        // through to the mutex, which is released right after, and starts normally.
        _showEvent?.Dispose();
        if (_ownsMutex)
        {
            // Release before disposing so a successor launched during our teardown acquires the
            // mutex immediately instead of waiting on abandoned-mutex semantics.
            try { _mutex!.ReleaseMutex(); }
            catch (ApplicationException) { } // not owned (shouldn't happen; flag tracks ownership)
        }
        _mutex?.Dispose();
        _settingsSaver.Dispose();
        base.OnExit(e);
    }
}
