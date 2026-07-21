using System.IO;
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
    private EventWaitHandle? _showEvent;
    private KeyboardHook? _hook;
    private ApoWriter? _writer;
    private TrayIcon? _tray;
    private OsdWindow? _osd;
    private SettingsWindow? _settingsWindow;
    private VolumeState _state = new();
    private string _settingsPath = "";
    private bool _runAsAdmin;
    private bool _uacDeclined;
    private bool _togglingRunAsAdmin;
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
        _runAsAdmin = settings.RunAsAdmin;

        // Cheap probe so a second launch doesn't pay for a pointless UAC prompt via the
        // elevated bounce below: if an instance is already running, its named event exists
        // and we can just signal it and exit. The mutex below remains the authoritative
        // single-instance check — this is purely an optimization for the common case.
        if (EventWaitHandle.TryOpenExisting(ShowEventName, out var existing))
        {
            existing.Set();
            existing.Dispose();
            Shutdown();
            return;
        }

        // Must run before the mutex is created: the elevated child claims the mutex itself,
        // so the exiting (non-elevated) parent must not hold it.
        if (BounceToElevatedOrContinue(e.Args))
            return;

        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!isFirstInstance)
        {
            _showEvent.Set(); // ask the running instance to show its slider
            Shutdown();
            return;
        }

        string configDir;
        try
        {
            configDir = ApoPaths.GetConfigDir();
            EnsureWritableOrElevate(configDir);

            _state = new VolumeState(settings.Percent, settings.Muted);

            _writer = new ApoWriter(configDir);
            _writer.WriteFailing += () => Dispatcher.BeginInvoke(() =>
                _tray?.ShowWarning("Volume changes are not reaching Equalizer APO (apo-volume.txt is not writable)."));
            _writer.EnsureInclude();
            _writer.StartIncludeGuard();

            _osd = new OsdWindow();
            _osd.PercentChangedByUser += p => { _state.SetPercent(p); Render(interactive: true); };

            _tray = new TrayIcon();
            _tray.OpenRequested += () => Render(interactive: true);
            _tray.MuteToggleRequested += () => { _state.ToggleMute(); Render(interactive: false); };
            _tray.SettingsRequested += OpenSettings;
            _tray.ExitRequested += () => Shutdown();

            if (_uacDeclined)
                _tray.ShowWarning(
                    "Running without administrator rights — volume keys won't work while elevated games are focused.");

            // Elevated-startup reconciliation: only act when Run-key autostart is on, the scheduled
            // task isn't, and we actually have the rights to create it now — otherwise this would
            // create needless schtasks churn on every elevated launch.
            if (Elevation.IsElevated && _runAsAdmin
                && new Autostart().IsEnabled() && !new ScheduledTaskAutostart().IsEnabled())
            {
                ApplyAutostart(true);
            }
            // Reverse case: RunAsAdmin is off (or was turned off) but a scheduled task from an
            // earlier elevated/RunAsAdmin session is still registered. We're elevated right now,
            // so we have the rights to remove it — register the Run key instead, under the
            // current (non-admin) mode.
            else if (Elevation.IsElevated && !_runAsAdmin && new ScheduledTaskAutostart().IsEnabled())
            {
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
                    Dispatcher.BeginInvoke(() => Render(interactive: true));
                }
            }
            catch (Exception) { } // shutdown races (disposed handle / stopped dispatcher) must not crash the process
        }) { IsBackground = true };
        waiter.Start();

        // apply persisted volume to APO immediately at startup
        _writer.WriteVolume(_state.CurrentDb);
        _tray.Update(_state.Percent, _state.Muted);
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
        if (!_runAsAdmin || Elevation.IsElevated || args.Contains("--no-elevate"))
            return false;

        if (TryRelaunchElevatedAndShutdown())
            return true;

        _uacDeclined = true;
        return false;
    }

    /// <summary>Relaunches this executable elevated via UAC and shuts this instance down.
    /// Returns false, having taken no action, if UAC was declined.</summary>
    private bool TryRelaunchElevatedAndShutdown()
    {
        System.Diagnostics.Process? proc;
        try
        {
            proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ExePath)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // UAC declined
        }

        if (proc is null)
            return false; // couldn't start — treat like a decline, stay running non-elevated

        Shutdown();
        return true;
    }

    /// <summary>Raw, blocking mechanism call selected by <see cref="_runAsAdmin"/> — schtasks calls
    /// can take ~100ms, so callers on the dispatcher thread must run this via Task.Run.</summary>
    private void ApplyAutostartMechanism(bool enable)
    {
        if (enable)
        {
            if (_runAsAdmin) new ScheduledTaskAutostart().Enable(ExePath);
            else new Autostart().Enable(ExePath);
        }
        else
        {
            if (_runAsAdmin) new ScheduledTaskAutostart().Disable();
            else new Autostart().Disable();
        }
    }

    /// <summary>Raw, blocking disable of the mechanism NOT selected by <see cref="_runAsAdmin"/>.</summary>
    private void DisableOtherMechanism()
    {
        if (_runAsAdmin) new Autostart().Disable();
        else new ScheduledTaskAutostart().Disable();
    }

    /// <summary>
    /// Enables/disables autostart via whichever mechanism <see cref="_runAsAdmin"/> selects
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
            _runAsAdmin = on;

            // Synchronous, not coalesced: TryRelaunchElevatedAndShutdown below can call Shutdown(),
            // which disposes _settingsSaver before its debounce timer fires — the coalesced write
            // would be lost and the elevated child would start from stale settings. This write must
            // land before we ever offer or perform that relaunch.
            try
            {
                new Settings(_state.Percent, _state.Muted, _runAsAdmin).Save(_settingsPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

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

            _settingsWindow?.SyncState(await IsAutostartEnabledAsync(), _runAsAdmin, Elevation.IsElevated);
        }
        finally
        {
            _togglingRunAsAdmin = false;
        }
    }

    /// <summary>Lazily creates (or re-syncs and shows) the single SettingsWindow instance.
    /// Queries autostart state off the dispatcher thread since ScheduledTaskAutostart.IsEnabled()
    /// shells out to schtasks (~100ms); the await resumes back here on the UI thread.</summary>
    private async void OpenSettings()
    {
        bool autostartEnabled = await IsAutostartEnabledAsync();

        if (_settingsWindow is null)
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "dev";
            _settingsWindow = new SettingsWindow(autostartEnabled, _runAsAdmin, Elevation.IsElevated, version);
            _settingsWindow.AutostartChanged += async on => await ApplyAutostartAsync(on);
            _settingsWindow.RunAsAdminChanged += OnRunAsAdminToggled;
        }
        else
        {
            _settingsWindow.SyncState(autostartEnabled, _runAsAdmin, Elevation.IsElevated);
        }

        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    /// <summary>One render path for every change: APO file, OSD, tray, settings.</summary>
    // Null-forgiving below relies on OnStartup's construction order: _writer/_osd/_tray are all
    // assigned before the keyboard hook, tray, or second-instance listener can dispatch into Render.
    private void Render(bool interactive)
    {
        _writer!.WriteVolume(_state.CurrentDb);
        _osd!.ShowVolume(_state.Percent, _state.Muted, interactive);
        _tray!.Update(_state.Percent, _state.Muted);
        SaveSettings();
    }

    private void SaveSettings()
    {
        var s = new Settings(_state.Percent, _state.Muted, _runAsAdmin);
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
    private static void EnsureWritableOrElevate(string configDir)
    {
        try
        {
            var probe = Path.Combine(configDir, ApoWriter.VolumeFileName);
            File.AppendAllText(probe, ""); // create-or-touch; throws if unwritable
            using var w = new ApoWriter(configDir);
            w.EnsureInclude();
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
                    "Elevated setup failed. Grant write access to the Equalizer APO config folder and retry.");
        }
    }

    /// <summary>Runs elevated (--setup): create volume file, grant Users modify on it, add include line.</summary>
    private static void RunElevatedSetup()
    {
        var configDir = ApoPaths.GetConfigDir();
        var volumePath = Path.Combine(configDir, ApoWriter.VolumeFileName);
        if (!File.Exists(volumePath))
            File.WriteAllText(volumePath, ApoWriter.FormatPreamp(0) + Environment.NewLine);

        var fi = new FileInfo(volumePath);
        var acl = fi.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Modify, AccessControlType.Allow));
        fi.SetAccessControl(acl);

        using var w = new ApoWriter(configDir);
        w.EnsureInclude();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hook?.Dispose();
        _tray?.Dispose();
        _writer?.Dispose();
        _mutex?.Dispose();
        _showEvent?.Dispose();
        _settingsSaver.Dispose();
        base.OnExit(e);
    }
}
