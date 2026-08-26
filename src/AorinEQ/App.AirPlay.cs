using System.Windows;
using AorinEQ.Core;
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
        _airPlayDevices = devices;
        // Discovery completes on a thread-pool thread; the window is WPF.
        window.Dispatcher.Invoke(() => window.SetAirPlayDevices(devices));
        _tray?.SetAirPlayDevices(devices, AirPlay.Current?.Id);
    }

    private bool ConnectAirPlay(AirPlayDevice device)
    {
        var setting = _settings.AirPlay ?? AirPlaySetting.Default;
        bool started = AirPlay.Start(device, setting.SourceEndpointId, setting.Mode,
            setting.CustomQueueMs);

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

    private void OnAirPlayStateChanged()
    {
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
        _airPlayDevices = devices;
        Dispatcher.BeginInvoke(() => _tray?.SetAirPlayDevices(devices, AirPlay.Current?.Id));
    }

    private void DisposeAirPlay()
    {
        _airPlay?.Dispose();
        _airPlay = null;
    }
}
