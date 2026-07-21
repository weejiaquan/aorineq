using System.Windows;
using System.Windows.Navigation;

namespace ApoVolume.UI;

public partial class SettingsWindow : Window
{
    private bool _initializing = true;

    public event Action<bool>? AutostartChanged;
    public event Action<bool>? RunAsAdminChanged;

    public SettingsWindow(bool autostartEnabled, bool runAsAdmin, bool isElevated, string version)
    {
        InitializeComponent();
        AutostartBox.IsChecked = autostartEnabled;
        RunAsAdminBox.IsChecked = runAsAdmin;
        ElevationStateText.Text = isElevated
            ? "Currently running elevated."
            : runAsAdmin
                ? "Not elevated in this session — restart the app (or approve the prompt) to apply."
                : "Currently running without elevation.";
        VersionText.Text = "apo-volume " + version;
        _initializing = false;
    }

    public void SyncState(bool autostartEnabled, bool runAsAdmin, bool isElevated)
    {
        _initializing = true;
        AutostartBox.IsChecked = autostartEnabled;
        RunAsAdminBox.IsChecked = runAsAdmin;
        ElevationStateText.Text = isElevated ? "Currently running elevated."
            : runAsAdmin ? "Not elevated in this session — restart the app (or approve the prompt) to apply."
            : "Currently running without elevation.";
        _initializing = false;
    }

    private void OnAutostartChanged(object sender, RoutedEventArgs e)
    {
        if (!_initializing) AutostartChanged?.Invoke(AutostartBox.IsChecked == true);
    }

    private void OnRunAsAdminChanged(object sender, RoutedEventArgs e)
    {
        if (!_initializing) RunAsAdminChanged?.Invoke(RunAsAdminBox.IsChecked == true);
    }

    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        // When this window is running in an elevated session, ShellExecute here inherits the
        // elevated token, so the browser process it launches is elevated too.
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri)
        {
            UseShellExecute = true,
        });
        e.Handled = true;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true; // App owns lifetime; hide like the OSD
        Hide();
    }
}
