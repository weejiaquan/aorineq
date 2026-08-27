using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace AorinEQ.Core;

/// <summary>
/// The thread every system-wide low-level hook is installed on, and the message loop that
/// services their callbacks.
///
/// WHY IT EXISTS. A WH_KEYBOARD_LL / WH_MOUSE_LL callback is delivered to the thread that
/// INSTALLED the hook, and the system holds the input event it belongs to until that thread
/// picks it up. Install one from the WPF UI thread — which is where both of this app's hooks
/// lived until this class existed — and every keystroke and every mouse movement on the machine,
/// cursor motion included, now queues behind whatever else that thread happens to be doing.
///
/// On a warm run that is invisible. On a cold first start it is the whole of startup: a proxy
/// lookup with no network up yet, an antivirus scanning a bundle unpacked seconds ago, an audio
/// stack that has not finished starting. The machine appears to freeze — the reported symptom
/// was a mouse that would not move — and past LowLevelHooksTimeout (300 ms by default) Windows
/// stops calling the hook at all, silently, so the volume keys stay dead until a restart.
///
/// So the hooks get a thread that does nothing else, and no future slow startup step can ever
/// reach the machine's input again. The loop is a bare GetMessage, which is all a low-level hook
/// needs: the system delivers its callbacks DURING message retrieval, not as messages the loop
/// has to dispatch. The WM_RUN messages exist only to wake it for <see cref="Invoke{T}"/>.
///
/// CALLBACKS MUST STAY CHEAP ANYWAY. This thread removes the app's startup from the input path;
/// it does not license work inside a hook callback. Everything the callbacks touch is either a
/// snapshot published for them (<c>HudManager.IsOverVolumeWidget</c>) or an atomic field read
/// (<c>TrayIcon.IsOverIcon</c>), and the real handling still hops to the dispatcher.
/// </summary>
public sealed class HookThread : IDisposable
{
    private const uint WM_QUIT = 0x0012;
    private const uint WM_USER = 0x0400;
    /// <summary>Thread message asking the loop to drain <see cref="_queue"/>. A thread message
    /// has no window to collide with, so any value at or above WM_USER is ours.</summary>
    private const uint WM_RUN = WM_USER + 1;
    private const uint PM_NOREMOVE = 0x0000;

    /// <summary>How long <see cref="HookThread()"/> waits for the loop to own a message queue.
    /// Only ever hit if the thread cannot run at all, in which case failing loudly beats a
    /// process that hangs before it has drawn anything.</summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly ConcurrentQueue<Action> _queue = new();
    private uint _threadId;
    private bool _disposed;

    public HookThread()
    {
        // STA: the callbacks that will run here reach shell and WinForms helpers (icon metrics),
        // whose contract is a single-threaded apartment. Background, so a leaked instance can
        // never be the reason the process refuses to exit.
        _thread = new Thread(Run) { IsBackground = true, Name = "AorinEQ hooks" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_ready.Wait(StartTimeout))
            throw new InvalidOperationException("The hook thread did not start.");
    }

    /// <summary>Runs <paramref name="work"/> ON THE HOOK THREAD and returns what it returned,
    /// rethrowing on the caller's thread whatever it threw. Blocking, and deliberately: the
    /// callers are hook install and uninstall, and both have to know the answer before they can
    /// proceed. Called from the hook thread itself it simply runs inline, so a callback that
    /// needs it cannot deadlock against its own loop.</summary>
    public T Invoke<T>(Func<T> work)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Environment.CurrentManagedThreadId == _thread.ManagedThreadId)
            return work();

        T result = default!;
        Exception? error = null;
        using var done = new ManualResetEventSlim(false);
        _queue.Enqueue(() =>
        {
            try { result = work(); }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        });
        PostThreadMessage(_threadId, WM_RUN, IntPtr.Zero, IntPtr.Zero);
        done.Wait();
        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
        return result;
    }

    /// <summary>The void form of <see cref="Invoke{T}"/>, with the same semantics.</summary>
    public void Invoke(Action work) => Invoke(() => { work(); return true; });

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        // Forces the message queue into existence before anybody can post to it: PostThreadMessage
        // fails, silently and by design, against a thread that has never asked for a message. The
        // handshake below therefore has to come after this call, not after Thread.Start.
        PeekMessage(out _, IntPtr.Zero, WM_USER, WM_USER, PM_NOREMOVE);
        _ready.Set();

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.Message == WM_RUN)
                Drain();
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        // Work queued in the instant the quit was posted still runs, so a caller blocked in
        // Invoke is released rather than left waiting on a loop that has ended.
        Drain();
    }

    private void Drain()
    {
        while (_queue.TryDequeue(out var work))
            work();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PtX;
        public int PtY;
    }

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin,
        uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
