using System.IO;
using System.Windows;
using System.Windows.Navigation;
using ApoVolume.Core;

namespace ApoVolume.UI;

/// <summary>First-run setup wizard for Equalizer APO. Two modes: blocking (startup found EAPO
/// missing — closing without success exits the app) and informational ("Setup guide…" from
/// Settings / --onboarding: shows current state, closing just closes). One state machine:
/// Explain → Downloading → InstallerRunning → re-detect → (Retry | Configurator | Success);
/// Success offers the audio-service restart that substitutes for a reboot.</summary>
public partial class OnboardingWindow : Window
{
    private enum Step
    {
        Explain,
        Downloading,
        InstallerRunning,
        NeedsDevice,   // installed, but not enabled on the default playback device
        Success,
    }

    private readonly bool _blocking;
    private Step _step;
    private System.Threading.CancellationTokenSource? _downloadCancel;

    /// <summary>Raised when the wizard is done: true → EAPO is usable, continue startup;
    /// false → user chose to exit (blocking mode only).</summary>
    public event Action<bool>? Completed;

    public OnboardingWindow(bool blocking)
    {
        _blocking = blocking;
        InitializeComponent();
        EnterStateForCurrentDetection(initial: true);
    }

    /// <summary>Picks the wizard step matching live detection — the entry point and every
    /// re-detect go through here so the UI can never disagree with reality.</summary>
    private void EnterStateForCurrentDetection(bool initial = false)
    {
        switch (EapoDetection.Detect())
        {
            case EapoStatus.NotInstalled:
                Show(Step.Explain,
                    heading: "Set up Equalizer APO",
                    body: "apo-volume changes your volume through Equalizer APO, a free, open-source "
                        + "system-wide audio processor. It isn't installed on this PC yet.\n\n"
                        + "apo-volume can download the official installer and start it for you.",
                    primary: "Download and install…",
                    secondary: _blocking ? "Exit apo-volume" : "Close");
                break;
            case EapoStatus.InstalledInactive:
                Show(Step.NeedsDevice,
                    heading: "Almost there — pick your playback device",
                    body: "Equalizer APO is installed, but it isn't enabled on your current playback "
                        + "device, so volume changes won't be audible there.\n\n"
                        + "Open the Configurator, tick the checkbox next to your speakers or "
                        + "headphones, and click OK.",
                    primary: "Open Configurator",
                    secondary: _blocking ? "Exit apo-volume" : "Close");
                break;
            case EapoStatus.Active:
                Show(Step.Success,
                    heading: initial && !_blocking ? "Everything is set up" : "Equalizer APO is ready",
                    body: "Equalizer APO is installed and enabled on your current playback device."
                        + (initial ? "" : "\n\nIf volume changes aren't audible yet, Windows' audio "
                        + "engine still needs a restart — use the button below (or restart your PC)."),
                    primary: _blocking ? "Start apo-volume" : "Close",
                    secondary: initial ? null : "Restart audio now");
                break;
        }
    }

    private void Show(Step step, string heading, string body, string primary, string? secondary,
        string? guidance = null)
    {
        _step = step;
        HeadingText.Text = heading;
        BodyText.Text = body;
        GuidanceText.Text = guidance ?? "";
        GuidanceText.Visibility = guidance is null ? Visibility.Collapsed : Visibility.Visible;
        DownloadProgress.Visibility = step == Step.Downloading ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Visibility = Visibility.Collapsed;
        PrimaryButton.Content = primary;
        PrimaryButton.IsEnabled = true;
        SecondaryButton.Content = secondary ?? "";
        SecondaryButton.Visibility = secondary is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnPrimary(object sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case Step.Explain:
                await RunDownloadAndInstaller();
                break;
            case Step.NeedsDevice:
                await RunConfigurator();
                break;
            case Step.Success:
                Finish(proceed: true);
                break;
            case Step.Downloading:
            case Step.InstallerRunning:
                break; // primary is disabled in these states; defensive
        }
    }

    private async void OnSecondary(object sender, RoutedEventArgs e)
    {
        if (_step == Step.Success)
        {
            await RestartAudioServices();
            return;
        }
        Finish(proceed: false);
    }

    private async Task RunDownloadAndInstaller()
    {
        Show(Step.Downloading,
            heading: "Downloading Equalizer APO…",
            body: "Fetching the official installer from SourceForge.",
            primary: "Downloading…",
            secondary: "Cancel");
        PrimaryButton.IsEnabled = false;
        DownloadProgress.IsIndeterminate = false;

        var dest = Path.Combine(Path.GetTempPath(), "EqualizerAPO-Setup.exe");
        _downloadCancel = new System.Threading.CancellationTokenSource();
        try
        {
            await InstallerDownload.DownloadAsync(InstallerDownload.OfficialUrl, dest,
                new Progress<double>(p =>
                {
                    if (p < 0) { DownloadProgress.IsIndeterminate = true; return; }
                    DownloadProgress.Value = p * 100;
                }),
                _downloadCancel.Token);
        }
        catch (InvalidOperationException ex)
        {
            if (_downloadCancel.IsCancellationRequested)
            {
                EnterStateForCurrentDetection();
                return;
            }
            EnterStateForCurrentDetection(); // back to Explain
            ErrorText.Text = ex.Message + " You can also install manually from equalizerapo.com.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        finally
        {
            _downloadCancel = null;
        }

        Show(Step.InstallerRunning,
            heading: "Installer running…",
            body: "Follow the Equalizer APO installer.",
            primary: "Waiting for the installer…",
            secondary: null,
            guidance: "When the Configurator window appears, tick the checkbox next to your "
                + "speakers or headphones, then click OK and finish the installer.");
        PrimaryButton.IsEnabled = false;

        try
        {
            // The installer elevates itself (its own UAC prompt) and runs interactively — silent
            // install is not supported upstream, and device selection genuinely needs the user.
            var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dest)
            {
                UseShellExecute = true,
            });
            if (proc is not null)
                await proc.WaitForExitAsync();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // UAC declined or launch failure: fall through to re-detect, error shown below if unchanged.
        }

        EnterStateForCurrentDetection();
        if (_step == Step.Explain)
        {
            ErrorText.Text = "The installer didn't complete. You can try again, or install "
                + "manually from equalizerapo.com and reopen this guide.";
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private async Task RunConfigurator()
    {
        var configurator = EapoDetection.GetConfiguratorPath();
        if (configurator is null)
        {
            EnterStateForCurrentDetection();
            return;
        }
        PrimaryButton.IsEnabled = false;
        try
        {
            // Configurator needs elevation to register the APO on a device.
            var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(configurator)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            if (proc is not null)
                await proc.WaitForExitAsync();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // UAC declined — nothing changed; re-detect keeps us on this page.
        }
        EnterStateForCurrentDetection();
    }

    /// <summary>Community-verified reboot substitute (EAPO ticket #214): restart the audio
    /// services so a freshly registered APO starts processing. One elevated helper, one UAC.</summary>
    private async Task RestartAudioServices()
    {
        SecondaryButton.IsEnabled = false;
        try
        {
            var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                "/c net stop /y AudioEndpointBuilder && net start AudioEndpointBuilder && net start Audiosrv")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            });
            if (proc is not null)
                await proc.WaitForExitAsync();
            BodyText.Text = "Audio restarted. Equalizer APO is now processing your playback device.";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            ErrorText.Text = "Elevation was declined — restart your PC instead to finish the setup.";
            ErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            SecondaryButton.IsEnabled = true;
        }
    }

    private void Finish(bool proceed)
    {
        var handler = Completed;
        Completed = null; // single-fire: OnClosing must not report a second time
        handler?.Invoke(proceed);
        Close();
    }

    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri)
        {
            UseShellExecute = true,
        });
        e.Handled = true;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _downloadCancel?.Cancel();
        // Real close (not hide): the wizard is cheap to recreate. A raw close (X) that skipped
        // Finish still reports once — proceed only if EAPO actually ended up usable.
        var handler = Completed;
        Completed = null;
        handler?.Invoke(EapoDetection.Detect() == EapoStatus.Active);
        base.OnClosing(e);
    }
}
