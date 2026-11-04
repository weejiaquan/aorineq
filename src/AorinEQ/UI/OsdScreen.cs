using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace AorinEQ.UI;

/// <summary>Which screen the OSD appears on, and how to put a window there.
///
/// THE ACTIVE SCREEN is the one holding the foreground window - the game, browser or document the
/// user is working in - because that is where they are looking when they press a volume key. With
/// nothing meaningful in front (no foreground window, or the desktop itself) it is the screen under
/// the pointer. The primary screen, which is all the OSD knew before, is only ever the answer when
/// Windows cannot name a monitor at all.
///
/// COORDINATES. The process is PerMonitorV2: a screen's own DIPs are its physical pixels divided by
/// ITS scale, but WPF interprets a window's Left/Top with the scale of the screen the window is on
/// NOW. So positions are computed in the target screen's DIPs (where the window's DIP size is the
/// size it will draw at) and converted to the window's current scale only when assigned. Moving to
/// a screen of a different scale then changes the window's DPI; its owner re-applies the same
/// position on DpiChanged, which is exact because the current scale is then the target's.</summary>
internal static class OsdScreen
{
    /// <summary>A screen's work area in its own DIPs, and its scale (1.0 at 96 DPI).</summary>
    public readonly record struct Target(Rect WorkArea, double Scale);

    /// <summary>The screen the OSD belongs on right now.</summary>
    public static Target Active()
    {
        var monitor = ActiveMonitor();
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            return new Target(SystemParameters.WorkArea, 1.0);

        double scale = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpi, out _) == 0 && dpi > 0
            ? dpi / 96.0
            : 1.0;
        return new Target(ToDips(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom, scale), scale);
    }

    /// <summary>Puts <paramref name="window"/>'s top-left at (<paramref name="left"/>,
    /// <paramref name="top"/>) in <paramref name="target"/>'s DIPs.</summary>
    public static void MoveTo(Window window, Target target, double left, double top)
    {
        double current = VisualTreeHelper.GetDpi(window).DpiScaleX;
        if (current <= 0) current = 1.0;
        window.Left = WindowCoordinate(left, target.Scale, current);
        window.Top = WindowCoordinate(top, target.Scale, current);
    }

    /// <summary>Whether the foreground window says nothing about where the user is looking: there
    /// is none, or it is the desktop (Progman, or the WorkerW behind the icons).</summary>
    internal static bool FollowsCursor(string? foregroundClass) =>
        foregroundClass is null or "Progman" or "WorkerW";

    /// <summary>A physical-pixel rectangle in DIPs at <paramref name="scale"/>.</summary>
    internal static Rect ToDips(int left, int top, int right, int bottom, double scale) =>
        new(left / scale, top / scale, (right - left) / scale, (bottom - top) / scale);

    /// <summary>A coordinate in the target screen's DIPs, as a window currently at
    /// <paramref name="currentScale"/> must be told it: back to physical pixels, then into the
    /// window's present DIPs.</summary>
    internal static double WindowCoordinate(double targetDips, double targetScale, double currentScale) =>
        targetDips * targetScale / currentScale;

    private static IntPtr ActiveMonitor()
    {
        var foreground = GetForegroundWindow();
        if (!FollowsCursor(foreground == IntPtr.Zero ? null : ClassName(foreground)))
            return MonitorFromWindow(foreground, MONITOR_DEFAULTTONEAREST);
        return GetCursorPos(out var point) ? MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST) : IntPtr.Zero;
    }

    private static string ClassName(IntPtr hwnd)
    {
        var name = new StringBuilder(256);
        return GetClassName(hwnd, name, name.Capacity) > 0 ? name.ToString() : "";
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT point, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}
