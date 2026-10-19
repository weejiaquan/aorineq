using System.IO;
using System.Net.Http;
using AorinEQ.Core;

namespace AorinEQ;

/// <summary>App's side of VB-Cable: the install flow the AirPlay page starts, and the
/// "restart Windows to finish" state it leaves behind. Its own partial for the same reason
/// App.AirPlay is — one self-contained concern, readable whole.</summary>
public partial class App
{
    /// <summary>One start's step of a pending install (<see cref="VirtualCableInstaller.ResolvePendingAtStart"/>):
    /// announce a cable that has appeared since the restart, keep waiting, or — after
    /// <see cref="VirtualCableInstaller.PendingStartLimit"/> starts without one — give up so the
    /// install card shows again. Writes settings only when the step changed them.</summary>
    private void ResolveVirtualCablePending()
    {
        var (next, announceReady) = VirtualCableInstaller.ResolvePendingAtStart(
            _settings, VirtualCable.Find() is not null);
        if (next == _settings) return;
        _settings = next;
        SaveSettings();
        if (announceReady)
            _tray?.ShowActionableInfo(Loc.T("app.vbcable.ready"),
                () => _ = OpenSettingsAsync(SettingsSections.AirPlay));
    }

    /// <summary>Everything the AirPlay page shows about VB-Cable, read from the machine now. Stands
    /// down while an install runs: the activation that follows VB-Audio's UAC prompt must not wipe
    /// the install's progress line or re-enable its button; the install pushes once it ends.</summary>
    private void PushVirtualCableState(UI.SettingsWindow window)
    {
        if (_virtualCableInstalling) return;
        var cable = VirtualCable.Find();
        window.SetVirtualCable(
            VirtualCable.StateFor(cable is not null, VirtualCable.IsInstalledButInactive(),
                _settings.VirtualCablePendingReboot),
            cable?.Id);
        // While the cable's own repair runs, the toggle is showing the user's request, not the
        // machine: the UAC prompt deactivates and reactivates the window, and that activation must
        // not flip it back. Any other run (the default device's) leaves it showing the machine, so a
        // request dropped because of that run snaps straight back.
        if (cable is null || !RepairRunningOn(_eapoRepairRunning, _eapoRepairEndpointGuid, cable.Guid))
            window.SetAirPlayEqStream(cable is not null && EapoDetection.IsActiveOnEndpoint(cable.Guid));
        window.SetAirPlayVolumeMode(_settings.VolumeMode, EapoActiveOnAirPlaySource());
    }

    /// <summary>"EQ the AirPlay stream": the same elevated, backed-up, verified repair as the Volume
    /// page's, aimed at VB-Cable with <c>--endpoint</c>; off is its undo. No Equalizer APO yet means
    /// the setup guide instead. The toggle's state is re-read from the machine afterwards, whatever
    /// happened.</summary>
    private async Task SetAirPlayEqStreamAsync(bool on)
    {
        if (VirtualCable.Find() is { } cable)
        {
            if (on && EapoDetection.GetInstallPath() is null)
                OpenOnboarding();
            else
                await RunEapoRepairHelperAsync(on ? "--repair-eapo" : "--undo-eapo-repair",
                    on ? Loc.T("app.repairing") : Loc.T("app.undoing"), cable.Guid);
        }
        if (_settingsWindow is { } window) PushVirtualCableState(window);
    }

    private void OpenEqEditorOnAirPlayCable()
    {
        if (VirtualCable.Find() is not { } cable) return;
        OpenEqEditor();
        _eqEditor?.SelectScope(cable.Id);
    }

    /// <summary>Guards against stacking installs: the button is disabled while one runs, but the
    /// window can be closed and reopened mid-flow.</summary>
    private bool _virtualCableInstalling;

    /// <summary>The guided install: find VB-Audio's newest package, download it through every gate,
    /// extract it flat, verify the setup is VB-Audio's, run VB-Audio's own installer elevated, then
    /// look for the cable for ten seconds. Every failure finding or fetching the package — no temp
    /// folder, no network, a 404, a gate — sends the user to VB-Audio's page instead. The work folder
    /// is created INSIDE the try, so the running flag always resets and the folder is deleted
    /// whatever happens. The follow-up question (AirPlay only, or restart) is asked only after the
    /// folder is gone, so VB-Audio's files do not sit in %TEMP% for as long as a dialog stays open.</summary>
    private async Task InstallVirtualCableAsync()
    {
        if (_virtualCableInstalling) return;
        _virtualCableInstalling = true;
        string? work = null;
        string? verdict = null;
        bool setupRan = false;
        bool cableFound = false;
        try
        {
            string zip;
            try
            {
                work = VirtualCableInstaller.NewWorkFolder();
                zip = Path.Combine(work, "pack.zip");
                _settingsWindow?.SetVirtualCableProgress(Loc.T("app.vbcable.finding"));
                var url = await VirtualCableInstaller.ResolvePackageUrlAsync();
                _settingsWindow?.SetVirtualCableProgress(Loc.T("app.vbcable.downloading"));
                await VirtualCableInstaller.DownloadPackageAsync(url, zip);
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException
                or IOException or TaskCanceledException or UnauthorizedAccessException)
            {
                // Spec §2: any failure finding or fetching the package goes to VB-Audio's own page.
                // The flow passes no cancellation token, so a TaskCanceledException is a timeout,
                // never the user backing out. The last two are also the temp folder failing to appear.
                System.Diagnostics.Trace.TraceWarning($"VB-Cable install: couldn't fetch the package: {ex.Message}");
                OpenUrl(VirtualCable.VendorPage);
                verdict = Loc.T("app.vbcable.download-failed");
                return;
            }

            _settingsWindow?.SetVirtualCableProgress(Loc.T("app.vbcable.verifying"));
            if (VirtualCableInstaller.PrepareSetup(zip, work, Environment.Is64BitOperatingSystem) is not { } setup)
            {
                verdict = Loc.T("app.vbcable.not-verified");
                return;
            }

            _settingsWindow?.SetVirtualCableProgress(Loc.T("app.vbcable.installing"));
            switch (await RunVirtualCableSetupAsync(setup, work))
            {
                case VirtualCableLaunch.Cancelled:
                    verdict = Loc.T("app.vbcable.cancelled");
                    return;
                case VirtualCableLaunch.Failed:
                    OpenUrl(VirtualCable.VendorPage);
                    verdict = Loc.T("app.vbcable.setup-failed");
                    return;
            }
            setupRan = true;

            _settingsWindow?.SetVirtualCableProgress(Loc.T("app.vbcable.detecting"));
            cableFound = await VirtualCableInstaller.WaitForCableAsync(VirtualCableInstaller.DetectTimeout) is not null;
            if (cableFound)
            {
                verdict = Loc.T("app.vbcable.installed");
            }
            else
            {
                // The card turns to "Restart Windows to finish"; the next starts resolve it.
                _settings = _settings with { VirtualCablePendingReboot = true, VirtualCablePendingStarts = 0 };
                SaveSettings();
            }
        }
        finally
        {
            if (work is not null) VirtualCableInstaller.DeleteWorkFolder(work);
            _virtualCableInstalling = false;
            if (_settingsWindow is { } window)
            {
                PushVirtualCableState(window);
                if (verdict is not null) window.SetVirtualCableVerdict(verdict);
            }
        }

        if (!setupRan) return;
        if (cableFound) OfferAirPlayOnly();
        else ConfirmRestartForVirtualCable();
    }

    /// <summary>Runs VB-Audio's setup elevated from the extract folder (it installs the .inf/.sys/
    /// .cat beside it) and waits for it. Takes ownership of <paramref name="setup"/>, the verified
    /// file held open by <see cref="VirtualCableInstaller.PrepareSetup"/>: it stays open until
    /// <c>Process.Start</c> has returned, so the file launched is the file verified, and is closed
    /// straight after, so the work folder can be deleted. A declined UAC prompt is Cancelled, not a
    /// failure.</summary>
    private static async Task<VirtualCableLaunch> RunVirtualCableSetupAsync(FileStream setup, string workFolder)
    {
        System.Diagnostics.Process? process;
        try
        {
            process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(setup.Name)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = workFolder,
            });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return VirtualCableInstaller.LaunchOutcome(ex.NativeErrorCode);
        }
        finally
        {
            setup.Dispose();
        }
        if (process is null) return VirtualCableLaunch.Failed;
        using (process)
            await process.WaitForExitAsync();
        return VirtualCableLaunch.Ran;
    }

    /// <summary>The cable is here: offer the thing it was installed for. A yes goes through the same
    /// path as the user flipping the switch, and the page is told so it shows it.</summary>
    private void OfferAirPlayOnly()
    {
        if (System.Windows.MessageBox.Show(Loc.T("app.vbcable.offer-airplay-only"), "AorinEQ",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question)
            != System.Windows.MessageBoxResult.Yes)
            return;
        var updated = (_settings.AirPlay ?? AirPlaySetting.Default) with { AirPlayOnly = true };
        OnAirPlaySettingsChanged(updated);
        _settingsWindow?.SetAirPlaySetting(updated);
    }

    /// <summary>Restart now / Later. "Later" leaves the pending card up; the next start resolves it.</summary>
    private void ConfirmRestartForVirtualCable()
    {
        if (System.Windows.MessageBox.Show(Loc.T("app.vbcable.restart-confirm"), "AorinEQ",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question)
            != System.Windows.MessageBoxResult.Yes)
            return;
        _settingsSaver.Flush();
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "/r /t 0")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
