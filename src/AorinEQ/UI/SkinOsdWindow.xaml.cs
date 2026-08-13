using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AorinEQ.Core;

namespace AorinEQ.UI;

/// <summary>OSD style D: a skin, shown transiently when the volume changes, with per-pixel hit
/// testing — clicks/drags/wheel only affect opaque pixels, and transparent pixels click through to
/// whatever is beneath the window. One instance per loaded skin; <see cref="App"/> recreates it
/// whenever the active skin or style changes.
///
/// The skin is COMPOSED AND DRAWN BY <see cref="SkinView"/>, not here. What remains in this class
/// is everything specific to being a transient OSD: anchored placement, the auto-hide fade, and
/// drag/wheel-to-set-volume. The HUD's volume widget hosts the same view with its own behaviour,
/// which is why the two surfaces cannot drift apart.</summary>
public partial class SkinOsdWindow : Window
{
    private readonly SkinView _view;
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };

    // Behavior config, pushed in from Settings via ApplyConfig; same defaults as OsdWindow so the
    // window behaves reasonably even if ApplyConfig is never called.
    private string _anchor = "bottom-center";
    private int _offsetX;
    private int _offsetY;
    private bool _animationEnabled = true;
    private TimeSpan _fadeDuration = TimeSpan.FromMilliseconds(150);

    private bool _dragging;
    private int _lastPercent;

    /// <summary>Percent step applied per wheel notch; kept in sync via ApplyConfig.</summary>
    public int StepPercent { get; set; } = 2;

    public event Action<int>? PercentChangedByUser;

    public SkinOsdWindow(SkinInfo info)
    {
        InitializeComponent();

        _view = new SkinView(info);
        ViewHost.Children.Add(_view);

        Width = _view.LogicalWidth;
        Height = _view.LogicalHeight;

        _hideTimer.Tick += (_, _) =>
        {
            // IsMouseOver: user is interacting; _dragging: a drag can continue with the pointer
            // outside the window's bounds (IsMouseOver false) since OnMouseMove requires only
            // _dragging + the left button, not IsMouseOver — either way, stay open, timer keeps
            // ticking, and hiding resumes on its own once the drag ends.
            if (IsMouseOver || _dragging) return;
            _hideTimer.Stop();
            ReleaseDragIfActive(); // never hide out from under an in-progress drag's capture
            if (!_animationEnabled)
            {
                Hide(); // instant hide, no fade
                return;
            }
            var fade = new DoubleAnimation(1, 0, _fadeDuration);
            fade.Completed += (_, _) => Hide();
            BeginAnimation(OpacityProperty, fade);
        };
        SourceInitialized += (_, _) =>
        {
            HudWindowStyle.MakeToolWindow(this, clickThrough: false);
            HookWndProc();
        };
        MouseWheel += OnMouseWheel;
        // Entering mid-fade-out cancels the fade and re-arms the hide delay instead of letting the
        // OSD vanish under the pointer.
        MouseEnter += (_, _) =>
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            if (IsVisible)
            {
                _hideTimer.Stop();
                _hideTimer.Start();
            }
        };
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        LostMouseCapture += (_, _) => _dragging = false; // capture can be stolen without ever
            // raising MouseLeftButtonUp — keep _dragging accurate.
    }

    /// <summary>Decodes a PNG with BitmapCacheOption.OnLoad so the file handle is released
    /// immediately — the skin folder must not stay locked while the OSD is showing. Also sets
    /// BitmapCreateOptions.IgnoreImageCache: WPF's process-wide bitmap cache otherwise keys on the
    /// URI alone, so reloading a skin whose PNGs were edited in place (same path, new bytes) would
    /// silently serve the stale cached image instead of the updated one.</summary>
    internal static BitmapImage LoadBitmap(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bmp.UriSource = new Uri(path, UriKind.Absolute);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Applies position/behavior settings. Called once at construction time by App and
    /// again whenever settings change; safe to call any number of times, including while hidden.</summary>
    public void ApplyConfig(Settings s)
    {
        _anchor = s.OsdAnchor;
        _offsetX = s.OsdOffsetX;
        _offsetY = s.OsdOffsetY;
        _animationEnabled = s.AnimationEnabled;
        _fadeDuration = TimeSpan.FromMilliseconds(s.AnimationMs);
        _hideTimer.Interval = TimeSpan.FromSeconds(s.HideDelaySeconds);
        StepPercent = s.StepPercent;
    }

    public void ShowVolume(int percent, bool muted, bool interactive)
    {
        _lastPercent = percent;
        _view.SetVolume(percent, muted);

        var wa = SystemParameters.WorkArea;
        double left, top;
        try
        {
            (left, top) = OsdPosition.Compute(
                _anchor, Width, Height, wa.Left, wa.Top, wa.Width, wa.Height, _offsetX, _offsetY);
        }
        catch (ArgumentException)
        {
            // Defensive fallback: in-memory config could be mutated to an invalid anchor before
            // reaching here even though Settings.Load's Normalize() guarantees a valid one on disk.
            (left, top) = OsdPosition.Compute(
                "bottom-center", Width, Height, wa.Left, wa.Top, wa.Width, wa.Height, 0, 0);
        }
        Left = left;
        Top = top;

        BeginAnimation(OpacityProperty, null); // cancel any running fade-out
        Opacity = 1;
        Show();
        _hideTimer.Stop();
        _hideTimer.Start(); // both paths auto-hide; IsMouseOver blocks the tick while hovered
    }

    private bool IsOpaqueAt(System.Windows.Point windowPoint) => _view.IsOpaqueAt(windowPoint);

    private void RaisePercentFromWindowPoint(System.Windows.Point windowPoint) =>
        PercentChangedByUser?.Invoke(_view.PercentFromX(windowPoint.X));

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(this);
        if (!IsOpaqueAt(pos)) return;
        // The initial click always sets the percent; CaptureMouse() additionally keeps the drag
        // alive even if the pointer crosses a transparent pixel mid-drag. Capture can fail, so
        // _dragging only tracks whether it actually succeeded.
        _dragging = CaptureMouse();
        RaisePercentFromWindowPoint(pos);
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(this);
        if (!IsOpaqueAt(pos)) return;
        RaisePercentFromWindowPoint(pos);
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => ReleaseDragIfActive();

    /// <summary>No-op unless a drag is in progress; otherwise releases mouse capture and clears
    /// the flag. Shared by button-up and the auto-hide path, which must not hide this window while
    /// it's still holding capture for an in-progress drag.</summary>
    private void ReleaseDragIfActive()
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var pos = e.GetPosition(this);
        if (!IsOpaqueAt(pos)) return;
        int next = Math.Clamp(_lastPercent + (e.Delta > 0 ? StepPercent : -StepPercent), 0, 100);
        PercentChangedByUser?.Invoke(next);
    }

    /// <summary>Stops the view's animation timers on the way out. A DispatcherTimer left running
    /// roots the window and every decoded frame behind it — and App tears this window down and
    /// rebuilds it on every skin change.</summary>
    protected override void OnClosed(EventArgs e)
    {
        _view.StopAnimations();
        _hideTimer.Stop();
        base.OnClosed(e);
    }

    /// <summary>Hooks WM_NCHITTEST so transparent pixels click through to whatever is beneath this
    /// window instead of capturing the click themselves.</summary>
    private void HookWndProc()
    {
        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        source.AddHook(WndProc);
    }

    private const int WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_NCHITTEST) return IntPtr.Zero;

        long lp = lParam.ToInt64();
        int screenX = unchecked((short)(lp & 0xFFFF));
        int screenY = unchecked((short)((lp >> 16) & 0xFFFF));

        if (IsOpaqueAtScreenPoint(screenX, screenY)) return IntPtr.Zero; // default hit-test stands

        handled = true;
        return new IntPtr(HTTRANSPARENT);
    }

    /// <summary>Converts a WM_NCHITTEST screen point (physical pixels) into this window's client
    /// coordinate space (the same DIP space e.GetPosition(this) reports) via GetWindowRect (also
    /// physical pixels, so no DPI math needed for the subtraction) followed by the
    /// PresentationSource's device-to-DIP transform, then checks the alpha map.</summary>
    private bool IsOpaqueAtScreenPoint(int screenX, int screenY)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(hwnd, out var rect)) return true; // fail open: don't break clicks on error

        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null) return true;

        var relativeDevice = new System.Windows.Point(screenX - rect.Left, screenY - rect.Top);
        var windowPoint = source.CompositionTarget.TransformFromDevice.Transform(relativeDevice);
        return IsOpaqueAt(windowPoint);
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
}
