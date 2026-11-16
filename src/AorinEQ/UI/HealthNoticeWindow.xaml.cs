using System.Windows;

namespace AorinEQ.UI;

/// <summary>What the Equalizer APO health monitor has to say, in a window of AorinEQ's own that
/// stays in the corner of the screen until it is dismissed.
///
/// It exists because a tray balloon is Windows' to show or not: with notifications switched off,
/// or Do Not Disturb on, "Equalizer APO stopped running on your playback device" was said to
/// nobody, and the fault was only found by opening Settings (reported 2026-11-15). It never takes
/// the focus — it appears at startup and while the user is typing somewhere else.
///
/// One message per window: the app closes the one on screen before showing the next.</summary>
public partial class HealthNoticeWindow
{
    /// <summary>The gap kept between the window and the edges of the screen's work area.</summary>
    internal const double ScreenMargin = 16;

    private readonly Action? _action;
    private OsdScreen.Target _target;

    /// <param name="action">What "Open Settings" does, or null for a message there is nothing to
    /// do about ("working again") — the button is then not offered.</param>
    public HealthNoticeWindow(string text, Action? action)
    {
        InitializeComponent();
        _action = action;
        MessageText.Text = text;
        ActionButton.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        // Moving onto a screen of another scale changes this window's DPI; the same position is
        // then exact — see OsdScreen's remarks.
        DpiChanged += (_, _) => Place();
    }

    /// <summary>Shows the window in the bottom-right corner of the screen in use.</summary>
    public void Present()
    {
        _target = OsdScreen.Active();
        Show();
        UpdateLayout();
        Place();
    }

    private void Place() => OsdScreen.MoveTo(this, _target,
        _target.WorkArea.Right - ActualWidth - ScreenMargin,
        _target.WorkArea.Bottom - ActualHeight - ScreenMargin);

    private void OnAction(object sender, RoutedEventArgs e)
    {
        Close();
        _action?.Invoke();
    }

    private void OnDismiss(object sender, RoutedEventArgs e) => Close();
}
