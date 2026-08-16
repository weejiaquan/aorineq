using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AorinEQ.Input;

/// <summary>One wheel notch, with the modifiers that were held when it arrived.</summary>
public readonly record struct WheelNotch(int RawDelta, bool Ctrl, bool Shift);

/// <summary>
/// System-wide WH_MOUSE_LL hook, installed only to give the tray icon a scroll wheel.
///
/// It exists because the shell never offers one. The notification area forwards clicks and moves
/// to an icon's owner (WM_LBUTTONDOWN, WM_MOUSEMOVE and friends), but there is no notification
/// message for the wheel — Explorer keeps wheel input for its own system icons, of which the
/// Windows volume icon is one, and that is why scrolling THAT icon works without anybody
/// installing a hook. For a Shell_NotifyIcon from another process there is no supported path, so
/// this is the path every app that does this takes.
///
/// The cost is that the app now sits in front of every mouse message on the machine, so the
/// callback bails to CallNextHookEx before doing any work at all for anything that is not a
/// vertical wheel — and <c>App</c> only installs the hook while the feature is switched on.
///
/// <paramref name="isOverTarget"/> is asked, synchronously, whether the cursor is over the thing
/// we own; only then is the notch delivered and the message swallowed. It has to be synchronous
/// because the return value IS the swallow, and it is safe to be: a low-level hook's callback
/// runs on the thread that installed it, which here is the UI thread. Delivery itself still goes
/// through the dispatcher, the same as <see cref="KeyboardHook"/>, so a slow handler can never
/// hold the hook past the system's timeout.
/// </summary>
public sealed class MouseWheelHook : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;

    // MSLLHOOKSTRUCT: POINT pt (2 x LONG), then DWORD mouseData. The wheel delta is the signed
    // high word of mouseData.
    private const int OffsetX = 0;
    private const int OffsetY = 4;
    private const int OffsetMouseData = 8;

    /// <summary>A whole notch over the target, raised on the dispatcher.</summary>
    public event Action<WheelNotch>? Scrolled;

    private readonly Func<int, int, bool> _isOverTarget;
    private readonly LowLevelMouseProc _proc; // field: keeps delegate alive for the native hook
    private readonly IntPtr _hook;
    private bool _disposed;

    public MouseWheelHook(Func<int, int, bool> isOverTarget)
    {
        _isOverTarget = isOverTarget;
        _proc = Callback;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to install low-level mouse hook.");
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Every mouse move on the machine comes through here. Nothing above this line may cost
        // anything, and the common case must fall straight through.
        if (nCode >= 0 && (long)wParam == WM_MOUSEWHEEL)
        {
            int x = Marshal.ReadInt32(lParam, OffsetX);
            int y = Marshal.ReadInt32(lParam, OffsetY);
            if (_isOverTarget(x, y))
            {
                int delta = Marshal.ReadInt32(lParam, OffsetMouseData) >> 16; // signed high word
                // Read here, not in the handler: the post below is asynchronous, and the user can
                // release the key before it runs.
                var notch = new WheelNotch(delta, IsDown(VK_CONTROL), IsDown(VK_SHIFT));
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                    () => Scrolled?.Invoke(notch));
                return 1; // ours: the taskbar underneath never sees it
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>Guarded against a second call: unlike the keyboard hook this one is installed and
    /// torn down repeatedly, every time the user toggles the setting.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnhookWindowsHookEx(_hook);
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
