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
        _hook.VolumeUp += () => Console.Beep(880, 40);
        _hook.VolumeDown += () => Console.Beep(440, 40);
        _hook.MuteToggle += () => Console.Beep(220, 80);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hook?.Dispose();
        base.OnExit(e);
    }
}
