using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ApoVolume.Core;

namespace ApoVolume.UI;

public partial class OsdWindow : Window
{
    private const string GlyphVolume = "\uE767"; // Segoe MDL2 'Volume'
    private const string GlyphMute = "\uE74F";   // Segoe MDL2 'Mute'
    private const double DarkPillWidth = 300;
    private const double DarkPillHeight = 64;
    private const double MinimalBarWidthFraction = 0.4; // 40% of the work-area width
    private const double DefaultMargin = 12; // matches OsdPosition.Compute's own default

    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private bool _updatingFromCode;

    // Behavior config, pushed in from Settings via ApplyConfig; sensible defaults match
    // Settings.Default so the window behaves reasonably even if ApplyConfig is never called.
    private string _style = OsdStyles.DarkPill;
    private string _anchor = "bottom-center";
    private int _offsetX;
    private int _offsetY;
    private bool _animationEnabled = true;
    private TimeSpan _fadeDuration = TimeSpan.FromMilliseconds(150);

    /// <summary>Percent step applied per wheel notch; kept in sync with VolumeState.StepPercent
    /// via ApplyConfig so the OSD's wheel handling and the global hotkeys agree.</summary>
    public int StepPercent { get; set; } = 2;

    public event Action<int>? PercentChangedByUser;

    public OsdWindow()
    {
        InitializeComponent();
        _hideTimer.Tick += (_, _) =>
        {
            if (IsMouseOver) return; // user is interacting: stay open, timer keeps ticking
            _hideTimer.Stop();
            if (!_animationEnabled)
            {
                Hide(); // instant hide, no fade
                return;
            }
            var fade = new DoubleAnimation(1, 0, _fadeDuration);
            fade.Completed += (_, _) => Hide();
            BeginAnimation(OpacityProperty, fade);
        };
        SourceInitialized += (_, _) => MakeNoActivate();
        MouseWheel += OnMouseWheel;
    }

    /// <summary>Applies style/position/behavior settings to the window. Called once at App
    /// startup and again whenever the user changes OSD settings (wired by a later task); safe
    /// to call any number of times, including while the window is hidden.</summary>
    public void ApplyConfig(Settings s)
    {
        _style = s.OsdStyle;
        _anchor = s.OsdAnchor;
        _offsetX = s.OsdOffsetX;
        _offsetY = s.OsdOffsetY;
        _animationEnabled = s.AnimationEnabled;
        _fadeDuration = TimeSpan.FromMilliseconds(s.AnimationMs);
        _hideTimer.Interval = TimeSpan.FromSeconds(s.HideDelaySeconds);
        StepPercent = s.StepPercent;

        ApplyStyle();
    }

    public void ShowVolume(int percent, bool muted, bool interactive)
    {
        _updatingFromCode = true;
        VolumeSlider.Value = percent;
        MinimalSlider.Value = percent;
        _updatingFromCode = false;

        PercentText.Text = percent.ToString();
        MinimalPercentText.Text = percent.ToString();
        GlyphText.Text = muted ? GlyphMute : GlyphVolume;

        var wa = SystemParameters.WorkArea;
        bool isMinimal = _style == OsdStyles.MinimalBar;
        if (isMinimal)
        {
            Width = wa.Width * MinimalBarWidthFraction;
            // Rows are Auto-sized; measure against the chosen width to get the real content
            // height instead of guessing a constant that could drift from the actual layout.
            MinimalBarRoot.Measure(new System.Windows.Size(Width, double.PositiveInfinity));
            Height = MinimalBarRoot.DesiredSize.Height;
            MinimalFill.Width = Math.Max(0, Width * percent / 100.0);
        }
        else
        {
            Width = DarkPillWidth;
            Height = DarkPillHeight;
        }

        // minimal-bar sits flush against the edge(s) its anchor names (margin 0); the two
        // center-vertical anchors (left-center/right-center) aren't against a top/bottom edge,
        // so they keep the standard margin. dark-pill always uses the standard margin.
        double margin = isMinimal && !IsCenterVerticalAnchor(_anchor) ? 0.0 : DefaultMargin;

        double left, top;
        try
        {
            (left, top) = OsdPosition.Compute(
                _anchor, Width, Height, wa.Left, wa.Top, wa.Width, wa.Height, _offsetX, _offsetY, margin);
        }
        catch (ArgumentException)
        {
            // Settings.Load's Normalize() guarantees a valid anchor, but in-memory config could
            // still be mutated to something invalid before reaching here — fall back rather
            // than crash the render path.
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

    /// <summary>Swaps the visible root (dark-pill vs. minimal-bar) and, for minimal-bar, the
    /// chip's side of the track so it sits toward screen center: below the track for
    /// top-anchored positions, above it for bottom- and center-anchored ones.</summary>
    private void ApplyStyle()
    {
        bool isMinimal = _style == OsdStyles.MinimalBar;
        DarkPillRoot.Visibility = isMinimal ? Visibility.Collapsed : Visibility.Visible;
        MinimalBarRoot.Visibility = isMinimal ? Visibility.Visible : Visibility.Collapsed;
        if (!isMinimal) return;

        bool chipBelow = _anchor is "top-left" or "top-center" or "top-right";
        Grid.SetRow(MinimalChip, chipBelow ? 1 : 0);
        Grid.SetRow(MinimalTrackHost, chipBelow ? 0 : 1);
        MinimalChip.Margin = chipBelow ? new Thickness(0, 4, 0, 0) : new Thickness(0, 0, 0, 4);
    }

    private static bool IsCenterVerticalAnchor(string anchor) => anchor is "left-center" or "right-center";

    private void OnSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingFromCode) return;
        PercentChangedByUser?.Invoke((int)Math.Round(e.NewValue));
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Not marked as updating-from-code: the change flows through ValueChanged like any
        // other user edit, and the slider itself clamps to its 0..100 range. Only the slider
        // for the currently active style is nudged, so the other (collapsed) one doesn't also
        // fire PercentChangedByUser for the same wheel notch.
        var slider = _style == OsdStyles.MinimalBar ? MinimalSlider : VolumeSlider;
        slider.Value += e.Delta > 0 ? StepPercent : -StepPercent;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true; // never destroy; App owns lifetime
        Hide();
    }

    private void MakeNoActivate()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        const int GWL_EXSTYLE = -20;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
