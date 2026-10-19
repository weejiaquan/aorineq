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
}
