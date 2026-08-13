using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AorinEQ.UI;

/// <summary>The extended window styles the OSD and every HUD widget share, in one place.
///
/// WS_EX_TOOLWINDOW keeps these windows out of alt-tab and off the taskbar; WS_EX_NOACTIVATE stops
/// them stealing focus from whatever the user is actually doing. WS_EX_TRANSPARENT is the one that
/// carries the HUD's live mode: with it set the window is invisible to hit testing entirely, so a
/// click over a widget reaches the desktop underneath instead of being eaten.
///
/// The click-through flag is set on the WINDOW HANDLE rather than by any WPF property because
/// there is no WPF equivalent — IsHitTestVisible only stops WPF's own routing, and the window
/// would still swallow the click before WPF ever saw it. That is precisely the defect a screenshot
/// cannot show, and why the release verifies live mode by hit-testing the DESKTOP THROUGH a
/// widget rather than by looking at one.</summary>
internal static class HudWindowStyle
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>Applies the shared style. Must be called after the handle exists (SourceInitialized
    /// or later); before that there is nothing to style.</summary>
    public static void MakeToolWindow(Window window, bool clickThrough)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int style = GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
        style = clickThrough ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT;
        SetWindowLong(hwnd, GWL_EXSTYLE, style);
    }

    /// <summary>Turns click-through on or off on an already-styled window — the edit/live toggle.
    /// A no-op before the handle exists, which is correct: the window's SourceInitialized applies
    /// the current mode when it does.</summary>
    public static void SetClickThrough(Window window, bool clickThrough)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int style = GetWindowLong(hwnd, GWL_EXSTYLE);
        int next = clickThrough ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT;
        if (next != style) SetWindowLong(hwnd, GWL_EXSTYLE, next);
    }

    /// <summary>Whether the window is currently click-through. Read back from the handle rather
    /// than from a field, so a verification harness is asking WINDOWS what the style is and not
    /// asking the app to repeat what it believes.</summary>
    public static bool IsClickThrough(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        return hwnd != IntPtr.Zero && (GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TRANSPARENT) != 0;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
