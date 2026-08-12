using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Navigation;
using ApoVolume.Core;

namespace ApoVolume.UI;

public partial class SettingsWindow : Window
{
    private const string DefaultAnchor = "bottom-center";

    private bool _initializing = true;

    private readonly (ToggleButton Button, string Anchor)[] _anchorButtons;

    public event Action<bool>? AutostartChanged;
    public event Action<bool>? RunAsAdminChanged;

    /// <summary>Raised when the "Enable apo-volume:// links" checkbox changes. App
    /// registers/unregisters the URL scheme and persists.</summary>
    public event Action<bool>? ProtocolLinksChanged;

    /// <summary>Raised when the auto-update checkbox changes. App persists and
    /// starts/stops the periodic check.</summary>
    public event Action<bool>? AutoUpdateChanged;

    /// <summary>Raised by the "Check now" button. App runs an interactive update check and
    /// reports back via <see cref="SetUpdateStatus"/>.</summary>
    public event Action? CheckUpdatesRequested;

    /// <summary>Raised with the chosen mode ("system" or "eapo") when the user switches the
    /// Volume control radios. App applies the transition live and persists.</summary>
    public event Action<string>? VolumeModeChanged;

    /// <summary>Raised whenever any OSD control changes (once fully initialized). App merges the
    /// snapshot into its persisted Settings — see <see cref="OsdSettings"/>'s remarks.</summary>
    public event Action<OsdSettings>? OsdSettingsChanged;

    /// <summary>Raised when the user clicks "Skin designer…" — App owns the designer window.</summary>
    public event Action? SkinDesignerRequested;

    /// <summary>Raised when the user clicks "Setup guide…" — App owns the onboarding window.</summary>
    public event Action? SetupGuideRequested;

    /// <summary>Raised when the user clicks "Open equalizer…" — App owns the EQ editor window.</summary>
    public event Action? EqualizerRequested;

    public SettingsWindow(bool autostartEnabled, bool runAsAdmin, bool isElevated, string version, Settings settings)
    {
        InitializeComponent();

        _anchorButtons = new (ToggleButton, string)[]
        {
            (AnchorTopLeft, "top-left"), (AnchorTopCenter, "top-center"), (AnchorTopRight, "top-right"),
            (AnchorLeftCenter, "left-center"), (AnchorRightCenter, "right-center"),
            (AnchorBottomLeft, "bottom-left"), (AnchorBottomCenter, "bottom-center"), (AnchorBottomRight, "bottom-right"),
        };

        AutostartBox.IsChecked = autostartEnabled;
        RunAsAdminBox.IsChecked = runAsAdmin;
        ElevationStateText.Text = isElevated
            ? "Currently running elevated."
            : runAsAdmin
                ? "Not elevated in this session — restart the app (or approve the prompt) to apply."
                : "Currently running without elevation.";
        VersionText.Text = "apo-volume " + version;
        ProtocolLinksBox.IsChecked = settings.ProtocolLinksEnabled;
        AutoUpdateBox.IsChecked = settings.AutoUpdate;

        ApplyOsdSettings(settings);
        ApplyVolumeMode(settings);
        PopulateSkins(settings.SkinName);
        RefreshEapoStatus();

        _initializing = false;
    }

    /// <summary>Live Equalizer APO status line + Configurator button state. Called at
    /// construction and on every SyncState (Settings reopen), so plugging in headphones or
    /// running the Configurator is reflected the next time the window is looked at.</summary>
    private void RefreshEapoStatus()
    {
        var status = EapoDetection.Detect();
        EapoStatusText.Text = status switch
        {
            EapoStatus.Active => "Active on the current playback device.",
            EapoStatus.InstalledInactive =>
                "Installed, but not enabled on the current playback device — volume changes won't be audible there.",
            _ => "Not installed.",
        };
        OpenConfiguratorButton.IsEnabled = EapoDetection.GetConfiguratorPath() is not null;
    }

    /// <summary>Launches EAPO's Configurator (elevated — it registers APOs on devices).</summary>
    private void OnOpenConfigurator(object sender, RoutedEventArgs e)
    {
        var path = EapoDetection.GetConfiguratorPath();
        if (path is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // UAC declined — nothing to do.
        }
    }

    private void OnOpenSetupGuide(object sender, RoutedEventArgs e) => SetupGuideRequested?.Invoke();

    private void OnOpenEqualizer(object sender, RoutedEventArgs e) => EqualizerRequested?.Invoke();

    /// <summary>Re-syncs every control (General tab and OSD tab alike) from current app state.
    /// Called both after autostart/RunAsAdmin changes and every time Settings is (re)opened —
    /// which is also when the skins folder gets rescanned.</summary>
    public void SyncState(bool autostartEnabled, bool runAsAdmin, bool isElevated, Settings settings)
    {
        _initializing = true;
        AutostartBox.IsChecked = autostartEnabled;
        RunAsAdminBox.IsChecked = runAsAdmin;
        ElevationStateText.Text = isElevated ? "Currently running elevated."
            : runAsAdmin ? "Not elevated in this session — restart the app (or approve the prompt) to apply."
            : "Currently running without elevation.";
        ProtocolLinksBox.IsChecked = settings.ProtocolLinksEnabled;
        AutoUpdateBox.IsChecked = settings.AutoUpdate;

        ApplyOsdSettings(settings);
        ApplyVolumeMode(settings);
        PopulateSkins(settings.SkinName);
        RefreshEapoStatus();

        _initializing = false;
    }

    /// <summary>Syncs the Volume control radios. Safe while _initializing (the guard is only
    /// checked when raising VolumeModeChanged).</summary>
    private void ApplyVolumeMode(Settings settings)
    {
        SystemModeRadio.IsChecked = settings.VolumeMode == VolumeModes.System;
        EapoModeRadio.IsChecked = settings.VolumeMode != VolumeModes.System;
    }

    /// <summary>Populates every OSD control from the given settings. Safe to call while
    /// _initializing is true (the guard is only checked when raising OsdSettingsChanged).</summary>
    private void ApplyOsdSettings(Settings settings)
    {
        SelectByTag(StyleCombo, settings.OsdStyle);
        SkinCombo.IsEnabled = settings.OsdStyle == OsdStyles.Skin;

        foreach (var (button, anchor) in _anchorButtons)
            button.IsChecked = anchor == settings.OsdAnchor;

        OffsetXBox.Text = settings.OsdOffsetX.ToString();
        OffsetYBox.Text = settings.OsdOffsetY.ToString();

        HideDelaySlider.Value = settings.HideDelaySeconds;
        HideDelayLabel.Text = FormatSeconds(settings.HideDelaySeconds);

        AnimationCheckBox.IsChecked = settings.AnimationEnabled;
        AnimationDurationSlider.Value = settings.AnimationMs;
        AnimationDurationLabel.Text = $"{settings.AnimationMs}ms";

        SelectByTag(StepCombo, settings.StepPercent.ToString());
    }

    /// <summary>Rescans the skins folder and repopulates SkinCombo, preserving the current
    /// selection by name when it's still present. Invalid skins stay in the list, shown disabled
    /// with their error as a tooltip, so a broken skin doesn't just silently disappear. The
    /// SelectionChanged handler is detached during the rebuild so clearing/repopulating the list
    /// can't emit a transient "no skin selected" change.</summary>
    private void PopulateSkins(string currentSkinName)
    {
        SkinCombo.SelectionChanged -= OnSkinChanged;
        try
        {
            SkinCombo.Items.Clear();
            foreach (var skin in SkinLoader.Scan(ApoPaths.GetSkinsRoot()))
            {
                var item = new ComboBoxItem { Content = skin.Name, Tag = skin.Name, IsEnabled = skin.IsValid };
                if (!skin.IsValid) item.ToolTip = skin.Error;
                SkinCombo.Items.Add(item);
            }
            SelectByTag(SkinCombo, currentSkinName);
        }
        finally
        {
            SkinCombo.SelectionChanged += OnSkinChanged;
        }
    }

    /// <summary>Selects the ComboBoxItem whose Tag matches, or leaves nothing selected if the
    /// configured value isn't present in the list (e.g. a skin folder that was removed).</summary>
    private static void SelectByTag(System.Windows.Controls.ComboBox combo, string tag)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if ((string)item.Tag == tag)
            {
                combo.SelectedItem = item;
                return;
            }
        }
        combo.SelectedIndex = -1;
    }

    private static string FormatSeconds(double seconds) => seconds.ToString("0.0") + "s";

    private static string? SelectedTag(System.Windows.Controls.ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string;

    private void OnAutostartChanged(object sender, RoutedEventArgs e)
    {
        if (!_initializing) AutostartChanged?.Invoke(AutostartBox.IsChecked == true);
    }

    private void OnRunAsAdminChanged(object sender, RoutedEventArgs e)
    {
        if (!_initializing) RunAsAdminChanged?.Invoke(RunAsAdminBox.IsChecked == true);
    }

    private void OnProtocolLinksChanged(object sender, RoutedEventArgs e)
    {
        if (!_initializing) ProtocolLinksChanged?.Invoke(ProtocolLinksBox.IsChecked == true);
    }

    private void OnAutoUpdateChanged(object sender, RoutedEventArgs e)
    {
        if (!_initializing) AutoUpdateChanged?.Invoke(AutoUpdateBox.IsChecked == true);
    }

    private void OnCheckUpdates(object sender, RoutedEventArgs e) => CheckUpdatesRequested?.Invoke();

    /// <summary>Updates the "current vX / latest vY" line under the Check now button. Called by
    /// App whenever a check starts or finishes (manual or background).</summary>
    public void SetUpdateStatus(string text) => UpdateStatusText.Text = text;

    // Checked-only (never Unchecked): each user switch fires exactly once, for the new radio.
    private void OnVolumeModeChanged(object sender, RoutedEventArgs e)
    {
        if (!_initializing)
            VolumeModeChanged?.Invoke(
                SystemModeRadio.IsChecked == true ? VolumeModes.System : VolumeModes.Eapo);
    }

    private void OnStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        SkinCombo.IsEnabled = SelectedTag(StyleCombo) == OsdStyles.Skin;
        RaiseOsdSettingsChanged();
    }

    private void OnSkinChanged(object sender, SelectionChangedEventArgs e) => RaiseOsdSettingsChanged();

    private void OnOpenSkinsFolder(object sender, RoutedEventArgs e)
    {
        var root = ApoPaths.GetSkinsRoot(); // creates the folder if missing
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(root)
        {
            UseShellExecute = true,
        });
    }

    private void OnOpenSkinDesigner(object sender, RoutedEventArgs e) => SkinDesignerRequested?.Invoke();

    /// <summary>Installs a skin shared as a zip, without needing the designer: name from the zip
    /// filename, overwrite confirmed, picker refreshed. If the ACTIVE skin was overwritten, the
    /// change event re-fires so App hot-reloads the live OSD (content-stamp rebuild).</summary>
    private void OnImportSkin(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Skin zip (*.zip)|*.zip",
            Title = "Import a shared skin",
        };
        if (dialog.ShowDialog(this) != true) return;

        var name = SkinArchive.DefaultName(dialog.FileName).Trim();
        try
        {
            if (SkinWriter.ValidateName(name) is { } nameError)
            {
                System.Windows.MessageBox.Show(
                    $"The zip filename can't be used as the skin name: {nameError}",
                    "apo-volume", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (Directory.Exists(Path.Combine(ApoPaths.GetSkinsRoot(), name)))
            {
                var choice = System.Windows.MessageBox.Show(
                    $"A skin named '{name}' already exists. Overwrite it?",
                    "apo-volume", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (choice != MessageBoxResult.Yes) return;
            }
            SkinArchive.Import(dialog.FileName, ApoPaths.GetSkinsRoot(), name);
            PopulateSkins(SelectedTag(SkinCombo) ?? "");
            if (SelectedTag(StyleCombo) == OsdStyles.Skin && SelectedTag(SkinCombo) == name)
                RaiseOsdSettingsChanged();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            System.Windows.MessageBox.Show(ex.Message, "apo-volume", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Rescans the skins folder preserving the current selection. Called by App after
    /// the skin designer saves, so the picker reflects new/renamed skins immediately.</summary>
    public void RefreshSkins() => PopulateSkins(SelectedTag(SkinCombo) ?? "");

    private void OnRescanSkins(object sender, RoutedEventArgs e)
    {
        PopulateSkins(SelectedTag(SkinCombo) ?? "");

        // If the currently-selected skin is now valid (e.g. the user just fixed the skin folder),
        // re-raise so App's ApplyOsdConfig retries loading it into the live OSD — fixing a skin
        // and clicking Rescan should just work, without having to reselect the same skin.
        if (SelectedTag(StyleCombo) == OsdStyles.Skin && SkinCombo.SelectedItem is ComboBoxItem { IsEnabled: true })
            RaiseOsdSettingsChanged();
    }

    /// <summary>Forces exactly one anchor ToggleButton checked: the one clicked. Uses Click
    /// (user-gesture only) rather than Checked/Unchecked so the programmatic IsChecked assignments
    /// in ApplyOsdSettings/here don't recurse.</summary>
    private void OnAnchorClicked(object sender, RoutedEventArgs e)
    {
        var clicked = (ToggleButton)sender;
        foreach (var (button, _) in _anchorButtons)
            button.IsChecked = button == clicked;
        RaiseOsdSettingsChanged();
    }

    private void OnOffsetChanged(object sender, RoutedEventArgs e) => ApplyOffsetsAndRaise();

    private void OnOffsetKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) ApplyOffsetsAndRaise();
    }

    /// <summary>Parses both offset boxes (invalid input treated as 0, and the box's text is
    /// corrected in place so the user sees what was actually applied) and raises the change.</summary>
    private void ApplyOffsetsAndRaise()
    {
        int x = int.TryParse(OffsetXBox.Text, out var px) ? px : 0;
        int y = int.TryParse(OffsetYBox.Text, out var py) ? py : 0;
        OffsetXBox.Text = x.ToString();
        OffsetYBox.Text = y.ToString();
        RaiseOsdSettingsChanged();
    }

    private void OnHideDelayChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        HideDelayLabel.Text = FormatSeconds(e.NewValue);
        RaiseOsdSettingsChanged();
    }

    private void OnAnimationChanged(object sender, RoutedEventArgs e) => RaiseOsdSettingsChanged();

    private void OnAnimationDurationChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        AnimationDurationLabel.Text = $"{(int)e.NewValue}ms";
        RaiseOsdSettingsChanged();
    }

    private void OnStepChanged(object sender, SelectionChangedEventArgs e) => RaiseOsdSettingsChanged();

    private void RaiseOsdSettingsChanged()
    {
        if (_initializing) return;

        string anchor = DefaultAnchor;
        foreach (var (button, name) in _anchorButtons)
        {
            if (button.IsChecked == true) { anchor = name; break; }
        }

        OsdSettingsChanged?.Invoke(new OsdSettings(
            Style: SelectedTag(StyleCombo) ?? "dark-pill",
            SkinName: SelectedTag(SkinCombo) ?? "",
            Anchor: anchor,
            OffsetX: int.TryParse(OffsetXBox.Text, out var x) ? x : 0,
            OffsetY: int.TryParse(OffsetYBox.Text, out var y) ? y : 0,
            HideDelaySeconds: HideDelaySlider.Value,
            AnimationEnabled: AnimationCheckBox.IsChecked == true,
            AnimationMs: (int)AnimationDurationSlider.Value,
            StepPercent: int.TryParse(SelectedTag(StepCombo), out var step) ? step : 2));
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
