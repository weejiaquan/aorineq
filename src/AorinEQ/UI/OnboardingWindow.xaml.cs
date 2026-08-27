using System.IO;
using System.Windows;
using System.Windows.Navigation;
using AorinEQ.Core;

namespace AorinEQ.UI;

/// <summary>Setup wizard. Two modes: blocking (first run, or startup found eapo mode without
/// EAPO — closing without success exits the app) and informational ("Setup guide…" from
/// Settings / --onboarding: shows current state, closing just closes). An optional volume-mode
/// page leads (first run and the setup guide): choosing "Replace Windows volume" finishes
/// immediately (EAPO not required), choosing the APO preamp flows into the one state machine:
/// Explain → Downloading → InstallerRunning → re-detect → (Retry | Configurator | Success);
/// Success offers the audio-service restart that substitutes for a reboot.</summary>
public partial class OnboardingWindow : Wpf.Ui.Controls.FluentWindow
{
    private enum Step
    {
        ModeChoice,    // how volume keys control loudness (system vs eapo)
        Explain,
        Downloading,
        InstallerRunning,
        NeedsDevice,   // installed, but not enabled on the default playback device
        Success,
    }

    private readonly bool _blocking;
    private readonly bool _startedOnModeChoice;
    private Step _step;
    private string? _selectedMode; // the mode the user confirmed on the mode page, if any
    private System.Threading.CancellationTokenSource? _downloadCancel;

    /// <summary>Raised when the wizard is done: true → the chosen setup is usable, continue
    /// startup; false → user chose to exit (blocking mode only).</summary>
    public event Action<bool>? Completed;

    /// <summary>Raised when the user confirms a choice on the volume-mode page ("eapo" or
    /// "system"), before any install flow runs. The owner persists/applies it.</summary>
    public event Action<string>? ModeSelected;

    /// <summary>Raised alongside <see cref="ModeSelected"/> with the auto-update checkbox state
    /// confirmed on the mode page. The owner persists/applies it.</summary>
    public event Action<bool>? AutoUpdateSelected;

    /// <summary>A non-null <paramref name="modeChoice"/> starts the wizard on the volume-mode
    /// page with that mode preselected; null keeps the classic EAPO-install-only flow.
    /// <paramref name="autoUpdate"/> preselects the update checkbox (first run: the default-on
    /// spec value; setup guide: the current setting).</summary>
    public OnboardingWindow(bool blocking, string? modeChoice = null, bool autoUpdate = true)
    {
        _blocking = blocking;
        _startedOnModeChoice = modeChoice is not null;
        InitializeComponent();
        AutoUpdateBox.IsChecked = autoUpdate;
        if (modeChoice is not null)
            ShowModeChoice(modeChoice);
        else
            EnterStateForCurrentDetection(initial: true);
    }

    private void ShowModeChoice(string preselect)
    {
        SystemModeRadio.IsChecked = preselect != VolumeModes.Eapo;
        EapoModeRadio.IsChecked = preselect == VolumeModes.Eapo;
        Show(Step.ModeChoice,
            heading: Loc.T("onboarding.how-should-volume-keys-control-loudness"),
            body: Loc.T("onboarding.aorineq-swallows-the-volume-keys-and"),
            primary: "Continue",
            secondary: _blocking ? "Exit AorinEQ" : "Close");
        ModePanel.Visibility = Visibility.Visible;
    }

    /// <summary>Picks the wizard step matching live detection — the entry point and every
    /// re-detect go through here so the UI can never disagree with reality.</summary>
    private void EnterStateForCurrentDetection(bool initial = false)
    {
        switch (EapoDetection.Detect())
        {
            case EapoStatus.NotInstalled:
                Show(Step.Explain,
                    heading: Loc.T("onboarding.set-up-equalizer-apo"),
                    body: Loc.T("onboarding.aorineq-changes-your-volume-through-equalizer"),
                    primary: Loc.T("onboarding.download-and-install"),
                    secondary: _blocking ? "Exit AorinEQ" : "Close");
                break;
            case EapoStatus.InstalledInactive:
                Show(Step.NeedsDevice,
                    heading: Loc.T("onboarding.almost-there-pick-your-playback-device"),
                    body: Loc.T("onboarding.equalizer-apo-is-installed-but-it"),
                    primary: Loc.T("onboarding.open-configurator"),
                    secondary: _blocking ? "Exit AorinEQ" : "Close");
                break;
            case EapoStatus.Active:
                Show(Step.Success,
                    heading: initial && !_blocking ? Loc.T("onboarding.everything-is-set-up") : Loc.T("onboarding.equalizer-apo-is-ready"),
                    body: Loc.T("onboarding.equalizer-apo-is-installed-and-enabled")
                        + (initial ? "" : Loc.T("onboarding.if-volume-changes-aren-t-audible")),
                    primary: _blocking ? "Start AorinEQ" : "Close",
                    secondary: initial ? null : Loc.T("onboarding.restart-audio-now"));
                break;
        }
    }

    private void Show(Step step, string heading, string body, string primary, string? secondary,
        string? guidance = null)
    {
        _step = step;
        HeadingText.Text = heading;
        BodyText.Text = body;
        ModePanel.Visibility = Visibility.Collapsed; // ShowModeChoice re-shows it after this
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
            case Step.ModeChoice:
            {
                var mode = EapoModeRadio.IsChecked == true ? VolumeModes.Eapo : VolumeModes.System;
                _selectedMode = mode;
                AutoUpdateSelected?.Invoke(AutoUpdateBox.IsChecked == true);
                ModeSelected?.Invoke(mode);
                if (mode == VolumeModes.System)
                    Finish(proceed: true); // Windows volume needs no EAPO — nothing left to set up
                else
                    EnterStateForCurrentDetection(); // Explain / NeedsDevice / Success, live
                break;
            }
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
            heading: Loc.T("onboarding.downloading-equalizer-apo"),
            body: Loc.T("onboarding.fetching-the-official-installer-from-sourceforge"),
            primary: "Downloading…",
            secondary: "Cancel");
        PrimaryButton.IsEnabled = false;
        DownloadProgress.IsIndeterminate = false;

        var dest = Path.Combine(Path.GetTempPath(), "EqualizerAPO-Setup.exe");
        _downloadCancel = new System.Threading.CancellationTokenSource();
        try
        {
            var url = await InstallerDownload.ResolveLatestUrlAsync(
                InstallerDownload.BestReleaseUrl, _downloadCancel.Token);
            await InstallerDownload.DownloadAsync(url, dest,
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
            ErrorText.Text = ex.Message + Loc.T("onboarding.you-can-also-install-manually-from");
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        finally
        {
            _downloadCancel = null;
        }

        Show(Step.InstallerRunning,
            heading: "Installer running…",
            body: Loc.T("onboarding.follow-the-equalizer-apo-installer"),
            primary: Loc.T("onboarding.waiting-for-the-installer"),
            secondary: null,
            guidance: Loc.T("onboarding.when-the-configurator-window-appears-tick"));
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
            ErrorText.Text = Loc.T("onboarding.the-installer-didn-t-complete-you");
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
            // Restart-Service -Force takes dependent services (Audiosrv depends on
            // AudioEndpointBuilder) down and up in the right order; Audiosrv is started
            // explicitly afterwards in case -Force left it stopped.
            var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "powershell.exe",
                Loc.T("onboarding.noprofile-executionpolicy-bypass-command-restart-service"))
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            });
            if (proc is null)
            {
                ErrorText.Text = Loc.T("onboarding.error.couldn-t-start-the");
                ErrorText.Visibility = Visibility.Visible;
                return;
            }
            await proc.WaitForExitAsync();
            if (proc.ExitCode == 0)
            {
                BodyText.Text = Loc.T("onboarding.body.audio-restarted-equalizer-apo");
            }
            else
            {
                ErrorText.Text = Loc.T("onboarding.the-audio-services-could-not-be")
                    + proc.ExitCode + Loc.T("onboarding.restart-your-pc-instead-to-finish");
                ErrorText.Visibility = Visibility.Visible;
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            ErrorText.Text = Loc.T("onboarding.error.elevation-was-declined-restart");
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
        // Finish still reports once — proceed if the confirmed choice was system mode (which
        // needs no EAPO), or if EAPO is usable AND no unanswered mode page is being skipped:
        // X-ing the first-run mode choice without confirming anything is a decline, even on a
        // machine where EAPO happens to be active.
        var handler = Completed;
        Completed = null;
        bool proceed = _selectedMode == VolumeModes.System
            || ((_selectedMode is not null || !_startedOnModeChoice)
                && EapoDetection.Detect() == EapoStatus.Active);
        handler?.Invoke(proceed);
        base.OnClosing(e);
    }
}
