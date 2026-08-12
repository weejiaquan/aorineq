using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows;
using AorinEQ.Core;
using AorinEQ.Input;
using AorinEQ.UI;

namespace AorinEQ;

public partial class App : System.Windows.Application
{
    private const string MutexName = "AorinEQ_SingleInstance";
    private const string ShowEventName = "AorinEQ_ShowOsd";

    private Mutex? _mutex;
    private bool _ownsMutex;
    private EventWaitHandle? _showEvent;
    private KeyboardHook? _hook;
    private ApoWriter? _writer;
    private EndpointVolume? _endpointVolume;
    private TrayIcon? _tray;
    private OsdWindow? _osd;
    private SkinOsdWindow? _skinOsd;
    private bool _useSkinOsd;
    private string? _loadedSkinFolder;
    private string? _loadedSkinStamp;
    private SettingsWindow? _settingsWindow;
    private SkinDesignerWindow? _skinDesigner;
    private EqEditorWindow? _eqEditor;
    private OnboardingWindow? _onboarding;
    private OnboardingWindow? _startupWizard; // blocking first-run wizard, while it's up
    private DeviceVolumeStates _deviceStates = new(Settings.Default);
    private string _settingsPath = "";
    // Single source of truth for everything persisted to settings.json. Every field (volume
    // percent/mute, RunAsAdmin, all OSD fields) is updated here via `with { }` before SaveSettings
    // serializes it — SaveSettings must never build a fresh Settings from a handful of fields, or
    // every change clobbers the rest of what's on disk.
    private Settings _settings = Settings.Default;
    private bool _uacDeclined;
    private bool _togglingRunAsAdmin;
    private bool _togglingAutostart;
    // aorineq:// links arriving from second launches (via the spool) or this launch's args
    // are processed strictly one at a time — each shows a modal confirm dialog.
    private readonly ProtocolSpool _protocolSpool = new(ProtocolSpool.DefaultPath);
    private readonly Queue<string> _pendingProtocolLinks = new();
    private bool _processingProtocolLinks;
    private System.Windows.Threading.DispatcherTimer? _updateTimer;
    private bool _updateCheckRunning;
    // Monotonic token for SettingsWindow state syncs (dispatcher thread only): each sync bumps it
    // before its async autostart query, and only the newest token may apply its result — an older
    // in-flight query (e.g. from opening Settings mid-toggle) can no longer overwrite fresher state.
    private int _stateSyncVersion;
    private readonly Coalescer _settingsSaver = new(TimeSpan.FromMilliseconds(50));

    private static string ExePath => Environment.ProcessPath!;

    /// <summary>Volume state of the ACTIVE device — what keys/OSD/tray act on. Follows the
    /// Windows default render device via <see cref="OnDefaultDeviceChanged"/>.</summary>
    private VolumeState ActiveState => _deviceStates.Active;

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
                System.Windows.MessageBox.Show(ex.Message, "AorinEQ", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }
            Shutdown();
            return;
        }

        // Settings must be loaded before the elevation bounce below decides whether to relaunch.
        _settingsPath = Path.Combine(ApoPaths.GetStateRoot(), "settings.json");
        bool firstRun = !File.Exists(_settingsPath); // brand-new install: mode-choice onboarding below
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
                // A browser-launched aorineq:// link rides this second launch: spool it for
                // the running instance BEFORE waking it, so the signal finds the link waiting.
                PostProtocolLinkForRunningInstance(e.Args);
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
                PostProtocolLinkForRunningInstance(e.Args);
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

        // This launch is becoming THE instance: any links a crashed/killed session left in the
        // spool are stale user intent — discard rather than pop surprise install dialogs. (A link
        // spooled by a second launch in this same instant is lost too; its signal then just shows
        // the OSD. Clicking a link during the app's own startup is rare enough to accept that.)
        _protocolSpool.TakeAll();

        // The second-instance listener starts as soon as the named event exists: a second launch
        // during first-run onboarding must activate the wizard (not vanish into a signal nobody
        // hears). The dispatch target is dynamic — wizard while one is up, the OSD once built.
        var waiter = new Thread(() =>
        {
            try
            {
                while (_showEvent.WaitOne())
                {
                    if (Dispatcher.HasShutdownStarted) break;
                    Dispatcher.BeginInvoke(OnSecondLaunchSignal);
                }
            }
            catch (Exception) { } // shutdown races (disposed handle / stopped dispatcher) must not crash the process
        }) { IsBackground = true };
        waiter.Start();

        // First-run onboarding: a brand-new install (no settings.json) chooses its volume mode
        // first — preselected from EAPO detection — and flows into the install wizard only when
        // APO preamp mode needs it. Existing installs keep their mode and only see the blocking
        // install wizard when eapo mode requires an EAPO that's missing (system mode never
        // needs EAPO, so it is never gated).
        if (firstRun)
        {
            bool proceed = false;
            var preselect = EapoDetection.Detect() == EapoStatus.Active ? VolumeModes.Eapo : VolumeModes.System;
            _startupWizard = new OnboardingWindow(blocking: true, modeChoice: preselect,
                autoUpdate: _settings.AutoUpdate);
            _startupWizard.ModeSelected += m => _settings = _settings with { VolumeMode = m };
            _startupWizard.AutoUpdateSelected += on => _settings = _settings with { AutoUpdate = on };
            _startupWizard.Completed += p => proceed = p;
            _startupWizard.ShowDialog();
            _startupWizard = null;
            if (!proceed)
            {
                Shutdown();
                return;
            }
            settings = _settings;
            // Persist the choice NOW: nothing else writes settings.json until the first volume
            // or settings change, and without the file this wizard would reappear every launch.
            try
            {
                settings.Save(_settingsPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        else if (_settings.VolumeMode == VolumeModes.Eapo && EapoDetection.Detect() == EapoStatus.NotInstalled)
        {
            // eapo mode without Equalizer APO cannot function at all, so a missing install gets
            // the guided wizard instead of the old fail-fast error box. The wizard downloads and
            // starts the official installer and verifies; declining exits.
            bool proceed = false;
            _startupWizard = new OnboardingWindow(blocking: true);
            _startupWizard.Completed += p => proceed = p;
            _startupWizard.ShowDialog();
            _startupWizard = null;
            if (!proceed)
            {
                Shutdown();
                return;
            }
        }

        bool systemMode = _settings.VolumeMode == VolumeModes.System;
        try
        {
            _deviceStates = new DeviceVolumeStates(settings);
            _deviceStates.SwitchTo(AudioEndpoint.GetDefaultRenderEndpointId());

            // Both modes need the endpoint backend now: system mode for the volume itself,
            // eapo mode for default-device tracking (per-device state switching) and the
            // mute-handover reads.
            SetupEndpointVolume();
            if (systemMode)
            {
                // Quiet best-effort pipeline: system mode still renders per-device EQ through
                // the config file (volume components parked at 0 — the idempotent repark that
                // also repairs a crash mid mode-transition), but must never pop a UAC setup
                // prompt just for EQ; a missing/unwritable EAPO simply means no EQ rendering.
                TryBuildEapoPipelineQuiet();
            }
            else
            {
                BuildEapoPipeline();
            }

            _osd = new OsdWindow();
            _osd.PercentChangedByUser += OnOsdPercentChanged;

            _tray = new TrayIcon();
            // Opening the slider changes no state, so it only shows the OSD — a full Render here
            // would pointlessly rewrite aorineq.txt and re-persist settings on every tray click.
            _tray.OpenRequested += () => ShowOsd(interactive: true);
            _tray.MuteToggleRequested += () => { ActiveState.ToggleMute(); Render(interactive: false); };
            _tray.SettingsRequested += OpenSettings;
            _tray.EqualizerRequested += OpenEqEditor;
            _tray.MenuOpening += RefreshTrayEqPresets;
            _tray.EqPresetSelected += OnTrayEqPresetSelected;
            _tray.ExitRequested += BeginShutdown;

            ApplyOsdConfig(settings); // needs _tray to exist first (skin-load failure balloons a warning)

            if (_uacDeclined)
                _tray.ShowWarning("Not elevated — volume keys won't work in elevated games.");

            // Installed-but-inactive is NOT blocking (running EAPO on a non-default device is
            // legitimate) — one balloon pointing at the Settings setup guide. eapo mode only:
            // in system mode volume changes are audible regardless of where EAPO is enabled.
            if (!systemMode && EapoDetection.Detect() == EapoStatus.InstalledInactive)
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
            _hook.VolumeUp += () => { ActiveState.Up(); Render(interactive: false); };
            _hook.VolumeDown += () => { ActiveState.Down(); Render(interactive: false); };
            _hook.MuteToggle += () => { ActiveState.ToggleMute(); Render(interactive: false); };
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or InvalidOperationException or IOException
            or System.ComponentModel.Win32Exception)
        {
            System.Windows.MessageBox.Show(ex.Message, "AorinEQ", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // Apply persisted volume immediately at startup. System mode instead ADOPTS the device's
        // current state (no startup volume jump) and still renders the config file (EQ blocks +
        // parked-0 volume). If the device is unreadable the saved percent stays and the first
        // keypress pushes it via Render.
        if (systemMode)
            AdoptEndpointState();
        RenderEqConfig();
        _tray!.Update(ActiveState.Percent, ActiveState.Muted);

        // Protocol links + auto-update, both post-init: neither may block or fail startup.
        try
        {
            var registration = new ProtocolRegistration();
            if (_settings.ProtocolLinksEnabled)
                registration.Register(ExePath); // idempotent; re-points a moved exe
            else
                registration.Unregister(); // fail-closed: purge any stale registration when off
        }
        catch (InvalidOperationException ex)
        {
            _tray!.ShowWarning(ex.Message);
        }
        SetupAutoUpdate();
        if (e.Args.Contains("--updated"))
            _tray!.ShowInfo($"Updated to AorinEQ {GetVersionString()}.");

        // Fresh-launch flags only (also used by E2E automation): when an instance is already
        // running, a second launch signals the OSD as usual — these are not IPC commands.
        if (e.Args.Contains("--settings"))
            OpenSettings();
        if (e.Args.Contains("--onboarding"))
            OpenOnboarding();

        // A protocol link launched THIS instance (nothing was running): handle it now that the
        // tray/OSD pipeline exists. Runs after the flag handling so a combined launch behaves.
        // Ignored outright when links are disabled — even if a stale OS registration launched us.
        var linkArg = e.Args.FirstOrDefault(ProtocolLink.IsProtocolArg);
        if (linkArg is not null && _settings.ProtocolLinksEnabled)
            EnqueueProtocolLinks(new[] { linkArg });
    }

    /// <summary>Second-launch side of the protocol handoff: spool the link (if this launch
    /// carries one) for the running instance to pick up on the show-event signal. Best-effort —
    /// an unwritable spool just means the running instance shows its OSD. Reads
    /// <see cref="Settings.ProtocolLinksEnabled"/> off the freshly-loaded settings (this is a
    /// short-lived second process, so its <see cref="_settings"/> is current) and refuses to
    /// spool when links are off — fail-closed even against a stale OS registration.</summary>
    private void PostProtocolLinkForRunningInstance(string[] args)
    {
        if (!_settings.ProtocolLinksEnabled) return;
        var link = args.FirstOrDefault(ProtocolLink.IsProtocolArg);
        if (link is null) return;
        try
        {
            _protocolSpool.Post(link);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
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

        // Whitelisted flags survive the bounce (no argument-quoting concerns by construction —
        // protocol links are additionally screened for whitespace/quote smuggling since the
        // join below is unquoted).
        var forwarded = string.Join(' ', args.Where(
            a => a is "--settings" or "--onboarding" || ProtocolLink.IsSafeToForward(a)));
        if (TryRelaunchElevatedAndShutdown(forwarded))
            return true;

        _uacDeclined = true;
        return false;
    }

    /// <summary>Relaunches this executable elevated via UAC and shuts this instance down.
    /// Returns false, having taken no action, if UAC was declined. Only whitelisted flags are
    /// ever passed through <paramref name="forwardedArgs"/> — the elevated child otherwise
    /// starts normally.</summary>
    private bool TryRelaunchElevatedAndShutdown(string forwardedArgs = "")
    {
        System.Diagnostics.Process? proc;
        try
        {
            proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ExePath)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = forwardedArgs,
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
            "AorinEQ appears to be running under a different account or security context in this session.",
            "AorinEQ", MessageBoxButton.OK, MessageBoxImage.Error);
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
                        "Restart AorinEQ elevated now?", "AorinEQ", MessageBoxButton.YesNo, MessageBoxImage.Question);
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
                        "Restart AorinEQ without elevation for this change to take full effect.",
                        "AorinEQ", MessageBoxButton.OK, MessageBoxImage.Information);
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
    private async void OpenSettings() => await OpenSettingsAsync(focusSkins: false);

    /// <summary><paramref name="focusSkins"/> scrolls to and focuses the skin picker — where an
    /// <c>aorineq://open?page=skins</c> link lands. It must happen after the await, because
    /// the window may not exist until then.</summary>
    private async Task OpenSettingsAsync(bool focusSkins)
    {
        int version = ++_stateSyncVersion;
        bool autostartEnabled = await IsAutostartEnabledAsync();

        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(
                autostartEnabled, _settings.RunAsAdmin, Elevation.IsElevated, GetVersionString(), _settings);
            _settingsWindow.AutostartChanged += OnAutostartToggled;
            _settingsWindow.RunAsAdminChanged += OnRunAsAdminToggled;
            _settingsWindow.ProtocolLinksChanged += OnProtocolLinksToggled;
            _settingsWindow.AutoUpdateChanged += OnAutoUpdateToggled;
            _settingsWindow.CheckUpdatesRequested += () => _ = RunUpdateCheckAsync(interactive: true);
            _settingsWindow.VolumeModeChanged += OnVolumeModeChanged;
            _settingsWindow.OsdSettingsChanged += OnOsdSettingsChanged;
            _settingsWindow.SkinDesignerRequested += OpenSkinDesigner;
            _settingsWindow.SetupGuideRequested += OpenOnboarding;
            _settingsWindow.EqualizerRequested += OpenEqEditor;
        }
        else if (version == _stateSyncVersion) // an in-flight toggle sync supersedes this open
        {
            // Also rescans the skins folder (inside SyncState -> ApplyOsdSettings/PopulateSkins).
            _settingsWindow.SyncState(autostartEnabled, _settings.RunAsAdmin, Elevation.IsElevated, _settings);
        }

        _settingsWindow.Show();
        _settingsWindow.Activate();
        if (focusSkins)
            _settingsWindow.FocusSkins();
    }

    /// <summary>Opens the setup wizard in informational mode (Settings "Setup guide…" and the
    /// --onboarding flag), starting on the volume-mode page preselected with the current mode.
    /// One window at a time; it closes for real, so no hide/show dance.</summary>
    private void OpenOnboarding()
    {
        if (_onboarding is null)
        {
            _onboarding = new OnboardingWindow(blocking: false, modeChoice: _settings.VolumeMode,
                autoUpdate: _settings.AutoUpdate);
            _onboarding.ModeSelected += OnWizardModeSelected;
            _onboarding.AutoUpdateSelected += OnAutoUpdateToggled;
            _onboarding.Closed += (_, _) =>
            {
                _onboarding = null;
                OnOnboardingClosed();
            };
            _onboarding.Show();
        }
        _onboarding.Activate();
    }

    /// <summary>The setup guide's mode page applied a choice: same live-switch path as the
    /// Settings radios, plus a radio re-sync in case the Settings window is open.</summary>
    private void OnWizardModeSelected(string mode)
    {
        OnVolumeModeChanged(mode);
        _ = SyncSettingsWindowStateAsync();
    }

    /// <summary>The setup guide closed: if eapo mode is still waiting on a writer (it was
    /// selected while EAPO was missing), retry now that the wizard may have installed it —
    /// including the mute handover the immediate-switch path runs, or a mute latched on the
    /// endpoint would keep eapo mode silent after the user unmutes in AorinEQ.</summary>
    private void OnOnboardingClosed()
    {
        if (_settings.VolumeMode == VolumeModes.Eapo && _writer is null && TryBuildEapoPipeline())
        {
            RenderEqConfig();
            HandMuteBackToPreamp();
        }
    }

    /// <summary>Lazily creates (or re-shows) the single EQ editor window. The editor is
    /// closed for real when dismissed (its loopback capture must fully tear down), so a
    /// fresh instance is built per open.</summary>
    private void OpenEqEditor()
    {
        if (_eqEditor is null)
        {
            _eqEditor = new EqEditorWindow(
                () => _settings,
                () => _deviceStates.ActiveId,
                deviceId => SystemModeActive || deviceId is null
                    ? null
                    : _deviceStates.Snapshot().TryGetValue(deviceId, out var v)
                        ? VolumeState.ToDb(v.Percent, v.Muted)
                        : 0.0);
            _eqEditor.ScopeChanged += OnEqScopeChanged;
            _eqEditor.EditorModeChanged += OnEqEditorModeChanged;
            _eqEditor.Closed += (_, _) => _eqEditor = null;
            if (_writer is null)
                _tray?.ShowWarning("Equalizer APO isn't set up (or its config folder isn't "
                    + "writable) — EQ changes won't reach your audio until it is. See Settings → Setup guide.");
            _eqEditor.Show();
        }
        _eqEditor.Activate();
    }

    /// <summary>The editor's Simple/Advanced switch: persist the choice so the editor opens the
    /// same way next time (and so <see cref="EqEditorModes.Resolve"/> stops guessing).</summary>
    private void OnEqEditorModeChanged(string mode)
    {
        if (mode == _settings.EqEditorMode)
            return;
        _settings = _settings with { EqEditorMode = mode };
        SaveSettings();
    }

    /// <summary>An EQ editor edit landed: merge the scope into settings (Global or the
    /// device's entry), re-render the config file, and persist. The dictionaries are copied —
    /// Settings instances are treated as immutable snapshots everywhere else.</summary>
    private void OnEqScopeChanged(string? deviceId, EqScopeSetting scope)
    {
        if (deviceId is null)
        {
            _settings = _settings with { GlobalEq = scope };
        }
        else
        {
            var map = _settings.DeviceEq is null
                ? new Dictionary<string, EqScopeSetting>()
                : new Dictionary<string, EqScopeSetting>(_settings.DeviceEq);
            map[deviceId] = scope;
            _settings = _settings with { DeviceEq = map };
        }
        RenderEqConfig();
        SaveSettings();
    }

    /// <summary>The EQ scope the tray submenu targets: the active device when one is known,
    /// otherwise the global scope.</summary>
    private EqScopeSetting? ActiveTrayEqScope() =>
        _deviceStates.ActiveId is { } id
            ? _settings.DeviceEq is not null && _settings.DeviceEq.TryGetValue(id, out var scope) ? scope : null
            : _settings.GlobalEq;

    /// <summary>Right before the tray menu opens: list the preset files and check the active
    /// device's current one.</summary>
    private void RefreshTrayEqPresets() =>
        _tray?.SetEqPresets(PresetStore.List(ApoPaths.GetPresetsRoot()),
            ActiveTrayEqScope()?.PresetName ?? "");

    /// <summary>Tray "EQ preset" pick: load the preset and apply it to the active device's
    /// scope (global scope when no device is known) — on-the-fly switching without opening
    /// the editor. Keeps the scope's on/off state.</summary>
    private void OnTrayEqPresetSelected(string name)
    {
        if (PresetStore.Load(ApoPaths.GetPresetsRoot(), name) is not { } preset)
        {
            _tray?.ShowWarning($"Preset '{name}' couldn't be loaded.");
            return;
        }
        var current = ActiveTrayEqScope();
        var scope = new EqScopeSetting(preset.Name, preset.PreampDb,
            current?.Enabled ?? true, preset.Bands.ToArray());
        OnEqScopeChanged(_deviceStates.ActiveId, scope);
        _eqEditor?.RefreshFromApp();
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

    /// <summary>A second launch signaled the show event. Same contract as the tray's
    /// OpenRequested — nothing changed, just bring the right surface forward: the first-run
    /// wizard while onboarding, a protocol link's confirm dialog when one was spooled,
    /// otherwise the OSD (a no-op before the OSD exists).</summary>
    private void OnSecondLaunchSignal()
    {
        if (_startupWizard is { IsVisible: true })
        {
            // Drain-and-discard any spooled link: the tray/OSD pipeline doesn't exist during
            // first-run setup, and leaving it in the spool would surface it on an unrelated
            // later second launch. Same stale-intent policy as the cold-start discard.
            _protocolSpool.TakeAll();
            _startupWizard.Activate();
            return;
        }
        // The OSD pipeline isn't built yet — this signal raced early startup (the waiter thread
        // starts before the wizard is shown and before _osd/_tray exist). Discard any spooled
        // link rather than process it half-initialized; the same stale-intent policy applies.
        if (_osd is null)
        {
            _protocolSpool.TakeAll();
            return;
        }
        var links = _protocolSpool.TakeAll();
        if (links.Count > 0)
        {
            EnqueueProtocolLinks(links);
            return;
        }
        ShowOsd(interactive: true);
    }

    /// <summary>Queues protocol links and drains the queue one link at a time — each link shows
    /// a modal confirm dialog, and a second link arriving mid-dialog must wait its turn, not
    /// stack a second dialog via re-entrant dispatch.</summary>
    private async void EnqueueProtocolLinks(IEnumerable<string> links)
    {
        if (!_settings.ProtocolLinksEnabled) return; // toggled off since the link was spooled — drop it
        foreach (var link in links)
            _pendingProtocolLinks.Enqueue(link);
        if (_processingProtocolLinks) return;
        _processingProtocolLinks = true;
        try
        {
            while (_pendingProtocolLinks.Count > 0)
            {
                // Re-checked every iteration: the await in HandleProtocolLinkAsync (download +
                // dialog) gives the user time to disable links mid-drain — honor that at once.
                if (!_settings.ProtocolLinksEnabled)
                {
                    _pendingProtocolLinks.Clear();
                    break;
                }
                try
                {
                    await HandleProtocolLinkAsync(_pendingProtocolLinks.Dequeue());
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // e.g. an unreachable skins root — this is an async void handler, so
                    // anything escaping here would crash the process.
                    _tray?.ShowWarning($"Skin install failed: {ex.Message}");
                }
            }
        }
        finally
        {
            _processingProtocolLinks = false;
        }
    }

    /// <summary>One aorineq:// link, end to end: strict parse (balloon-only on anything off),
    /// then the action's own handler. Every action that changes state confirms first; the
    /// <c>open</c> action changes nothing, so it just brings a window up.</summary>
    private async Task HandleProtocolLinkAsync(string raw)
    {
        var result = ProtocolLink.Parse(raw);
        if (result.Status == ProtocolParseStatus.UnknownAction)
        {
            _tray?.ShowWarning("This link needs a newer version of AorinEQ.");
            return;
        }
        if (result.Status != ProtocolParseStatus.Ok)
        {
            _tray?.ShowWarning("Invalid AorinEQ link.");
            return;
        }

        var link = result.Link!;
        switch (link.Action)
        {
            case ProtocolLink.InstallSkinAction:
                await HandleSkinInstallAsync(link);
                break;
            case ProtocolLink.ApplyPresetAction:
                await HandleEqPresetLinkAsync(link);
                break;
            case ProtocolLink.AutoEqAction:
                OpenEqEditor();
                _eqEditor?.OpenAutoEqImport(link.Model!);
                break;
            case ProtocolLink.OpenAction:
                await OpenProtocolPageAsync(link.Page!);
                break;
        }
    }

    /// <summary>An <c>install-skin</c> link: the confirm dialog (the trust boundary — nothing
    /// downloads before a click), the gated zip download, the staged
    /// <see cref="SkinArchive.Import"/>, and — for Install &amp; Use — the live switch to the
    /// new skin.</summary>
    private async Task HandleSkinInstallAsync(ProtocolLink link)
    {
        bool overwrites = Directory.Exists(Path.Combine(ApoPaths.GetSkinsRoot(), link.Name));
        var choice = SkinInstallDialog.Confirm(link.Name, new Uri(link.Url!).Host, overwrites);
        if (choice == SkinInstallChoice.Cancel)
            return;

        var staging = Path.Combine(Path.GetTempPath(), "apo-skin-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await GatedDownload.DownloadAsync(link.Url!, staging, SkinArchive.MaxZipBytes,
                GatedDownload.ZipMagic, link.Sha256);
            SkinArchive.Import(staging, ApoPaths.GetSkinsRoot(), link.Name);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _tray?.ShowWarning($"Skin install failed: {ex.Message}");
            return;
        }
        finally
        {
            try { File.Delete(staging); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        if (choice == SkinInstallChoice.InstallAndUse)
        {
            _settings = _settings with { OsdStyle = OsdStyles.Skin, SkinName = link.Name };
            ApplyOsdConfig(_settings); // content stamp differs if the active skin was overwritten
            SaveSettings();
            ShowOsd(interactive: false);
            _ = SyncSettingsWindowStateAsync(); // style/skin combos, if Settings is open
        }
        else
        {
            _settingsWindow?.RefreshSkins();
            _tray?.ShowInfo($"Skin '{link.Name}' installed.");
        }
    }

    /// <summary>Cap on a downloaded ParametricEQ preset. Real files are under a kilobyte; a
    /// megabyte is room for anything legitimate and far below what could hurt.</summary>
    private const long MaxPresetTextBytes = 1024 * 1024;

    /// <summary>An <c>apply-preset&amp;type=eq</c> link: confirm first, then apply. The dialog is
    /// the trust boundary — it shows the source, the target scope and, once the preset is in
    /// hand, the band count, the preamp and the response CURVE, so the user sees the tuning
    /// before accepting it.
    ///
    /// An inline (data=) preset is already decoded. A HOSTED preset is fetched by the dialog, and
    /// only when the user clicks (Preview, or one of the two accept buttons): a page that fires
    /// links must not be able to make this app hit URLs of its choosing. The transport is gated
    /// exactly like every other download (https-only with per-hop revalidation, size cap,
    /// optional sha256 pin) and the file is parsed, never executed.</summary>
    private async Task HandleEqPresetLinkAsync(ProtocolLink link)
    {
        var source = link.Preset is null
            ? new Uri(link.Url!).Host
            : EqPresetLinkDialog.SharedLinkSource;
        var (deviceId, scopeDescription) = ResolveEqLinkScope(link.Scope);
        bool overwrites = PresetStore.List(ApoPaths.GetPresetsRoot())
            .Contains(link.Name, StringComparer.OrdinalIgnoreCase);

        var result = EqPresetLinkDialog.Confirm(link.Name, source, scopeDescription, overwrites,
            link.Preset,
            link.Preset is null ? () => DownloadEqPresetAsync(link) : null);
        if (result.Choice == EqPresetLinkChoice.Cancel || result.Preset is not { } preset)
            return;

        try
        {
            PresetStore.Save(ApoPaths.GetPresetsRoot(), link.Name, preset.Serialize());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _tray?.ShowWarning($"Couldn't save the preset: {ex.Message}");
            return;
        }

        if (result.Choice == EqPresetLinkChoice.ApplyAndSave)
        {
            var current = deviceId is null
                ? _settings.GlobalEq
                : _settings.DeviceEq is not null && _settings.DeviceEq.TryGetValue(deviceId, out var s) ? s : null;
            OnEqScopeChanged(deviceId, new EqScopeSetting(
                link.Name, preset.PreampDb, current?.Enabled ?? true, preset.Bands.ToArray()));
            _eqEditor?.RefreshFromApp();
            _tray?.ShowInfo($"EQ preset '{link.Name}' applied to {scopeDescription}.");
        }
        else
        {
            _eqEditor?.RefreshFromApp();
            _tray?.ShowInfo($"EQ preset '{link.Name}' saved.");
        }
    }

    /// <summary>Fetches and strictly parses a hosted ParametricEQ preset. Called by the confirm
    /// dialog, only after the user clicks. Every failure — transport, size, checksum, or text
    /// that isn't a whole Equalizer APO block — surfaces as an
    /// <see cref="InvalidOperationException"/> the dialog shows inline; the staging file never
    /// outlives the call.</summary>
    private static async Task<EqPreset> DownloadEqPresetAsync(ProtocolLink link)
    {
        var staging = Path.Combine(Path.GetTempPath(),
            "apo-preset-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            await GatedDownload.DownloadAsync(link.Url!, staging, MaxPresetTextBytes,
                GatedDownload.NoMagic, link.Sha256);
            var text = await File.ReadAllTextAsync(staging);
            // Strict: the whole block parses or nothing is applied.
            if (!EqPreset.TryParse(link.Name, text, out var preset, out var parseError))
                throw new InvalidOperationException(
                    $"That link didn't contain a usable Equalizer APO preset. {parseError}");
            if (preset.Bands.Count == 0)
                throw new InvalidOperationException("That link contains no filters.");
            return preset;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Preset download failed: {ex.Message}", ex);
        }
        finally
        {
            try { File.Delete(staging); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Turns a link's requested scope into the scope the app actually has: "global" is
    /// always available, "device" means whatever the user is listening on right now — and when
    /// there IS no active render device, the preset lands on the global chain instead of being
    /// dropped. The description is what the confirm dialog names, so the user always sees where
    /// it will really go.</summary>
    private (string? DeviceId, string Description) ResolveEqLinkScope(string scope)
    {
        if (scope == EqLinkScopes.Global)
            return (null, "the global EQ (every device)");
        if (_deviceStates.ActiveId is not { } activeId)
            return (null, "the global EQ (no active playback device right now)");
        var name = AudioEndpoint.GetRenderEndpoints()
            .FirstOrDefault(e => e.Id == activeId)?.FriendlyName;
        return (activeId, name is null ? "the current playback device" : $"'{name}'");
    }

    /// <summary>An <c>open</c> link: bring up the named window. Nothing changes state, which is
    /// why these links need no confirmation.</summary>
    private async Task OpenProtocolPageAsync(string page)
    {
        switch (page)
        {
            case ProtocolPages.Eq:
                OpenEqEditor();
                break;
            case ProtocolPages.Designer:
                OpenSkinDesigner();
                break;
            case ProtocolPages.Skins:
                await OpenSettingsAsync(focusSkins: true);
                break;
            default:
                await OpenSettingsAsync(focusSkins: false);
                break;
        }
    }

    /// <summary>Four-part assembly version — what release tags compare against (see
    /// <see cref="UpdateChecker.IsNewer"/>'s normalization).</summary>
    private static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    /// <summary>Post-init updater wiring: clean up the previous version's <c>.old</c> image
    /// (retrying once after 30 s — right after an update relaunch it IS the still-exiting old
    /// process), create the 24 h re-check timer, and kick the startup check when enabled.</summary>
    private void SetupAutoUpdate()
    {
        if (!UpdateApplier.TryDeleteOld(ExePath))
        {
            var cleanup = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(30),
            };
            cleanup.Tick += (_, _) =>
            {
                cleanup.Stop();
                UpdateApplier.TryDeleteOld(ExePath);
            };
            cleanup.Start();
        }

        _updateTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromHours(24),
        };
        _updateTimer.Tick += (_, _) => _ = RunUpdateCheckAsync(interactive: false);
        if (_settings.AutoUpdate)
        {
            _updateTimer.Start();
            _ = RunUpdateCheckAsync(interactive: false);
        }
    }

    /// <summary>Handles the Settings/onboarding auto-update toggle: persist, and start/stop the
    /// periodic check (turning it on also checks right away).</summary>
    private void OnAutoUpdateToggled(bool on)
    {
        if (on == _settings.AutoUpdate) return;
        _settings = _settings with { AutoUpdate = on };
        SaveSettings();
        if (on)
        {
            _updateTimer?.Start();
            _ = RunUpdateCheckAsync(interactive: false);
        }
        else
        {
            _updateTimer?.Stop();
        }
        _ = SyncSettingsWindowStateAsync();
    }

    /// <summary>Handles the "Enable aorineq:// links" toggle: persist and register/unregister
    /// the scheme. Failures balloon; the checkbox re-syncs from the persisted setting.</summary>
    private void OnProtocolLinksToggled(bool on)
    {
        if (on == _settings.ProtocolLinksEnabled) return;
        _settings = _settings with { ProtocolLinksEnabled = on };
        SaveSettings();
        if (!on)
            _pendingProtocolLinks.Clear(); // drop anything queued but not yet installed
        try
        {
            var registration = new ProtocolRegistration();
            if (on) registration.Register(ExePath);
            else registration.Unregister();
        }
        catch (InvalidOperationException ex)
        {
            _tray?.ShowWarning(ex.Message);
        }
        _ = SyncSettingsWindowStateAsync();
    }

    /// <summary>One update check + (when an update is out) the download/apply flow.
    /// Background runs (startup, 24 h timer) are silent on everything except a completed swap;
    /// the interactive path (Settings "Check now") reports every outcome via the Settings
    /// status line. Never throws; never blocks startup.</summary>
    private async Task RunUpdateCheckAsync(bool interactive)
    {
        if (_updateCheckRunning) return;
        _updateCheckRunning = true;
        try
        {
            if (interactive)
                _settingsWindow?.SetUpdateStatus("Checking…");
            var result = await UpdateChecker.CheckAsync(CurrentVersion);
            switch (result.Status)
            {
                case UpdateStatus.Error:
                    _settingsWindow?.SetUpdateStatus(result.Error ?? "Update check failed.");
                    return;
                case UpdateStatus.UpToDate:
                    _settingsWindow?.SetUpdateStatus(
                        $"Latest release: {result.Release!.TagName} — you're up to date.");
                    return;
            }

            var release = result.Release!;
            _settingsWindow?.SetUpdateStatus($"Downloading {release.TagName}…");
            await DownloadAndApplyUpdateAsync(release, interactive);
        }
        finally
        {
            _updateCheckRunning = false;
        }
    }

    /// <summary>Downloads a newer release through the gates (sha256 asset REQUIRED, 200 MB cap,
    /// MZ magic) and applies the in-place swap. Unwritable exe directory → no swap, just a
    /// clickable balloon to the release page. After the swap: RunAsAdmin sessions finish on the
    /// next start (no surprise UAC); otherwise relaunch immediately via the mutex-takeover path
    /// and exit.</summary>
    private async Task DownloadAndApplyUpdateAsync(ReleaseInfo release, bool interactive)
    {
        var exeDir = Path.GetDirectoryName(ExePath)!;
        if (!UpdateApplier.CanWriteTo(exeDir))
        {
            _settingsWindow?.SetUpdateStatus(
                $"{release.TagName} is available, but {exeDir} isn't writable — get it from the release page.");
            _tray?.ShowNotice($"AorinEQ {release.TagName} is available — click to open the release page.",
                () => OpenUrl(release.HtmlUrl));
            return;
        }

        var staging = Path.Combine(Path.GetTempPath(), "apo-update-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            var sha = await UpdateChecker.FetchSha256Async(release.Sha256Url!);
            if (sha is null)
                throw new InvalidOperationException("couldn't verify the release checksum.");
            await GatedDownload.DownloadAsync(release.ExeUrl!, staging, UpdateApplier.MaxExeBytes,
                GatedDownload.ExeMagic, sha);
            UpdateApplier.Apply(ExePath, staging);
        }
        catch (InvalidOperationException ex)
        {
            _settingsWindow?.SetUpdateStatus($"Update to {release.TagName} failed: {ex.Message}");
            if (interactive)
                _tray?.ShowWarning($"Update failed: {ex.Message}");
            return;
        }
        finally
        {
            try { File.Delete(staging); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // The swap is done — the exe path now holds the new build; this process keeps running
        // from its renamed .old image until it exits.
        if (_settings.RunAsAdmin)
        {
            // Auto-restarting would spring a surprise UAC prompt; the next launch runs the
            // new exe anyway.
            _settingsWindow?.SetUpdateStatus($"Updated to {release.TagName} — applies on the next start.");
            _tray?.ShowInfo($"Update to {release.TagName} will apply the next time AorinEQ starts.");
            return;
        }

        // Relaunch via the SAME proven handoff as the elevation bounce: start the successor
        // FIRST, then BeginShutdown (which disposes the show event before Shutdown drains, so the
        // successor's probe misses the dying event and takes over via the mutex teardown-wait).
        // Starting first means a failed Process.Start never disposes our IPC — we stay fully
        // functional on the old image, and the completed swap runs on any later start.
        System.Diagnostics.Process? proc;
        try
        {
            proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ExePath, "--updated")
            {
                UseShellExecute = true,
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            proc = null;
        }
        if (proc is null)
        {
            _tray?.ShowInfo($"Updated to {release.TagName} — restart AorinEQ to finish.");
            return;
        }
        BeginShutdown();
    }

    private static void OpenUrl(string url)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private bool SystemModeActive => _settings.VolumeMode == VolumeModes.System;

    /// <summary>One render path for every change: volume backend (APO preamp file or Windows
    /// endpoint volume, per mode), OSD, tray, settings.</summary>
    // Null-forgiving below relies on OnStartup's construction order: the active mode's backend
    // and _osd/_tray are all assigned before the keyboard hook, tray, or second-instance
    // listener can dispatch into Render; OnVolumeModeChanged builds the endpoint backend
    // BEFORE flipping the mode, so SystemModeActive implies _endpointVolume exists.
    private void Render(bool interactive)
    {
        if (SystemModeActive)
        {
            _endpointVolume!.SetPercent(ActiveState.Percent);
            _endpointVolume.SetMuted(ActiveState.Muted);
        }
        else
        {
            // No-op when eapo mode was selected while EAPO is still missing — the
            // onboarding-close hook builds the pipeline once EAPO exists.
            RenderEqConfig();
        }
        ShowOsd(interactive);
        _tray!.Update(ActiveState.Percent, ActiveState.Muted);
        SaveSettings();
    }

    /// <summary>Creates the Windows endpoint-volume backend (idempotent) and marshals its
    /// external-change events onto the dispatcher, same pattern as KeyboardHook.</summary>
    private void SetupEndpointVolume()
    {
        if (_endpointVolume is not null) return;
        _endpointVolume = new EndpointVolume();
        _endpointVolume.Changed += (id, p, m) => Dispatcher.BeginInvoke(() => OnEndpointVolumeChanged(id, p, m));
        _endpointVolume.DefaultDeviceChanged += () => Dispatcher.BeginInvoke(OnDefaultDeviceChanged);
    }

    /// <summary>The Windows default render device changed: swap the active per-device state
    /// (both modes), re-render the config so a first-seen device gets its block, and refresh
    /// the tray SILENTLY — no OSD, matching the native behavior on device switches. In system
    /// mode the endpoint backend's follow-up Changed event then adopts the new device's
    /// actual volume into the freshly-switched state.</summary>
    private void OnDefaultDeviceChanged()
    {
        _deviceStates.SwitchTo(AudioEndpoint.GetDefaultRenderEndpointId());
        RenderEqConfig();
        _tray?.Update(ActiveState.Percent, ActiveState.Muted);
        SaveSettings();
        _eqEditor?.OnActiveDeviceChanged(); // retab + loopback re-attach, if the editor is open
    }

    /// <summary>External endpoint change (another app, the Windows mixer, a device switch):
    /// sync state/tray/settings SILENTLY — no OSD, matching the native Windows HUD, which only
    /// appears for direct interaction. Never raised for our own sets (event-context filtered).
    /// <paramref name="deviceId"/> names the endpoint the notification came from: one still in
    /// flight from the PREVIOUS default device must not be written into the new device's state
    /// (it would persist the wrong volume for it).</summary>
    private void OnEndpointVolumeChanged(string? deviceId, int percent, bool muted)
    {
        if (!SystemModeActive) return; // stale event raced a switch back to eapo mode
        if (deviceId is not null && _deviceStates.ActiveId is not null && deviceId != _deviceStates.ActiveId)
            return; // notification from a device that is no longer the active one
        ActiveState.SetPercent(percent);
        ActiveState.SetMuted(muted);
        _tray?.Update(ActiveState.Percent, ActiveState.Muted);
        SaveSettings();
    }

    /// <summary>Adopts the device's current volume/mute into VolumeState so entering system mode
    /// never jumps the audible volume. When the device is unreadable the saved state stays and
    /// the first Render pushes it to the device instead.</summary>
    private void AdoptEndpointState()
    {
        if (_endpointVolume?.TryRead() is { } s)
        {
            ActiveState.SetPercent(s.Percent);
            ActiveState.SetMuted(s.Muted);
        }
    }

    /// <summary>Endpoint GUID for the active device's config block, falling back to EAPO's
    /// match-everything "all" pattern when no endpoint is known (no audio device) — the preamp
    /// then applies everywhere, which is exactly the legacy behavior.</summary>
    private string ActiveDeviceGuid() =>
        AudioEndpoint.EndpointGuid(_deviceStates.ActiveId) ?? "all";

    /// <summary>Assembles the full render model: the global EQ scope plus one section per
    /// known device (every device with volume state or an EQ assignment). Volume components
    /// come from the per-device states in eapo mode and are parked at 0 in system mode
    /// (loudness rides the Windows endpoint there; EQ renders in BOTH modes). The device-less
    /// fallback state renders under EAPO's "all" pattern so volume keys keep working even when
    /// no endpoint id is readable.</summary>
    private EqConfigModel BuildEqModel()
    {
        bool system = SystemModeActive;
        var global = _settings.GlobalEq;
        var deviceEq = _settings.DeviceEq;

        var volumes = new Dictionary<string, DeviceVolumeSetting>(_deviceStates.Snapshot());
        var ids = new List<string>(volumes.Keys);
        if (deviceEq is not null)
            foreach (var id in deviceEq.Keys)
                if (!volumes.ContainsKey(id))
                    ids.Add(id);

        var devices = new List<DeviceEqSection>();
        foreach (var id in ids)
        {
            if (AudioEndpoint.EndpointGuid(id) is not { } guid)
                continue;
            double volumeDb = 0;
            if (!system)
                volumeDb = volumes.TryGetValue(id, out var v)
                    ? VolumeState.ToDb(v.Percent, v.Muted)
                    : 0; // EQ-only assignment, never volumed: neutral until first seen
            var eq = deviceEq is not null && deviceEq.TryGetValue(id, out var scope) ? scope : null;
            devices.Add(new DeviceEqSection(guid, volumeDb,
                eq?.Enabled ?? true, eq?.PresetPreampDb ?? 0,
                eq?.Bands ?? Array.Empty<EqBand>()));
        }
        // Fallback "all" block ONLY when no device sections rendered at all: EAPO would apply
        // an "all" preamp IN ADDITION to a matching device block's preamp (they sum), so the
        // two must never coexist. With no known devices, "all" is the legacy behavior.
        if (devices.Count == 0 && !system)
            devices.Add(new DeviceEqSection("all", ActiveState.CurrentDb, true, 0, Array.Empty<EqBand>()));

        return new EqConfigModel(
            global?.Enabled ?? true, global?.PresetPreampDb ?? 0,
            global?.Bands ?? Array.Empty<EqBand>(), devices);
    }

    /// <summary>One call for every "the config file must reflect current state" moment:
    /// volume keys, EQ edits, device switches, mode transitions. No-op without a writer.</summary>
    private void RenderEqConfig() => _writer?.WriteConfig(BuildEqModel());

    /// <summary>Creates the ApoWriter against the EAPO config dir, probes writability (elevating
    /// via --setup when needed), and starts the include guard. Throws the startup-dialog
    /// exception family when EAPO is missing or unusable. The probe reuses the writer (its
    /// EnsureInclude doubles as the config.txt write check), so exactly one ApoWriter and one
    /// include pass per session; on the elevated-setup path the child added the include line
    /// itself, so no retry is needed here.</summary>
    private void BuildEapoPipeline()
    {
        var configDir = ApoPaths.GetConfigDir();
        var writer = new ApoWriter(configDir);
        try
        {
            EnsureWritableOrElevate(writer);
        }
        catch
        {
            writer.Dispose();
            throw;
        }
        writer.WriteFailing += () => Dispatcher.BeginInvoke(() =>
            _tray?.ShowWarning("Volume changes are not reaching Equalizer APO (aorineq.txt is not writable)."));
        writer.StartIncludeGuard();
        _writer = writer;
    }

    /// <summary>Non-throwing <see cref="BuildEapoPipeline"/> for live mode switches.</summary>
    private bool TryBuildEapoPipeline()
    {
        try
        {
            BuildEapoPipeline();
            return true;
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or InvalidOperationException or IOException
            or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>System-mode variant: builds the writer for EQ rendering ONLY when the config
    /// dir is already writable — no UAC elevation, no dialogs. System mode works fine without
    /// it (loudness is the endpoint's job); a missing pipeline just means EQ doesn't render
    /// until eapo mode's guided setup grants access.</summary>
    private bool TryBuildEapoPipelineQuiet()
    {
        try
        {
            var configDir = ApoPaths.GetConfigDir();
            var writer = new ApoWriter(configDir);
            try
            {
                File.AppendAllText(writer.VolumeFilePath, ""); // create-or-touch; throws if unwritable
                writer.EnsureInclude();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                writer.Dispose();
                return false;
            }
            writer.WriteFailing += () => Dispatcher.BeginInvoke(() =>
                _tray?.ShowWarning("EQ changes are not reaching Equalizer APO (aorineq.txt is not writable)."));
            writer.StartIncludeGuard();
            _writer = writer;
            return true;
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException
            or UnauthorizedAccessException)
        {
            return false; // EAPO missing entirely: nothing to render into
        }
    }

    /// <summary>Reverse mute handover (system → eapo): a muted endpoint would keep eapo mode
    /// silent no matter what the preamp says, so it unmutes here — but only once the mute
    /// preamp is PROVEN on disk (Flush is just a run barrier, not a success proof). The proof
    /// compares the active device's rendered Preamp line (volume + preset preamps summed)
    /// against a fresh render of the same model. On a missing writer or a failed write the
    /// endpoint stays muted: silence, never an audible leak. Runs on the immediate switch AND
    /// on the deferred completion after the setup guide installs EAPO
    /// (see <see cref="OnOnboardingClosed"/>).</summary>
    private void HandMuteBackToPreamp()
    {
        if (ActiveState.Muted && _writer is not null && _endpointVolume?.TryRead() is { Muted: true })
        {
            _writer.Flush();
            var expected = ApoWriter.ReadDevicePreamp(
                ApoWriter.RenderConfig(BuildEqModel()), ActiveDeviceGuid());
            if (expected is { } db && PreampFileReads(db))
                _endpointVolume.SetMuted(false);
        }
    }

    /// <summary>Whether aorineq.txt's block for the active device currently reads exactly
    /// the preamp for <paramref name="db"/> — the proof gate for handing mute duty back from
    /// the endpoint.</summary>
    private bool PreampFileReads(double db)
    {
        try
        {
            var content = File.ReadAllText(_writer!.VolumeFilePath);
            return ApoWriter.ReadDevicePreamp(content, ActiveDeviceGuid()) is { } read
                && ApoWriter.FormatPreamp(read) == ApoWriter.FormatPreamp(db);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Applies a volume-mode switch live (Settings radio or the setup guide's mode
    /// page) with the transition semantics: eapo→system parks the preamp at 0 dB and ADOPTS the
    /// device state (no jump); system→eapo re-applies the saved percent to the preamp, building
    /// the writer pipeline on the fly when this session started without one.</summary>
    private void OnVolumeModeChanged(string mode)
    {
        if (mode == _settings.VolumeMode) return;
        if (mode == VolumeModes.System)
        {
            SetupEndpointVolume(); // before the mode flips: Render's invariant (see above)
            _settings = _settings with { VolumeMode = mode };
            // Mute duty hands over FIRST: parking the preamp at 0 dB would audibly unmute an
            // eapo-muted session (preamp -120 was the only thing keeping it silent), so the
            // endpoint takes the mute before the chain goes transparent.
            if (ActiveState.Muted) _endpointVolume!.SetMuted(true);
            // Park the volume components at 0 dB (mode already flipped, so BuildEqModel
            // renders them parked) — through the writer's coalescer so it serializes AFTER any
            // in-flight volume write (latest-wins), then Flush as a barrier: the park must be
            // on disk before the coalesced settings save can record "system" (a crash between
            // the two is additionally repaired by the idempotent repark render at every
            // system-mode startup). The EQ chain keeps rendering — EQ applies in both modes.
            if (_writer is null)
                TryBuildEapoPipelineQuiet(); // EQ-only pipeline; never a UAC prompt from here
            if (_writer is not null)
            {
                RenderEqConfig();
                _writer.Flush();
            }
            AdoptEndpointState();
            _tray?.Update(ActiveState.Percent, ActiveState.Muted);
        }
        else
        {
            _settings = _settings with { VolumeMode = mode };
            if (_writer is null && !TryBuildEapoPipeline())
            {
                _tray?.ShowWarning("Equalizer APO isn't set up yet — volume keys won't change "
                    + "loudness until the setup guide completes.");
                OpenOnboarding();
            }
            RenderEqConfig(); // re-apply the saved per-device volumes to the preamps
            HandMuteBackToPreamp();
        }
        SaveSettings();
    }

    /// <summary>Shows the currently active OSD window (skin-driven or the standard OsdWindow) for
    /// the current volume state. Shared by Render (volume/mute changes) and OnOsdSettingsChanged
    /// (so an OSD-only settings change is immediately visible without needing a volume keypress).</summary>
    private void ShowOsd(bool interactive)
    {
        if (_useSkinOsd && _skinOsd is not null)
            _skinOsd.ShowVolume(ActiveState.Percent, ActiveState.Muted, interactive);
        else
            _osd!.ShowVolume(ActiveState.Percent, ActiveState.Muted, interactive);
    }

    /// <summary>Shared handler for both OsdWindow's and SkinOsdWindow's PercentChangedByUser —
    /// same contract either way: only ever raised by direct user interaction with the OSD, never
    /// by ShowVolume's own programmatic updates.</summary>
    private void OnOsdPercentChanged(int percent)
    {
        ActiveState.SetPercent(percent);
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
        _deviceStates.StepPercent = _settings.StepPercent;
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
            if (info.MutedPath is not null)
                stamp = Math.Max(stamp, File.GetLastWriteTimeUtc(info.MutedPath).Ticks);
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
        _settings = _settings with
        {
            // Legacy mirror of the ACTIVE device (pre-v2 compatibility + first-seen seed)…
            Percent = ActiveState.Percent, Muted = ActiveState.Muted, StepPercent = ActiveState.StepPercent,
            // …plus the full per-device map.
            DeviceVolumes = new Dictionary<string, DeviceVolumeSetting>(_deviceStates.Snapshot()),
        };
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

    /// <summary>Normal runs must be able to write aorineq.txt and config.txt. If not, self-elevate once.</summary>
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
        _endpointVolume?.Dispose();
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
