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

    private static string ExePath => Environment.ProcessPath!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--setup"))
        {
            RunElevatedSetup();
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
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or InvalidOperationException)
        {
            System.Windows.MessageBox.Show(ex.Message, "apo-volume", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "apo-volume", "settings.json");
        var settings = Settings.Load(_settingsPath);
        _state = new VolumeState(settings.Percent, settings.Muted);

        _writer = new ApoWriter(configDir);
        _writer.EnsureInclude();
        _writer.StartIncludeGuard();

        _osd = new OsdWindow();
        _osd.PercentChangedByUser += p => { _state.SetPercent(p); Render(interactive: true); };

        _tray = new TrayIcon(new Autostart(), ExePath);
        _tray.OpenRequested += () => Render(interactive: true);
        _tray.MuteToggleRequested += () => { _state.ToggleMute(); Render(interactive: false); };
        _tray.ExitRequested += () => Shutdown();

        _hook = new KeyboardHook();
        _hook.VolumeUp += () => { _state.Up(); Render(interactive: false); };
        _hook.VolumeDown += () => { _state.Down(); Render(interactive: false); };
        _hook.MuteToggle += () => { _state.ToggleMute(); Render(interactive: false); };

        // second-instance listener
        var waiter = new Thread(() =>
        {
            while (_showEvent.WaitOne())
                Dispatcher.BeginInvoke(() => Render(interactive: true));
        }) { IsBackground = true };
        waiter.Start();

        // apply persisted volume to APO immediately at startup
        _writer.WriteVolume(_state.CurrentDb);
        _tray.Update(_state.Percent, _state.Muted);
    }

    /// <summary>One render path for every change: APO file, OSD, tray, settings.</summary>
    private void Render(bool interactive)
    {
        _writer!.WriteVolume(_state.CurrentDb);
        _osd!.ShowVolume(_state.Percent, _state.Muted, interactive);
        _tray!.Update(_state.Percent, _state.Muted);
        new Settings(_state.Percent, _state.Muted).Save(_settingsPath);
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
        base.OnExit(e);
    }
}
