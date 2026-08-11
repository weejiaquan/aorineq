using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ApoVolume.Core;

namespace ApoVolume.UI;

/// <summary>OSD style D: a two-PNG shaped bar (empty.png background + full.png fill, clipped to the
/// current percent) with per-pixel hit testing — clicks/drags/wheel only affect opaque pixels, and
/// transparent pixels click through to whatever is beneath the window. One instance per loaded
/// skin; <see cref="App"/> recreates it whenever the active skin or style changes.</summary>
public partial class SkinOsdWindow : Window
{
    private readonly SkinInfo _info;
    // Hit shape = union of opaque pixels across ALL frames of BOTH layers, so an element that is
    // transparent in one animation frame stays clickable throughout.
    private readonly List<AlphaMap> _alphaMaps = new();
    private readonly SkinFrames _emptyFrames;
    private readonly SkinFrames _fullFrames;
    private readonly DispatcherTimer _emptyAnimTimer = new();
    private readonly DispatcherTimer _fullAnimTimer = new();
    private int _emptyFrameIndex;
    private int _fullFrameIndex;
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
        if (!info.IsValid)
            throw new ArgumentException($"Cannot render an invalid skin: {info.Error}", nameof(info));
        _info = info;

        InitializeComponent();

        _emptyFrames = SkinFrames.Load(info.EmptyPath, info.EmptyFrames, info.Fps);
        _fullFrames = SkinFrames.Load(info.FullPath, info.FullFrames, info.Fps);
        foreach (var frame in _emptyFrames.Frames) _alphaMaps.Add(new AlphaMap(frame));
        foreach (var frame in _fullFrames.Frames) _alphaMaps.Add(new AlphaMap(frame));

        EmptyImage.Source = _emptyFrames.Frames[0];
        FullImage.Source = _fullFrames.Frames[0];

        // Animated layers advance on their own cadence (per-frame delays), and only while the
        // window is visible — hiding stops the timers so an idle OSD costs nothing.
        if (_emptyFrames.IsAnimated)
            _emptyAnimTimer.Tick += (_, _) =>
            {
                _emptyFrameIndex = (_emptyFrameIndex + 1) % _emptyFrames.Frames.Count;
                EmptyImage.Source = _emptyFrames.Frames[_emptyFrameIndex];
                _emptyAnimTimer.Interval = _emptyFrames.Delays[_emptyFrameIndex];
            };
        if (_fullFrames.IsAnimated)
            _fullAnimTimer.Tick += (_, _) =>
            {
                _fullFrameIndex = (_fullFrameIndex + 1) % _fullFrames.Frames.Count;
                FullImage.Source = _fullFrames.Frames[_fullFrameIndex];
                _fullAnimTimer.Interval = _fullFrames.Delays[_fullFrameIndex];
            };
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false)
            {
                _emptyAnimTimer.Stop();
                _fullAnimTimer.Stop();
            }
        };

        Width = info.Width * info.Scale;
        Height = info.Height * info.Scale;

        if (info.Text is { Show: true } text)
        {
            PercentTextBlock.Visibility = Visibility.Visible;
            PercentTextBlock.Margin = new Thickness(text.X * info.Scale, text.Y * info.Scale, 0, 0);
        }

        _hideTimer.Tick += (_, _) =>
        {
            // IsMouseOver: user is interacting; _dragging: a drag can continue with the pointer
            // outside the window's bounds (IsMouseOver false) since OnMouseMove requires only
            // _dragging + the left button, not IsMouseOver — either way, stay open, timer keeps
            // ticking, and hiding resumes on its own once the drag ends.
            if (IsMouseOver || _dragging) return;
            _hideTimer.Stop();
            ReleaseDragIfActive(); // never hide out from under an in-progress drag's capture
                                    // (defensive: reachable paths above already require !_dragging)
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
            MakeNoActivate();
            HookWndProc();
        };
        MouseWheel += OnMouseWheel;
        // Same fade rescue as OsdWindow: entering mid-fade-out cancels the fade and re-arms the
        // hide delay instead of letting the OSD vanish under the pointer.
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
        LostMouseCapture += (_, _) => _dragging = false; // capture can be stolen (e.g. by another
            // window/element) without ever raising MouseLeftButtonUp — keep _dragging accurate.
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
        if (_info.Text is { Show: true })
            PercentTextBlock.Text = percent.ToString();

        double fillWidth = SkinMath.FillWidth(_info.Width, percent) * _info.Scale; // already clamped >= 0
        FillClip.Rect = new Rect(0, 0, fillWidth, Height);

        FullImage.Visibility = muted ? Visibility.Hidden : Visibility.Visible;
        EmptyImage.Opacity = muted ? 0.6 : 1.0;
        MuteBadge.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;

        var wa = SystemParameters.WorkArea;
        double left, top;
        try
        {
            (left, top) = OsdPosition.Compute(
                _anchor, Width, Height, wa.Left, wa.Top, wa.Width, wa.Height, _offsetX, _offsetY);
        }
        catch (ArgumentException)
        {
            // Same defensive fallback as OsdWindow: in-memory config could be mutated to an
            // invalid anchor before reaching here even though Settings.Load's Normalize()
            // guarantees a valid one on disk.
            (left, top) = OsdPosition.Compute(
                "bottom-center", Width, Height, wa.Left, wa.Top, wa.Width, wa.Height, 0, 0);
        }
        Left = left;
        Top = top;

        BeginAnimation(OpacityProperty, null); // cancel any running fade-out
        Opacity = 1;
        Show();
        if (_emptyFrames.IsAnimated && !_emptyAnimTimer.IsEnabled)
        {
            _emptyAnimTimer.Interval = _emptyFrames.Delays[_emptyFrameIndex];
            _emptyAnimTimer.Start();
        }
        if (_fullFrames.IsAnimated && !_fullAnimTimer.IsEnabled)
        {
            _fullAnimTimer.Interval = _fullFrames.Delays[_fullFrameIndex];
            _fullAnimTimer.Start();
        }
        _hideTimer.Stop();
        _hideTimer.Start(); // both paths auto-hide; IsMouseOver blocks the tick while hovered
    }

    private bool IsOpaqueAt(System.Windows.Point windowPoint)
    {
        int px = (int)(windowPoint.X / _info.Scale);
        int py = (int)(windowPoint.Y / _info.Scale);
        foreach (var map in _alphaMaps)
        {
            if (map.IsOpaque(px, py)) return true;
        }
        return false;
    }

    private void RaisePercentFromWindowPoint(System.Windows.Point windowPoint)
    {
        int percent = SkinMath.PercentFromX(windowPoint.X / _info.Scale, _info.Width);
        PercentChangedByUser?.Invoke(percent);
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(this);
        if (!IsOpaqueAt(pos)) return;
        // The initial click always sets the percent; CaptureMouse() additionally keeps the drag
        // alive even if the pointer crosses a transparent pixel mid-drag. Capture can fail (e.g.
        // another element/window already holds it), so _dragging only tracks whether it actually
        // succeeded — MouseMove below requires _dragging, so a failed capture just means this
        // click doesn't continue into a drag, rather than getting stuck in a bad state.
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
    /// it's still holding capture for an in-progress drag (e.g. the pointer dragged outside the
    /// window's bounds, so IsMouseOver reads false even though the button is still held).</summary>
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

    // No OnClosing override, unlike OsdWindow: that window is a permanent singleton for the
    // app's whole lifetime, so it cancels Close() defensively. This window is deliberately
    // torn down and recreated by App whenever the active skin or style changes (there's no
    // taskbar/Alt+F4 affordance to close it unexpectedly — no-activate + ShowActivated="False"
    // means it can never receive focus), so a real Close() here is correct and expected.

    private void MakeNoActivate()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        const int GWL_EXSTYLE = -20;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
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

        if (IsOpaqueAtScreenPoint(screenX, screenY)) return IntPtr.Zero; // default hit-test (HTCLIENT) stands

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

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
}
