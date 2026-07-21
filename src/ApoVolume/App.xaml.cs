using System.Windows;
using ApoVolume.Input;

namespace ApoVolume;

public partial class App : System.Windows.Application
{
    private KeyboardHook? _hook;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _hook = new KeyboardHook();
        var osd = new UI.OsdWindow();
        int percent = 50;
        _hook.VolumeUp += () => { percent = Math.Min(100, percent + 2); osd.ShowVolume(percent, false, false); };
        _hook.VolumeDown += () => { percent = Math.Max(0, percent - 2); osd.ShowVolume(percent, false, false); };
        _hook.MuteToggle += () => osd.ShowVolume(percent, true, false);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hook?.Dispose();
        base.OnExit(e);
    }
}
