using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using AorinEQ.Core;
using AorinEQ.UI;
using Application = System.Windows.Application;

namespace AorinEQ.Tests;

/// <summary>The OSD appears on the screen holding the foreground window, falling back to the
/// pointer's screen when the desktop is in front - instead of always on the primary.</summary>
[Collection("Wpf")]
public class OsdScreenTests
{
    private readonly StaWpf _sta;
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public OsdScreenTests(StaWpf sta, Xunit.Abstractions.ITestOutputHelper output)
    {
        _sta = sta;
        _out = output;
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("Progman", true)]
    [InlineData("WorkerW", true)]
    [InlineData("Shell_TrayWnd", false)]   // the taskbar is ON a screen: a tray click shows there
    [InlineData("Chrome_WidgetWin_1", false)]
    [InlineData("", false)]                // a window whose class could not be read still has a screen
    public void Only_no_window_or_the_desktop_defers_to_the_pointer(string? foregroundClass, bool followsCursor)
    {
        _out.WriteLine($"foreground class '{foregroundClass ?? "(none)"}' -> follows cursor: {OsdScreen.FollowsCursor(foregroundClass)}");
        Assert.Equal(followsCursor, OsdScreen.FollowsCursor(foregroundClass));
    }

    [Fact]
    public void A_work_area_converts_to_the_screens_own_dips()
    {
        // A 150 % screen to the left of the primary: 2880x1620 physical, taskbar 72 px.
        var r = OsdScreen.ToDips(-2880, 0, 0, 1548, 1.5);
        _out.WriteLine($"work area in DIPs: {r}");

        Assert.Equal(new Rect(-1920, 0, 1920, 1032), r);
    }

    /// <summary>The window is still on a 100 % screen when it is told where to go on a 150 % one:
    /// WPF reads Left at 100 %, so the 150 % DIP has to be handed over as physical pixels.</summary>
    [Theory]
    [InlineData(-1000, 1.5, 1.0, -1500)]
    [InlineData(-1000, 1.5, 1.5, -1000)]  // already on the target screen: unchanged
    [InlineData(800, 1.0, 1.25, 640)]
    public void A_target_coordinate_is_given_in_the_windows_current_scale(double target, double targetScale, double currentScale, double expected)
    {
        double actual = OsdScreen.WindowCoordinate(target, targetScale, currentScale);
        _out.WriteLine($"{target} @ {targetScale} as seen @ {currentScale} = {actual}");

        Assert.Equal(expected, actual, 6);
    }

    /// <summary>The real OSD, the real foreground window, every real screen: a window is put on
    /// each screen in turn and made foreground, and the OSD must appear inside THAT screen's work
    /// area. On a one-screen machine this still proves the OSD lands where the foreground is.</summary>
    [Fact]
    [Trait(Requires.Key, Requires.Display)]
    public void The_osd_appears_on_the_screen_of_the_foreground_window()
    {
        var monitors = DisplayMonitors.Enumerate();
        Assert.NotEmpty(monitors);

        var results = _sta.Run(() =>
        {
            Application.Current.Resources["AppFontFamily"] = new System.Windows.Media.FontFamily("Segoe UI");
            var osd = new OsdWindow();
            var list = new List<(string Monitor, bool Foreground, GetRect Osd, HudRect Work)>();
            try
            {
                foreach (var monitor in monitors)
                {
                    var host = new Window
                    {
                        // An explicit empty style: the fixture merges WPF-UI's implicit Window style,
                        // which sets AllowsTransparency and throws once the window is shown.
                        Style = new Style(typeof(Window)), ShowInTaskbar = false, Width = 200, Height = 120,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                    };
                    try
                    {
                        host.Show();
                        var hwnd = new WindowInteropHelper(host).Handle;
                        SetWindowPos(hwnd, IntPtr.Zero, (int)monitor.WorkArea.X + 50, (int)monitor.WorkArea.Y + 50, 300, 200, 0x0004);
                        ForceForeground(hwnd);
                        bool isForeground = GetForegroundWindow() == hwnd;

                        osd.ShowVolume(42, muted: false, interactive: false);
                        PumpUntilRendered();
                        GetWindowRect(new WindowInteropHelper(osd).Handle, out var rect);
                        list.Add((monitor.Name, isForeground, rect, monitor.WorkArea));
                        osd.Hide();
                    }
                    finally { host.Close(); }
                }
            }
            finally { osd.Close(); }
            return list;
        });

        foreach (var r in results)
            _out.WriteLine($"{r.Monitor}: host foreground={r.Foreground} osd=({r.Osd.Left},{r.Osd.Top})-({r.Osd.Right},{r.Osd.Bottom}) "
                         + $"work=({r.Work.X},{r.Work.Y}) {r.Work.Width}x{r.Work.Height}");

        Assert.All(results, r =>
        {
            Assert.True(r.Foreground, $"could not make the test window foreground on {r.Monitor}; the test proves nothing then");
            Assert.True(r.Osd.Left >= r.Work.X && r.Osd.Right <= r.Work.X + r.Work.Width
                     && r.Osd.Top >= r.Work.Y && r.Osd.Bottom <= r.Work.Y + r.Work.Height,
                $"OSD not inside {r.Monitor}'s work area");
        });
    }

    private static void PumpUntilRendered() =>
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { },
            System.Windows.Threading.DispatcherPriority.ContextIdle);

    /// <summary>SetForegroundWindow is refused to a process that is not already in front. The
    /// standard way round it for a test: attach to the foreground thread's input for the call.</summary>
    private static void ForceForeground(IntPtr hwnd)
    {
        uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        uint thisThread = GetCurrentThreadId();
        AttachThreadInput(thisThread, foregroundThread, true);
        try { SetForegroundWindow(hwnd); }
        finally { AttachThreadInput(thisThread, foregroundThread, false); }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GetRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out GetRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
}
