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

    private bool ConnectAirPlay(AirPlayDevice device)
    {
        var setting = _settings.AirPlay ?? AirPlaySetting.Default;
        bool started = AirPlay.Start(device, setting);

        // Remember what was chosen even when the attempt failed: the user is more likely to
        // retry the same receiver than to want the selection reset.
        _settings = _settings with
        {
            AirPlay = setting with { DeviceId = device.Id, DeviceName = device.DisplayName },
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
        osd.AirPlayDeviceChosen += ConnectFromOsd;
        osd.AirPlayDisconnectRequested += DisconnectAirPlay;
        osd.AirPlayRescanRequested += () => _ = RefreshAirPlayForTrayAsync();
        osd.AirPlayVolumeSetByUser += SetAirPlayVolumeFromOsd;
        osd.AirPlayVolumeScrolled += OnAirPlayWheel;
    }

    private void WireAirPlayOsd(SkinOsdWindow osd)
    {
        osd.AirPlayDeviceChosen += ConnectFromOsd;
        osd.AirPlayDisconnectRequested += DisconnectAirPlay;
        osd.AirPlayRescanRequested += () => _ = RefreshAirPlayForTrayAsync();
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

    /// <summary>Connecting from the OSD also PERSISTS the choice, exactly as picking from the
    /// tray does - otherwise the strip would connect to a receiver the settings file still says
    /// is a different one, and the next start would disagree with what is playing.</summary>
    private void ConnectFromOsd(AirPlayDevice device)
    {
        _settings = _settings with
        {
            AirPlay = (_settings.AirPlay ?? AirPlaySetting.Default) with
            {
                Enabled = true,
                DeviceName = device.DisplayName,
                DeviceId = device.Id,
            },
        };
        SaveSettings();
        ConnectAirPlay(device);
    }

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
        _tray.AirPlayRefreshRequested += () => _ = RefreshAirPlayForTrayAsync();
    }

    private async Task RefreshAirPlayForTrayAsync()
    {
        var devices = await MdnsBrowser.DiscoverAsync(TimeSpan.FromSeconds(3));
        Dispatcher.Invoke(() =>
        {
            _airPlayDevices = devices;
            _tray?.SetAirPlayDevices(devices, AirPlay.Current?.Id);
        });
    }

    private void DisposeAirPlay()
    {
        _airPlay?.Dispose();
        _airPlay = null;
    }
}
