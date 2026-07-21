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
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ExePath)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // UAC declined
        }

        Shutdown();
        return true;
    }

    /// <summary>
    /// Enables/disables autostart via whichever mechanism <see cref="_runAsAdmin"/> selects
    /// (scheduled task when elevated-on-logon is wanted, Run key otherwise). Enabling also
    /// disables the other mechanism so only one autostart registration ever exists — this is how
    /// migration between mechanisms happens (e.g. from <see cref="OnRunAsAdminToggled"/>). Every
    /// operation is best-effort: schtasks operations can fail without elevation, and failures are
    /// reported via a tray balloon rather than thrown.
    /// </summary>
    private void ApplyAutostart(bool enable)
    {
        try
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
        catch (InvalidOperationException ex)
        {
            _tray?.ShowWarning(ex.Message);
        }

        if (!enable) return;

        try
        {
            if (_runAsAdmin) new Autostart().Disable();
            else new ScheduledTaskAutostart().Disable();
        }
        catch (InvalidOperationException ex)
        {
            _tray?.ShowWarning(ex.Message);
        }
    }

    private bool IsAutostartEnabled() => new Autostart().IsEnabled() || new ScheduledTaskAutostart().IsEnabled();

    /// <summary>Handles the SettingsWindow RunAsAdminChanged event: persists the choice, offers an
    /// immediate elevated relaunch when turning on, migrates the autostart mechanism to match, and
    /// suggests a restart when turning off while currently elevated.</summary>
    private void OnRunAsAdminToggled(bool on)
    {
        _runAsAdmin = on;
        SaveSettings();
        bool autostartEnabled = IsAutostartEnabled();

        if (on)
        {
            if (!Elevation.IsElevated)
            {
                var choice = System.Windows.MessageBox.Show(
                    "Restart apo-volume elevated now?", "apo-volume", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (choice == MessageBoxResult.Yes && TryRelaunchElevatedAndShutdown())
                    return;
            }

            // Migrate Run-key -> scheduled task. If we're still not elevated this is best-effort;
            // ApplyAutostart balloons the failure and it will self-correct on the next elevated start.
            if (autostartEnabled) ApplyAutostart(true);
        }
        else
        {
            // Migrate scheduled task -> Run key. Deleting the task may need elevation; best-effort.
            if (autostartEnabled) ApplyAutostart(true);

            if (Elevation.IsElevated)
                System.Windows.MessageBox.Show(
                    "Restart apo-volume without elevation for this change to take full effect.",
                    "apo-volume", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        _settingsWindow?.SyncState(IsAutostartEnabled(), _runAsAdmin, Elevation.IsElevated);
    }

    /// <summary>Lazily creates (or re-syncs and shows) the single SettingsWindow instance.</summary>
    private void OpenSettings()
    {
        if (_settingsWindow is null)
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "dev";
            _settingsWindow = new SettingsWindow(IsAutostartEnabled(), _runAsAdmin, Elevation.IsElevated, version);
            _settingsWindow.AutostartChanged += on => ApplyAutostart(on);
            _settingsWindow.RunAsAdminChanged += OnRunAsAdminToggled;
        }
        else
        {
            _settingsWindow.SyncState(IsAutostartEnabled(), _runAsAdmin, Elevation.IsElevated);
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
