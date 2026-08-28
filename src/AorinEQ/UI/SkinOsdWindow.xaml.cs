using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AorinEQ.Core;
using AorinEQ.Input;

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

    /// <summary>The AirPlay strip, stacked under the skin's own artwork. It draws the SKIN's
    /// AirPlay bar when the skin ships one and a native strip when it does not - which is what
    /// every skin written before this release gets, and is the whole of the legacy story.</summary>
    private readonly AirPlayBarView _airPlayBar = new() { Visibility = Visibility.Collapsed };

    /// <summary>True while the device menu is open: a ContextMenu is its own window, so the
    /// pointer being over it leaves IsMouseOver false and the OSD would fade out underneath it.</summary>
    private bool _airPlayMenuOpen;

    private IReadOnlyList<AorinEQ.Core.Raop.AirPlayDevice> _airPlayDevices =
        Array.Empty<AorinEQ.Core.Raop.AirPlayDevice>();
    private string? _airPlayCurrentId;
    private string? _airPlayChosenId;

    public event Action<AorinEQ.Core.Raop.AirPlayDevice>? AirPlayDeviceChosen;
    public event Action? AirPlayDisconnectRequested;
    public event Action? AirPlayRescanRequested;
    public event Action<int>? AirPlayVolumeSetByUser;
    /// <summary>Which fade-out is still allowed to hide this window. Cancelling a fade does not
    /// cancel its Completed event — see <see cref="OsdFade"/> for the crash-free but very visible
    /// bug that came of assuming it did.</summary>
    private readonly OsdFade _fade = new();

    // Behavior config, pushed in from Settings via ApplyConfig; same defaults as OsdWindow so the
    // window behaves reasonably even if ApplyConfig is never called.
    private string _anchor = "bottom-center";
    private int _offsetX;
    private int _offsetY;
    private bool _animationEnabled = true;
    private TimeSpan _fadeDuration = TimeSpan.FromMilliseconds(150);

    private bool _dragging;

    public event Action<int>? PercentChangedByUser;

    /// <summary>A raw wheel notch over an OPAQUE pixel of the skin. Raised rather than applied
    /// here for the same reason <see cref="OsdWindow.VolumeScrolled"/> is: one accumulator and one
    /// mute policy for every surface that scrolls.</summary>
    public event Action<WheelNotch>? VolumeScrolled;

    public SkinOsdWindow(SkinInfo info)
    {
        InitializeComponent();

        _view = new SkinView(info);
        ViewHost.Children.Add(_view);
        ViewHost.Children.Add(_airPlayBar);

        Width = _view.LogicalWidth;
        Height = _view.LogicalHeight;

        _airPlayBar.DropdownRequested += OpenAirPlayMenu;
        _airPlayBar.VolumeSetByUser += percent => AirPlayVolumeSetByUser?.Invoke(percent);

        _hideTimer.Tick += (_, _) =>
        {
            // IsMouseOver: user is interacting; _dragging: a drag can continue with the pointer
            // outside the window's bounds (IsMouseOver false) since OnMouseMove requires only
            // _dragging + the left button, not IsMouseOver — either way, stay open, timer keeps
            // ticking, and hiding resumes on its own once the drag ends.
            if (IsMouseOver || _dragging || _airPlayMenuOpen) return;
            _hideTimer.Stop();
            ReleaseDragIfActive(); // never hide out from under an in-progress drag's capture
            if (!_animationEnabled)
            {
                Hide(); // instant hide, no fade
                return;
            }
            var token = _fade.Begin();
            var fade = new DoubleAnimation(1, 0, _fadeDuration);
            fade.Completed += (_, _) => { if (_fade.MayHide(token)) Hide(); };
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
            CancelFade();
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
    }

    /// <summary>Shows or hides the AirPlay strip. The skin's own AirPlay artwork is preferred;
    /// null falls back to the native strip, which is what a skin without one gets.</summary>
    public void SetAirPlay(AirPlayBarState state)
    {
        _airPlayBar.SetSkin(_view.Info.AirPlay, _view.RenderScale);
        _airPlayBar.SetState(state);

        // The window is sized explicitly rather than by SizeToContent, so the strip has to be
        // measured into the height here - before ShowVolume positions against it.
        double extra = 0;
        double width = _view.LogicalWidth;
        if (state.Visible)
        {
            _airPlayBar.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            extra = _airPlayBar.DesiredSize.Height;

            // The strip is allowed its own size by the format, so the window has to be the wider
            // of the two - otherwise a strip wider than the volume bar is clipped, and a dropdown
            // hit region near its right-hand end becomes unreachable.
            width = Math.Max(width, _airPlayBar.DesiredSize.Width);
        }
        Width = width;
        Height = _view.LogicalHeight + extra;
    }

    public void SetAirPlayDevices(IReadOnlyList<AorinEQ.Core.Raop.AirPlayDevice> devices, string? currentId, string? chosenId)
    {
        _airPlayDevices = devices;
        _airPlayCurrentId = currentId;
        _airPlayChosenId = chosenId;
    }

    private void OpenAirPlayMenu()
    {
        _airPlayMenuOpen = true;
        Activate();
        AirPlayMenu.Show(_airPlayBar, _airPlayDevices, _airPlayCurrentId, _airPlayChosenId,
            device => AirPlayDeviceChosen?.Invoke(device),
            () => AirPlayDisconnectRequested?.Invoke(),
            () => AirPlayRescanRequested?.Invoke(),
            closed: () =>
            {
                _airPlayMenuOpen = false;
                _hideTimer.Stop();
                _hideTimer.Start();
            });
    }

    public void ShowVolume(int percent, bool muted, bool interactive)
    {
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

        CancelFade();
        Show();
        _hideTimer.Stop();
        _hideTimer.Start(); // both paths auto-hide; IsMouseOver blocks the tick while hovered
    }

    /// <summary>Stops a fade-out and restores full opacity, so the window this rescues stays up.
    ///
    /// The <see cref="OsdFade.Cancel"/> is not optional and not belt-and-braces: removing an
    /// animation with <c>BeginAnimation(OpacityProperty, null)</c> leaves its Completed handler to
    /// fire regardless, and that handler hides the window. Without withdrawing its permission
    /// first, a volume key pressed during the fade showed the OSD and lost it again milliseconds
    /// later — the "sometimes the skin doesn't show" bug.</summary>
    private void CancelFade()
    {
        _fade.Cancel();
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
    }

    /// <summary>Whether a point in this window is "solid" - which decides both the drag handling
    /// and, through WM_NCHITTEST, whether the click reaches this window at all.
    ///
    /// The AirPlay strip has to be included or it is DEAD on a skinned OSD: it is stacked below
    /// the skin rather than drawn inside it, so asking only the skin returns transparent for every
    /// pixel of the strip, the window reports HTTRANSPARENT, and the click lands on whatever is
    /// behind the OSD. Nothing about the strip looked wrong on screen - it simply never received
    /// a mouse event.
    ///
    /// Its whole rectangle counts as solid, rather than alpha-testing its artwork the way the
    /// volume bar does. A control you have to be able to press should not have holes in it, and a
    /// skin author who draws a strip with transparent gaps has not asked for those gaps to fall
    /// through to the desktop.</summary>
    private bool IsOpaqueAt(System.Windows.Point windowPoint)
    {
        if (_airPlayBar.Visibility == Visibility.Visible
            && windowPoint.Y >= _view.LogicalHeight
            && windowPoint.Y < _view.LogicalHeight + _airPlayBar.ActualHeight
            && windowPoint.X >= 0 && windowPoint.X < _airPlayBar.ActualWidth)
        {
            return true;
        }

        return _view.IsOpaqueAt(windowPoint);
    }

    private void RaisePercentFromWindowPoint(System.Windows.Point windowPoint) =>
        PercentChangedByUser?.Invoke(_view.PercentFromX(windowPoint.X));

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(this);
        // The strip handles its own press - dropdown or receiver volume. Without this the window
        // would ALSO read it as a volume-bar drag and set the system volume from the x position.
        if (IsInAirPlayStrip(pos)) return;
        if (!IsOpaqueAt(pos)) return;
        // The initial click always sets the percent; CaptureMouse() additionally keeps the drag
        // alive even if the pointer crosses a transparent pixel mid-drag. Capture can fail, so
        // _dragging only tracks whether it actually succeeded.
        _dragging = CaptureMouse();
        RaisePercentFromWindowPoint(pos);
    }

    private bool IsInAirPlayStrip(System.Windows.Point windowPoint) =>
        _airPlayBar.Visibility == Visibility.Visible
        && windowPoint.Y >= _view.LogicalHeight;

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

    /// <summary>Per-pixel as ever — a notch over a transparent pixel belongs to whatever is behind
    /// the window, not to us. What changed is what happens after: the notch goes up to the one
    /// accumulator instead of being treated as a whole step here, which a high-resolution wheel
    /// sends several of per detent.</summary>
    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!IsOpaqueAt(e.GetPosition(this))) return;
        VolumeScrolled?.Invoke(new WheelNotch(
            e.Delta,
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control),
            Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)));
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
