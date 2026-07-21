using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace ApoVolume.Input;

/// <summary>
/// System-wide WH_KEYBOARD_LL hook. Swallows volume keys (down and up) so the broken
/// Windows volume path and its flyout never engage; raises events on the WPF dispatcher.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int VK_VOLUME_MUTE = 0xAD;
    private const int VK_VOLUME_DOWN = 0xAE;
    private const int VK_VOLUME_UP = 0xAF;

    public event Action? VolumeUp;
    public event Action? VolumeDown;
    public event Action? MuteToggle;

    private readonly LowLevelKeyboardProc _proc; // field: keeps delegate alive for the native hook
    private readonly IntPtr _hook;

    public KeyboardHook()
    {
        _proc = Callback;
        using var module = Process.GetCurrentProcess().MainModule!;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module.ModuleName), 0);
        if (_hook == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to install low-level keyboard hook.");
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int vk = Marshal.ReadInt32(lParam); // vkCode is the first DWORD of KBDLLHOOKSTRUCT
            if (vk is VK_VOLUME_MUTE or VK_VOLUME_DOWN or VK_VOLUME_UP)
            {
                int msg = wParam.ToInt32();
                if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
                {
                    var handler = vk switch
                    {
                        VK_VOLUME_UP => VolumeUp,
                        VK_VOLUME_DOWN => VolumeDown,
                        _ => MuteToggle,
                    };
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => handler?.Invoke());
                }
                return 1; // swallow both down and up: no app or shell ever sees the key
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => UnhookWindowsHookEx(_hook);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
}
