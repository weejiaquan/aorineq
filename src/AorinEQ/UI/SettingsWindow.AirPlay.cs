using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AorinEQ.Core;
using AorinEQ.Core.Raop;

namespace AorinEQ.UI;

/// <summary>The AirPlay section.
///
/// Split into its own partial rather than added to SettingsWindow.xaml.cs, which was already
/// nine hundred lines before this feature. Same window, same namescope, same generated fields —
/// only the file boundary is new, and it keeps one self-contained concern readable.
///
/// The window stays a view: it raises what the user did and is told what to display. App owns
/// the <see cref="AirPlayController"/>, the discovery and the persistence, exactly as it owns
/// every other setting here.</summary>
public partial class SettingsWindow
{
    private IReadOnlyList<AirPlayDevice> _airPlayDevices = [];
    private IReadOnlyList<RenderEndpoint> _airPlayEndpoints = [];
    private AirPlaySetting _airPlay = AirPlaySetting.Default;
    private string _airPlayVolumeMode = VolumeModes.Eapo;
    private DispatcherTimer? _airPlayTimer;

    /// <summary>The user asked to rescan for receivers. App discovers and calls
    /// <see cref="SetAirPlayDevices"/>.</summary>
    public event Action? AirPlayRefreshRequested;

    /// <summary>Connect to the selected receiver, or disconnect when already streaming.</summary>
    public event Action<AirPlayDevice>? AirPlayConnectRequested;
    public event Action? AirPlayDisconnectRequested;

    /// <summary>Any AirPlay control changed; the whole block is sent so App merges one snapshot
    /// rather than a field at a time — the same shape as OsdSettingsChanged.</summary>
    public event Action<AirPlaySetting>? AirPlaySettingsChanged;

    private void ApplyAirPlay(Settings settings)
    {
        _airPlay = settings.AirPlay ?? AirPlaySetting.Default;
        _airPlayVolumeMode = settings.VolumeMode;

        PopulateAirPlaySources();
        SelectByTag(AirPlayModeCombo, _airPlay.Mode);
        SelectByTag(AirPlayBarCombo, AirPlayBarVisibility.Normalize(_airPlay.BarVisibility));
        AirPlayCustomMsBox.Text = _airPlay.CustomQueueMs.ToString();
        AirPlayCustomMsBox.IsEnabled = _airPlay.Mode == AirPlayModes.Custom;
        AirPlayVolumeSlider.Value = _airPlay.VolumePercent;
        AirPlayVolumeText.Text = $"{_airPlay.VolumePercent}%";
        AirPlayRetargetBox.IsChecked = _airPlay.AutoRetargetVolume;
        AirPlayMuteLocalBox.IsChecked = _airPlay.MuteLocalWhileStreaming;
        AirPlayDitherBox.IsChecked = _airPlay.DitheredSilence;
        AirPlayStandbyBox.IsChecked = _airPlay.StandbyEnabled;
        AirPlayIdleSecondsBox.Text = _airPlay.IdleDisconnectSeconds.ToString();
        AirPlayIdleSecondsBox.IsEnabled = !_airPlay.StandbyEnabled;

        ApplyRetargetAvailability();
        UpdateAirPlayButtons(streaming: false);
    }

    /// <summary>The AutoRetargetVolume switch is meaningless in Equalizer APO mode and says so
    /// rather than sitting there doing nothing.
    ///
    /// In that mode the preamp already sits upstream of the loopback tap, so the volume keys
    /// have attenuated the stream before it is packetised. Retargeting as well would apply the
    /// same attenuation twice.</summary>
    private void ApplyRetargetAvailability()
    {
        bool applies = AirPlayController.RetargetSettingApplies(_airPlayVolumeMode);
        AirPlayRetargetBox.IsEnabled = applies;
        AirPlayRetargetNote.Text = applies
            ? Loc.T("settings.airplay.while-streaming-the-volume-keys-set")
            : Loc.T("settings.airplay.not-needed-in-equalizer-apo-mode");
    }

    /// <summary>Called by App when the volume mode changes, so the note above stays honest
    /// without reopening the window.</summary>
    public void SetAirPlayVolumeMode(string volumeMode)
    {
        _airPlayVolumeMode = volumeMode;
        ApplyRetargetAvailability();
    }

    private void PopulateAirPlaySources()
    {
        _airPlayEndpoints = AudioEndpoint.GetRenderEndpoints();
        AirPlaySourceCombo.Items.Clear();
        AirPlaySourceCombo.Items.Add(new ComboBoxItem { Content = Loc.T("settings.airplay.default-playback-device"), Tag = "" });
        foreach (var endpoint in _airPlayEndpoints)
            AirPlaySourceCombo.Items.Add(new ComboBoxItem
            {
                Content = endpoint.FriendlyName,
                Tag = endpoint.Id,
            });
        SelectByTag(AirPlaySourceCombo, _airPlay.SourceEndpointId);
        // The saved endpoint may have been unplugged since. Fall back to "Default playback
        // device" rather than leaving the combo blank and the source ambiguous.
        if (AirPlaySourceCombo.SelectedIndex < 0) AirPlaySourceCombo.SelectedIndex = 0;
    }

    /// <summary>App hands over what discovery found.</summary>
    public void SetAirPlayDevices(IReadOnlyList<AirPlayDevice> devices)
    {
        _airPlayDevices = devices;
        AirPlayDeviceCombo.Items.Clear();
        foreach (var device in devices)
            AirPlayDeviceCombo.Items.Add(new ComboBoxItem
            {
                Content = device.DisplayName,
                Tag = device.Id,
            });

        AirPlayRefreshButton.IsEnabled = true;
        if (devices.Count == 0)
        {
            AirPlayStatusText.Text = Loc.T("settings.airplay.air-play-status.no-airplay-receivers-found");
            AirPlayConnectButton.IsEnabled = false;
            return;
        }

        SelectByTag(AirPlayDeviceCombo, _airPlay.DeviceId);
        if (AirPlayDeviceCombo.SelectedIndex < 0)
            AirPlayDeviceCombo.SelectedIndex = 0;
        AirPlayConnectButton.IsEnabled = true;
    }

    /// <summary>App pushes the live session state here on a timer while Settings is open.</summary>
    public void SetAirPlayState(RaopDiagnostics diagnostics, bool streaming)
    {
        AirPlayStatusText.Text = diagnostics.Summary;
        UpdateAirPlayButtons(streaming);

        AirPlayDiagnosticsText.Text = streaming
            ? string.Join(Environment.NewLine,
                $"state      {diagnostics.State}",
                $"receiver   {diagnostics.Receiver}  ({diagnostics.ServerName})",
                $"codec      {diagnostics.Codec}",
                $"format     {diagnostics.SampleRate} Hz, {diagnostics.FramesPerPacket} frames/packet",
                Loc.T("settings.airplay.queue-ms-receiver-reports-samples", diagnostics.QueueMs, diagnostics.ReceiverLatencySamples),
                $"sent       {diagnostics.PacketsSent:N0} packets, {diagnostics.BytesSent / 1024:N0} KiB, {diagnostics.Kbps:F0} kbps",
                Loc.T("settings.airplay.resends-requested-served-missed-of-packets", diagnostics.RetransmitRequests, diagnostics.RetransmitsServed, diagnostics.RetransmitsMissed, diagnostics.RetransmitPercent),
                $"timing     {diagnostics.TimingReplies:N0} replies, {diagnostics.SyncsSent:N0} syncs",
                Loc.T("settings.airplay.silence-packets-with-no-source-audio", diagnostics.SilentPackets))
            : diagnostics.LastError is { Length: > 0 } error
                ? $"Last attempt failed: {error}"
                : "Not streaming.";
    }

    private void UpdateAirPlayButtons(bool streaming)
    {
        AirPlayConnectButton.Content = streaming ? "Disconnect" : "Connect";
        AirPlayConnectButton.Icon = streaming
            ? new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Stop24)
            : new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Play24);
        AirPlayDeviceCombo.IsEnabled = !streaming;
        AirPlaySourceCombo.IsEnabled = !streaming;
    }

    /// <summary>Starts polling the session while the AirPlay page is visible. Stopped when the
    /// window closes or another section is shown — a tray app should not run a timer for a page
    /// nobody is looking at.</summary>
    private void StartAirPlayPolling()
    {
        _airPlayTimer ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _airPlayTimer.Tick -= OnAirPlayTick;
        _airPlayTimer.Tick += OnAirPlayTick;
        _airPlayTimer.Start();
    }

    private void StopAirPlayPolling() => _airPlayTimer?.Stop();

    private void OnAirPlayTick(object? sender, EventArgs e) => AirPlayPollRequested?.Invoke();

    /// <summary>Fires twice a second while the AirPlay page is open. App answers by calling
    /// <see cref="SetAirPlayState"/>.</summary>
    public event Action? AirPlayPollRequested;

    // ---- handlers -------------------------------------------------------------------------

    private void OnAirPlayRefresh(object sender, RoutedEventArgs e)
    {
        AirPlayRefreshButton.IsEnabled = false;
        AirPlayStatusText.Text = Loc.T("settings.airplay.air-play-status.searching");
        AirPlayRefreshRequested?.Invoke();
    }

    private void OnAirPlayConnect(object sender, RoutedEventArgs e)
    {
        if (AirPlayConnectButton.Content as string == "Disconnect")
        {
            AirPlayDisconnectRequested?.Invoke();
            return;
        }

        if (SelectedTag(AirPlayDeviceCombo) is not { Length: > 0 } id) return;
        var device = _airPlayDevices.FirstOrDefault(d => d.Id == id);
        if (device is null) return;

        AirPlayStatusText.Text = Loc.T("settings.airplay.air-play-status.connecting-to", device.DisplayName);
        AirPlayConnectRequested?.Invoke(device);
    }

    private void OnAirPlayDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (SelectedTag(AirPlayDeviceCombo) is not { } id) return;
        var device = _airPlayDevices.FirstOrDefault(d => d.Id == id);
        RaiseAirPlay(_airPlay with
        {
            DeviceId = id,
            DeviceName = device?.DisplayName ?? "",
        });
    }

    private void OnAirPlaySourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        RaiseAirPlay(_airPlay with { SourceEndpointId = SelectedTag(AirPlaySourceCombo) ?? "" });
    }

    private void OnAirPlayModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        string mode = AirPlayModes.Normalize(SelectedTag(AirPlayModeCombo), AirPlayModes.Normal);
        AirPlayCustomMsBox.IsEnabled = mode == AirPlayModes.Custom;
        RaiseAirPlay(_airPlay with { Mode = mode });
    }

    /// <summary>When the OSD draws its AirPlay strip. Normalised on the way out so an unknown
    /// tag - from a newer build, or a hand-edited settings file - lands on the default rather
    /// than silently removing a bar the user then cannot find the switch for.</summary>
    private void OnAirPlayBarVisibilityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        RaiseAirPlay(_airPlay with
        {
            BarVisibility = AirPlayBarVisibility.Normalize(SelectedTag(AirPlayBarCombo)),
        });
    }

    private void OnAirPlayCustomMsChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        if (!int.TryParse(AirPlayCustomMsBox.Text, out int ms))
        {
            AirPlayCustomMsBox.Text = _airPlay.CustomQueueMs.ToString();
            return;
        }
        ms = Math.Clamp(ms, AirPlayModes.MinQueueMs, AirPlayModes.MaxQueueMs);
        AirPlayCustomMsBox.Text = ms.ToString();
        RaiseAirPlay(_airPlay with { CustomQueueMs = ms });
    }

    private void OnAirPlayVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int percent = (int)Math.Round(e.NewValue);
        AirPlayVolumeText.Text = $"{percent}%";
        if (_initializing) return;
        RaiseAirPlay(_airPlay with { VolumePercent = percent });
    }

    private void OnAirPlayRetargetChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        RaiseAirPlay(_airPlay with { AutoRetargetVolume = AirPlayRetargetBox.IsChecked == true });
    }

    private void OnAirPlayMuteLocalChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        RaiseAirPlay(_airPlay with
        {
            MuteLocalWhileStreaming = AirPlayMuteLocalBox.IsChecked == true,
        });
    }

    private void OnAirPlayDitherChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        RaiseAirPlay(_airPlay with { DitheredSilence = AirPlayDitherBox.IsChecked == true });
    }

    private void OnAirPlayStandbyChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        bool standby = AirPlayStandbyBox.IsChecked == true;
        // The timeout box only means anything when standby is off - staying connected IS having
        // no timeout, so showing an editable number beside it would be a contradiction.
        AirPlayIdleSecondsBox.IsEnabled = !standby;
        RaiseAirPlay(_airPlay with { StandbyEnabled = standby });
    }

    private void OnAirPlayIdleSecondsChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        if (!int.TryParse(AirPlayIdleSecondsBox.Text, out int seconds))
        {
            AirPlayIdleSecondsBox.Text = _airPlay.IdleDisconnectSeconds.ToString();
            return;
        }
        seconds = Math.Clamp(seconds, AirPlayIdle.MinSeconds, AirPlayIdle.MaxSeconds);
        AirPlayIdleSecondsBox.Text = seconds.ToString();
        RaiseAirPlay(_airPlay with { IdleDisconnectSeconds = seconds });
    }

    /// <summary>Stops a closed ComboBox from eating the mouse wheel and changing its own value.
    ///
    /// WPF's default is that scrolling over a combo cycles its selection. On a settings page
    /// that means scrolling down the page silently changes whatever setting the pointer happens
    /// to pass over — found by doing exactly that and switching the playback mode by accident.
    /// The wheel is handed to the ScrollViewer instead, which is what the user meant.</summary>
    private void OnComboWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is System.Windows.Controls.ComboBox { IsDropDownOpen: true }) return;
        e.Handled = true;
        var bubbled = new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = sender,
        };
        (((System.Windows.FrameworkElement)sender).Parent as UIElement)?.RaiseEvent(bubbled);
    }

    private void RaiseAirPlay(AirPlaySetting updated)
    {
        _airPlay = updated;
        AirPlaySettingsChanged?.Invoke(updated);
    }

    // SelectByTag and SelectedTag already exist on the other partial; they are reused rather
    // than reimplemented here. Both leave nothing selected when the stored value is no longer
    // in the list, which is why the callers above fall back explicitly.
}
