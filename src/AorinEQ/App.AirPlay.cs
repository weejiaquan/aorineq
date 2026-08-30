using System.Windows;
using AorinEQ.Core;
using AorinEQ.Input;
using AorinEQ.Core.Raop;
using AorinEQ.UI;

namespace AorinEQ;

/// <summary>App's ownership of the AirPlay stream.
///
/// Split into its own partial for the same reason the Settings page was: App.xaml.cs was three
/// thousand lines before this feature, and one self-contained concern is easier to read whole.
///
/// App owns the controller because the stream outlives the Settings window — you start it,
/// close Settings, and it keeps playing. The window and the tray are both views onto it.</summary>
public partial class App
{
    private AirPlayController? _airPlay;

    /// <summary>Discovery is slow (a few seconds of listening) and the result is reused by both
    /// the Settings page and the tray menu, so the last scan is kept.</summary>
    private IReadOnlyList<AirPlayDevice> _airPlayDevices = [];

    private AirPlayController AirPlay => _airPlay ??= CreateAirPlayController();

    private AirPlayController CreateAirPlayController()
    {
        var controller = new AirPlayController();
        controller.StateChanged += OnAirPlayStateChanged;
        return controller;
    }

    private void WireAirPlay(SettingsWindow window)
    {
        window.AirPlayRefreshRequested += () => _ = RefreshAirPlayDevicesAsync(window);
        window.AirPlayConnectRequested += device => ConnectAirPlay(device);
        window.AirPlayDisconnectRequested += DisconnectAirPlay;
        window.AirPlaySettingsChanged += OnAirPlaySettingsChanged;
        window.AirPlayPollRequested += () =>
            window.SetAirPlayState(AirPlay.Snapshot(), AirPlay.IsStreaming);

        window.SetAirPlayVolumeMode(_settings.VolumeMode);
        if (_airPlayDevices.Count > 0) window.SetAirPlayDevices(_airPlayDevices);
    }

    private async Task RefreshAirPlayDevicesAsync(SettingsWindow window)
    {
        var devices = await MdnsBrowser.DiscoverAsync(TimeSpan.FromSeconds(3));
        // Both the Settings page and the tray's NotifyIcon are affine to the UI thread. The
        // continuation usually lands there already (this is raised from a UI event), but that
        // depends on the caller's synchronisation context, and a tray menu rebuilt from a
        // thread-pool thread is the kind of bug that only shows up on someone else's machine.
        Dispatcher.Invoke(() =>
        {
            _airPlayDevices = devices;
            window.SetAirPlayDevices(devices);
            _tray?.SetAirPlayDevices(devices, AirPlay.Current?.Id);
        });
    }

    /// <summary>The ONE place a receiver is connected, from every entry point there is - the OSD
    /// strip's menu, the tray's menu and the Connect button on the settings page.
    ///
    /// It arms <see cref="AirPlaySetting.Enabled"/> because connecting IS switching AirPlay on.
    /// That was previously done only on the OSD strip's path, which made the feature unreachable:
    /// the strip is hidden while Enabled is false, so the only control that could set it was one
    /// the setting itself kept off screen.</summary>
    private bool ConnectAirPlay(AirPlayDevice device)
    {
        var setting = _settings.AirPlay ?? AirPlaySetting.Default;
        bool started = AirPlay.Start(device, setting);

        // What connecting does to settings is AirPlaySetting.Connecting's rule, not this method's:
        // three entry points reach here, and writing it out at each is how one of them ended up
        // being the only one that armed Enabled.
        _settings = _settings with
        {
            AirPlay = setting.Connecting(device.Id, device.DisplayName),
        };
        SaveSettings();
        return started;
    }

    private void DisconnectAirPlay() => _airPlay?.Stop();

    private void OnAirPlaySettingsChanged(AirPlaySetting setting)
    {
        _settings = _settings with { AirPlay = setting };
        SaveSettings();

        // The receiver's own level applies immediately; the queue depth and source only take
        // effect on the next connection, because changing them mid-stream would mean tearing
        // the session down and rebuilding it under the user.
        AirPlay.SetVolumePercent(setting.VolumePercent);
    }

    /// <summary>What the AirPlay strip should be showing right now. One answer, asked by both the
    /// show path and the live-refresh path, so the two can never disagree.</summary>
    private AirPlayBarState CurrentAirPlayBarState() =>
        AirPlayBarState.From(
            _settings.AirPlay,
            connected: _airPlay?.IsConnected == true,
            streaming: _airPlay?.IsStreaming == true);

    /// <summary>The receiver named in SETTINGS, which is not the one we hold a session with -
    /// that is the whole point of offering an explicit Connect in the menu.</summary>
    private string? ChosenAirPlayId() =>
        (_settings.AirPlay ?? AirPlaySetting.Default).DeviceId is { Length: > 0 } id ? id : null;

    /// <summary>Repaints the OSD's AirPlay strip if it is currently on screen.
    ///
    /// ShowOsdLevel is the only other place the strip is told anything, and it runs on a volume
    /// event - so connecting or disconnecting FROM the strip's own menu left the name and the
    /// connected state stale until the user happened to press a volume key.</summary>
    private void RefreshAirPlayOsd()
    {
        if (_useSkinOsd && _skinOsd is { IsVisible: true })
        {
            _skinOsd.SetAirPlay(CurrentAirPlayBarState());
            _skinOsd.SetAirPlayDevices(_airPlayDevices, AirPlay.Current?.Id, ChosenAirPlayId());
        }
        else if (_osd is { IsVisible: true })
        {
            _osd.SetAirPlay(CurrentAirPlayBarState(), skin: null, scale: 1.0);
            _osd.SetAirPlayDevices(_airPlayDevices, AirPlay.Current?.Id, ChosenAirPlayId());
        }
    }

    private void OnAirPlayStateChanged()
    {
        RefreshAirPlayOsd();
        // Raised from a background thread — see AirPlayController.StateChanged.
        Dispatcher.BeginInvoke(() =>
        {
            _tray?.SetAirPlayStreaming(AirPlay.IsStreaming, AirPlay.Current?.Id);
            if (_settingsWindow is { IsVisible: true } window)
                window.SetAirPlayState(AirPlay.Snapshot(), AirPlay.IsStreaming);
        });
    }

    /// <summary>Whether a volume key press should drive the AirPlay receiver instead of Windows.
    ///
    /// False in Equalizer APO mode however the setting is left: the preamp already sits upstream
    /// of the loopback tap, so the key has attenuated the stream before it is packetised and
    /// retargeting would attenuate it twice.</summary>
    private bool AirPlayOwnsVolume() =>
        AirPlayController.ShouldRetargetVolume(
            _settings.VolumeMode,
            (_settings.AirPlay ?? AirPlaySetting.Default).AutoRetargetVolume,
            _airPlay?.IsStreaming == true);

    /// <summary>Applies a volume step to the receiver. Returns false when AirPlay does not own
    /// the volume, so the caller falls through to its normal path.</summary>
    private bool TryApplyAirPlayVolume(int deltaPercent)
    {
        if (!AirPlayOwnsVolume() || _airPlay is null) return false;
        int target = Math.Clamp(_airPlay.VolumePercent + deltaPercent, 0, 100);
        _airPlay.SetVolumePercent(target);
        _settings = _settings with
        {
            AirPlay = (_settings.AirPlay ?? AirPlaySetting.Default) with { VolumePercent = target },
        };
        SaveSettings();
        ShowAirPlayOsd(target);
        return true;
    }

    /// <summary>The OSD still appears for an AirPlay volume change — the point of this app is
    /// that the volume keys give feedback, and that should not stop being true because the
    /// audio is going somewhere else.</summary>
    private void ShowAirPlayOsd(int percent) =>
        ShowOsdLevel(percent, muted: percent == 0, interactive: false);

    /// <summary>Points an OSD's AirPlay strip at the same handlers the tray menu already uses.
    ///
    /// Both OSD windows expose the same four events on purpose: connecting from the strip and
    /// connecting from the tray are the same act, and giving them separate paths is how the two
    /// would end up disagreeing about what "connected" means. Written as one method taking the
    /// pieces rather than twice, because SkinOsdWindow and OsdWindow share no base type - they are
    /// different windows that happen to offer the same vocabulary.</summary>
    private void WireAirPlayOsd(OsdWindow osd)
    {
        osd.AirPlayDeviceChosen += device => ConnectAirPlay(device);
        osd.AirPlayConnectChosenRequested += () => _ = ConnectChosenAirPlayAsync();
        osd.AirPlayDisconnectRequested += DisconnectAirPlay;
        osd.AirPlayRescanRequested += () => _ = RescanAirPlayAsync();
        osd.AirPlayVolumeSetByUser += SetAirPlayVolumeFromOsd;
        osd.AirPlayVolumeScrolled += OnAirPlayWheel;
    }

    private void WireAirPlayOsd(SkinOsdWindow osd)
    {
        osd.AirPlayDeviceChosen += device => ConnectAirPlay(device);
        osd.AirPlayConnectChosenRequested += () => _ = ConnectChosenAirPlayAsync();
        osd.AirPlayDisconnectRequested += DisconnectAirPlay;
        osd.AirPlayRescanRequested += () => _ = RescanAirPlayAsync();
        osd.AirPlayVolumeSetByUser += SetAirPlayVolumeFromOsd;
        osd.AirPlayVolumeScrolled += OnAirPlayWheel;
    }

    /// <summary>A wheel notch over the AirPlay strip, through the SAME accumulator the volume bar
    /// uses. A high-resolution wheel sends many small deltas per detent; treating each as a whole
    /// step is the overshoot bug the volume bar already fixed once, and the strip must not
    /// reintroduce it.</summary>
    private void OnAirPlayWheel(WheelNotch notch)
    {
        int step = ScrollStep.StepFor(notch.Ctrl, notch.Shift, _settings.StepPercent);
        int delta = _airPlayScroll.Feed(notch.RawDelta, step, _settings.ScrollInverted);
        if (delta == 0) return;

        var current = (_settings.AirPlay ?? AirPlaySetting.Default).VolumePercent;
        SetAirPlayVolumeFromOsd(current + delta);
    }

    /// <summary>Its own accumulator, not the volume bar's. They are two different volumes and a
    /// partial notch carried from one must never land on the other.</summary>
    private readonly ScrollStep _airPlayScroll = new();

    /// <summary>The RECEIVER's level, set by dragging or scrolling the strip. Deliberately not
    /// routed through TryApplyAirPlayVolume: that one exists for the volume KEYS and declines in
    /// Equalizer APO mode, because the preamp already attenuated upstream. This is a direct
    /// request to change the speaker, so it applies in every mode.</summary>
    private void SetAirPlayVolumeFromOsd(int percent)
    {
        int target = Math.Clamp(percent, 0, 100);
        _airPlay?.SetVolumePercent(target);
        _settings = _settings with
        {
            AirPlay = (_settings.AirPlay ?? AirPlaySetting.Default) with { VolumePercent = target },
        };
        SaveSettings();

        // NOT ShowAirPlayOsd. That one exists for a retargeted volume KEY, where the level being
        // shown IS the receiver's - here the volume bar must go on showing the system volume,
        // which in Equalizer APO mode is a completely different number. Re-showing at the active
        // level refreshes the strip underneath it and holds the OSD open through the drag.
        ShowOsd(interactive: false);
    }

    private void WireAirPlayTray()
    {
        if (_tray is null) return;
        _tray.AirPlayDeviceChosen += id =>
        {
            var device = _airPlayDevices.FirstOrDefault(d => d.Id == id);
            if (device is not null) ConnectAirPlay(device);
        };
        _tray.AirPlayDisconnectRequested += DisconnectAirPlay;
        _tray.AirPlayRefreshRequested += () => _ = RescanAirPlayAsync();
    }

    /// <summary>A discovery pass on behalf of the tray AND the OSD strip - both offer Rescan, and
    /// it is one act.
    ///
    /// It feeds the OSD as well as the tray. It used to update only the tray, so pressing Rescan
    /// on the strip's own menu left that menu's list exactly as empty as it had been: the OSD
    /// windows keep their own copy, refreshed on show, so the result did not arrive until the user
    /// happened to press a volume key.</summary>
    private async Task RescanAirPlayAsync()
    {
        if (_airPlayScanning) return;
        _airPlayScanning = true;
        try
        {
            var devices = await MdnsBrowser.DiscoverAsync(TimeSpan.FromSeconds(3));

            // Both the tray's NotifyIcon and the OSD windows are affine to the UI thread, and the
            // continuation's context depends on the caller - see RefreshAirPlayDevicesAsync.
            Dispatcher.Invoke(() =>
            {
                _airPlayDevices = devices;
                _tray?.SetAirPlayDevices(devices, AirPlay.Current?.Id);
                RefreshAirPlayOsd();
            });
        }
        finally
        {
            _airPlayScanning = false;
        }
    }

    /// <summary>Guards against stacking discovery passes. The strip can be shown, and Rescan
    /// pressed, several times inside one three-second scan.</summary>
    private bool _airPlayScanning;

    /// <summary>Discovery on behalf of the OSD, once, when the strip appears with nothing found.
    ///
    /// Without it the first thing the strip's menu ever says is "No receivers found", because
    /// nothing looks for a receiver until the user asks - and the menu is where they would ask.
    /// Only when the list is EMPTY: a strip shown twenty times in a minute must not mean twenty
    /// mDNS sweeps.</summary>
    private void EnsureAirPlayDevicesDiscovered()
    {
        if (_airPlayDevices.Count > 0 || _airPlayScanning) return;
        if (!(_settings.AirPlay ?? AirPlaySetting.Default).Enabled) return;
        _ = RescanAirPlayAsync();
    }

    /// <summary>Connect to the receiver the settings already name, finding it first if discovery
    /// has not run yet.
    ///
    /// This is what makes Connect offerable on a menu opened before anything has been discovered.
    /// The id is an mDNS instance name, not an address, so a session still needs the receiver
    /// resolved - but the USER has already chosen it, and "the thing you picked is not on this
    /// menu until you press Rescan and open it again" is not a choice worth making them repeat.</summary>
    private async Task ConnectChosenAirPlayAsync()
    {
        if (ChosenAirPlayId() is not { } chosenId) return;

        var device = _airPlayDevices.FirstOrDefault(d => d.Id == chosenId);
        if (device is null)
        {
            var devices = await MdnsBrowser.DiscoverAsync(TimeSpan.FromSeconds(3));
            Dispatcher.Invoke(() =>
            {
                _airPlayDevices = devices;
                _tray?.SetAirPlayDevices(devices, AirPlay.Current?.Id);
            });
            device = devices.FirstOrDefault(d => d.Id == chosenId);
        }

        Dispatcher.Invoke(() =>
        {
            if (device is null)
            {
                // Said out loud rather than silently doing nothing: the receiver being off, asleep
                // or on another network is the ordinary reason, and the user can act on it.
                var name = (_settings.AirPlay ?? AirPlaySetting.Default).DeviceName;
                _tray?.ShowWarning(Loc.T("app.airplay-not-found", name));
                return;
            }
            ConnectAirPlay(device);
            RefreshAirPlayOsd();
        });
    }

    private void DisposeAirPlay()
    {
        _airPlay?.Dispose();
        _airPlay = null;
    }
}
