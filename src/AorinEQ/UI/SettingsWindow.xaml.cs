using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AorinEQ.Core;

namespace AorinEQ.UI;

/// <summary>The Settings shell: a Windows 11-style sidebar (WPF-UI's NavigationView) over six
/// sections of setting cards.
///
/// The six section bodies are declared in this window's XAML — inside its namescope, so every
/// control keeps the generated field this file already uses — and are detached from their holder at
/// construction, then handed to the NavigationView's frame one at a time. Splitting them into six
/// NavigationView Pages instead would have moved thirty named controls into six new namescopes and
/// rewritten every handler below, for a release that deliberately changes no behaviour.</summary>
public partial class SettingsWindow : Wpf.Ui.Controls.FluentWindow
{
    private const string DefaultAnchor = "bottom-center";

    private bool _initializing = true;

    private readonly (ToggleButton Button, string Anchor)[] _anchorButtons;

    /// <summary>Section name (see <see cref="SettingsSections"/>) to its detached body. Populated
    /// once, in declaration order, so the NavigationView can show any of them on demand.</summary>
    private readonly Dictionary<string, UIElement> _sections = new();

    /// <summary>Section requested before the NavigationView had a template to put it in; applied
    /// on Loaded. Null once it has been.</summary>
    private string? _pendingSection;

    public event Action<bool>? AutostartChanged;
    public event Action<bool>? RunAsAdminChanged;

    /// <summary>Raised when the "Enable aorineq:// links" checkbox changes. App
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

        DetachSections();

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
        VersionText.Text = "AorinEQ " + version;
        ProtocolLinksBox.IsChecked = settings.ProtocolLinksEnabled;
        AutoUpdateBox.IsChecked = settings.AutoUpdate;

        ApplyOsdSettings(settings);
        ApplyVolumeMode(settings);
        ApplyDeviceVolumes(settings);
        PopulateSkins(settings.SkinName);
        RefreshEapoStatus();

        _initializing = false;

        // Nothing is in the frame until a section is selected, so the window would open blank.
        // Applied on Loaded — see Navigate.
        Navigate(SettingsSections.All[0]);
        Loaded += (_, _) => ApplyPendingSection();
    }

    /// <summary>Takes the six section bodies out of the collapsed holder they are declared in, so
    /// exactly one is ever parented — a UIElement can only have one parent, and the NavigationView
    /// frame becomes that parent as each is navigated to.</summary>
    private void DetachSections()
    {
        var bodies = new (string Section, UIElement Body)[]
        {
            (SettingsSections.Volume, SectionVolume),
            (SettingsSections.Osd, SectionOsd),
            (SettingsSections.Skins, SectionSkins),
            (SettingsSections.Equalizer, SectionEqualizer),
            (SettingsSections.Updates, SectionUpdates),
            (SettingsSections.About, SectionAbout),
        };
        SectionHolder.Children.Clear();
        foreach (var (section, body) in bodies) _sections[section] = body;
    }

    /// <summary>Shows a section and selects its sidebar item. Public because deep links land here:
    /// <c>aorineq://open?page=skins</c> routes through
    /// <see cref="SettingsSections.ForProtocolPage"/>. An unknown name is ignored rather than
    /// blanking the frame — the routing already resolves those to a real section.
    ///
    /// Before the window is loaded the NavigationView has no template yet, and its content
    /// presenter — the thing ReplaceContent writes to — does not exist, so calling it from the
    /// constructor throws. The request is therefore recorded and applied on Loaded. That also
    /// makes a deep link that arrives while Settings is still opening land on the right section
    /// instead of being lost.</summary>
    public void Navigate(string section)
    {
        if (!_sections.ContainsKey(section)) return;
        _pendingSection = section;
        if (IsLoaded) ApplyPendingSection();
    }

    private void ApplyPendingSection()
    {
        if (_pendingSection is not { } section || !_sections.TryGetValue(section, out var body)) return;
        _pendingSection = null;

        // ReplaceContent does not raise SelectionChanged, so the sidebar is set separately; doing
        // it in this order means the handler below is a no-op when the user drives the sidebar.
        Nav.ReplaceContent(body);
        var item = NavItemFor(section);
        foreach (var other in NavItems()) other.IsActive = ReferenceEquals(other, item);
    }

    private IEnumerable<Wpf.Ui.Controls.NavigationViewItem> NavItems() =>
        new[] { NavVolume, NavOsd, NavSkins, NavEqualizer, NavUpdates, NavAbout };

    private Wpf.Ui.Controls.NavigationViewItem? NavItemFor(string section) =>
        NavItems().FirstOrDefault(i => i.TargetPageTag == section);

    /// <summary>The sidebar was clicked. The items carry no target page type, so nothing navigates
    /// on its own — the tag names the section body to show.</summary>
    private void OnSectionSelected(Wpf.Ui.Controls.NavigationView sender, RoutedEventArgs e)
    {
        if (sender.SelectedItem is Wpf.Ui.Controls.NavigationViewItem { TargetPageTag: { } tag })
            Navigate(tag);
    }

    /// <summary>Read-only summary of the per-device volumes the app is tracking. Purely
    /// informational — the volume itself is changed with the keys or the OSD, never here.</summary>
    private void ApplyDeviceVolumes(Settings settings)
    {
        int count = settings.DeviceVolumes?.Count ?? 0;
        DeviceVolumeText.Text = count == 0
            ? "AorinEQ remembers a volume per playback device. None seen yet — press a volume key."
            : $"AorinEQ remembers a volume per playback device, and follows the Windows default. "
                + $"{count} device{(count == 1 ? "" : "s")} remembered.";
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
        ApplyDeviceVolumes(settings);
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
                    "AorinEQ", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (Directory.Exists(Path.Combine(ApoPaths.GetSkinsRoot(), name)))
            {
                var choice = System.Windows.MessageBox.Show(
                    $"A skin named '{name}' already exists. Overwrite it?",
                    "AorinEQ", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (choice != MessageBoxResult.Yes) return;
            }
            SkinArchive.Import(dialog.FileName, ApoPaths.GetSkinsRoot(), name);
            PopulateSkins(SelectedTag(SkinCombo) ?? "");
            if (SelectedTag(StyleCombo) == OsdStyles.Skin && SelectedTag(SkinCombo) == name)
                RaiseOsdSettingsChanged();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            System.Windows.MessageBox.Show(ex.Message, "AorinEQ", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true; // App owns lifetime; hide like the OSD
        Hide();
    }
}
