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
    private VolumeState _state = new();
    private string _settingsPath = "";
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

            _settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "apo-volume", "settings.json");
            var settings = Settings.Load(_settingsPath);
            _state = new VolumeState(settings.Percent, settings.Muted);

            _writer = new ApoWriter(configDir);
            _writer.WriteFailing += () => Dispatcher.BeginInvoke(() =>
                _tray?.ShowWarning("Volume changes are not reaching Equalizer APO (apo-volume.txt is not writable)."));
            _writer.EnsureInclude();
            _writer.StartIncludeGuard();

            _osd = new OsdWindow();
            _osd.PercentChangedByUser += p => { _state.SetPercent(p); Render(interactive: true); };

            _tray = new TrayIcon(new Autostart(), ExePath);
            _tray.OpenRequested += () => Render(interactive: true);
            _tray.MuteToggleRequested += () => { _state.ToggleMute(); Render(interactive: false); };
            _tray.ExitRequested += () => Shutdown();

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

    /// <summary>One render path for every change: APO file, OSD, tray, settings.</summary>
    // Null-forgiving below relies on OnStartup's construction order: _writer/_osd/_tray are all
    // assigned before the keyboard hook, tray, or second-instance listener can dispatch into Render.
    private void Render(bool interactive)
    {
        _writer!.WriteVolume(_state.CurrentDb);
        _osd!.ShowVolume(_state.Percent, _state.Muted, interactive);
        _tray!.Update(_state.Percent, _state.Muted);
        var s = new Settings(_state.Percent, _state.Muted);
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
