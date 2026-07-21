using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ApoVolume.UI;

public partial class OsdWindow : Window
{
    private const string GlyphVolume = "\uE767"; // Segoe MDL2 'Volume'
    private const string GlyphMute = "\uE74F";   // Segoe MDL2 'Mute'
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(150);

    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private bool _updatingFromCode;

    public event Action<int>? PercentChangedByUser;

    public OsdWindow()
    {
        InitializeComponent();
        _hideTimer.Tick += (_, _) =>
        {
            if (IsMouseOver) return; // user is interacting: stay open, timer keeps ticking
            _hideTimer.Stop();
            var fade = new DoubleAnimation(1, 0, FadeDuration);
            fade.Completed += (_, _) => Hide();
            BeginAnimation(OpacityProperty, fade);
        };
        SourceInitialized += (_, _) => MakeNoActivate();
        MouseWheel += OnMouseWheel;
    }

    public void ShowVolume(int percent, bool muted, bool interactive)
    {
        _updatingFromCode = true;
        VolumeSlider.Value = percent;
        _updatingFromCode = false;

        PercentText.Text = percent.ToString();
        GlyphText.Text = muted ? GlyphMute : GlyphVolume;

        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Bottom - Height - 12;

        BeginAnimation(OpacityProperty, null); // cancel any running fade-out
        Opacity = 1;
        Show();
        _hideTimer.Stop();
        _hideTimer.Start(); // both paths auto-hide; IsMouseOver blocks the tick while hovered
    }

    private void OnSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingFromCode) return;
        PercentChangedByUser?.Invoke((int)Math.Round(e.NewValue));
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Not marked as updating-from-code: the change flows through ValueChanged like any
        // other user edit, and the slider itself clamps to its 0..100 range.
        VolumeSlider.Value += e.Delta > 0 ? 2 : -2;
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
