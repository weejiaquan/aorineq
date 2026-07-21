using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ApoVolume.UI;

public partial class OsdWindow : Window
{
    private const string GlyphVolume = "";
    private const string GlyphMute = "";

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
            Hide();
        };
        SourceInitialized += (_, _) => MakeNoActivate();
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

        Show();
        _hideTimer.Stop();
        if (!interactive) _hideTimer.Start();
        else _hideTimer.Start(); // interactive also auto-hides, but IsMouseOver blocks it while hovered
    }

    private void OnSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingFromCode) return;
        PercentChangedByUser?.Invoke((int)Math.Round(e.NewValue));
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
