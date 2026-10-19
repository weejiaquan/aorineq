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

    /// <summary>Everything the AirPlay page shows about VB-Cable, read from the machine now.</summary>
    private void PushVirtualCableState(UI.SettingsWindow window)
    {
        var cable = VirtualCable.Find();
        window.SetVirtualCable(
            VirtualCable.StateFor(cable is not null, VirtualCable.IsInstalledButInactive(),
                _settings.VirtualCablePendingReboot),
            cable?.Id);
        // While the helper runs, the toggle is showing the user's request, not the machine: the UAC
        // prompt deactivates and reactivates the window, and that activation must not flip it back.
        if (!_eapoRepairRunning)
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
}
