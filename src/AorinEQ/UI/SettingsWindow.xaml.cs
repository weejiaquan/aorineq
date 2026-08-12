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

    /// <summary>The section currently in the frame. Also the re-entry guard: setting
    /// <c>Nav.SelectedItem</c> raises SelectionChanged, which navigates, which would set it
    /// again.</summary>
    private string? _currentSection;

    /// <summary>Whether <see cref="_pendingSection"/> should also take keyboard focus.</summary>
    private bool _pendingFocus;

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

    /// <summary>Raised by the health banner's "Switch to Windows volume mode" button. App runs the
    /// same live transition the mode radios do, so the volume keys work on the very next press —
    /// this is the one action available while Equalizer APO is detached that makes the app work
    /// again immediately, and it is a real mode change, not a display state.</summary>
    public event Action? SwitchToSystemModeRequested;

    /// <summary>Raised by "Repair automatically". App confirms in plain language, then runs the
    /// elevated helper that writes the endpoint's effect settings.</summary>
    public event Action? RepairEapoRequested;

    /// <summary>Raised by "Undo repair" — offered for as long as a backup of the user's original
    /// settings exists.</summary>
    public event Action? UndoEapoRepairRequested;

    /// <summary>The last health reading and the mode it was judged against, kept so the banner can
    /// be re-rendered (mode change, theme change) without asking the machine again — and so the
    /// repair button knows which of its two jobs it currently has.</summary>
    private EapoHealthSnapshot? _health;
    private string _volumeMode = VolumeModes.Eapo;
    private bool _eapoApplies = true;

    public SettingsWindow(bool autostartEnabled, bool runAsAdmin, bool isElevated, string version,
        Settings settings, EapoHealthSnapshot? health)
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
        SetEapoHealth(health, settings.VolumeMode, EapoDependency.Applies(settings));

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

        // The sidebar is driven from each item's OWN Click, not from NavigationView.SelectionChanged.
        // WPF-UI's NavigationView navigates to page TYPES: an item carrying only a TargetPageTag
        // (which is what this window uses — its sections are elements in this namescope, not Page
        // classes) resolves no page on click, bails out before updating SelectedItem, and never
        // raises SelectionChanged. The sidebar looks alive and does nothing. NavigationViewItem is a
        // ButtonBase, so Click is raised for a mouse click AND for Enter/Space on a focused item,
        // which is also what makes keyboard navigation work.
        foreach (var item in NavItems())
        {
            var tag = item.TargetPageTag;
            item.Click += (_, _) => Navigate(tag);
        }
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
    /// <param name="focusPrimary">Also move keyboard focus to the section's main control. Set for
    /// deep links, which used to land the caret ON the thing the link is about; left off when the
    /// user clicks the sidebar, where stealing focus into a combo box would be surprising.</param>
    public void Navigate(string section, bool focusPrimary = false)
    {
        if (!_sections.ContainsKey(section)) return;
        if (section == _currentSection && !focusPrimary && _pendingSection is null) return;
        _pendingSection = section;
        _pendingFocus = focusPrimary;
        if (IsLoaded) ApplyPendingSection();
    }

    private void ApplyPendingSection()
    {
        if (_pendingSection is not { } section || !_sections.TryGetValue(section, out var body)) return;
        _pendingSection = null;
        bool focusPrimary = _pendingFocus;
        _pendingFocus = false;

        _currentSection = section;
        Nav.ReplaceContent(body);

        // IsActive is the ONE source of truth for which item looks selected. NavigationView's own
        // SelectedItem is read-only from outside, and it is only ever set by the page-type
        // navigation this window deliberately does not use — so leaving it alone (rather than
        // having two disagreeing notions of "selected") is the honest shape here.
        var item = NavItemFor(section);
        foreach (var other in NavItems()) other.IsActive = ReferenceEquals(other, item);

        if (focusPrimary) FocusPrimaryControl(section);
    }

    /// <summary>Focus target for a deep link: the one control the linked-to section is about.
    /// Queued at Loaded priority because the frame has only just been given the section and its
    /// content is not focusable until the layout pass has run.</summary>
    private void FocusPrimaryControl(string section)
    {
        FrameworkElement? primary = section switch
        {
            SettingsSections.Skins => SkinCombo,
            SettingsSections.Osd => StyleCombo,
            SettingsSections.Updates => CheckUpdatesButton,
            SettingsSections.Equalizer => OpenEqualizerButton,
            _ => null,
        };
        if (primary is null) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            primary.BringIntoView();
            primary.Focus();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private IEnumerable<Wpf.Ui.Controls.NavigationViewItem> NavItems() =>
        new[] { NavVolume, NavOsd, NavSkins, NavEqualizer, NavUpdates, NavAbout };

    private Wpf.Ui.Controls.NavigationViewItem? NavItemFor(string section) =>
        NavItems().FirstOrDefault(i => i.TargetPageTag == section);

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

    /// <summary>Renders the health row and the banner from one reading. Called at construction,
    /// on every SyncState, and by App after every health check — so the window shows what the
    /// monitor last saw rather than asking the machine on its own schedule, and the "last checked"
    /// time it prints is the time of a reading that really happened.
    ///
    /// A null reading means the monitor has not produced one yet (only possible in the instant
    /// between the window opening and its forced check landing): the row says so instead of
    /// guessing, and the banner stays down rather than flashing a fault that has not been
    /// measured.</summary>
    /// <param name="applies">Whether this user depends on Equalizer APO at all
    /// (<see cref="EapoDependency"/>). False hides the whole health group AND the banner: someone
    /// running AorinEQ purely for the OSD is told nothing about a program they do not use.</param>
    public void SetEapoHealth(EapoHealthSnapshot? health, string volumeMode, bool applies)
    {
        _health = health;
        _volumeMode = volumeMode;
        _eapoApplies = applies;

        var groupVisibility = applies ? Visibility.Visible : Visibility.Collapsed;
        EapoHealthHeading.Visibility = groupVisibility;
        EapoHealthCard.Visibility = groupVisibility;
        if (!applies)
        {
            EapoBanner.Visibility = Visibility.Collapsed;
            return;
        }

        HealthInstalledValue.Text = health is null ? "Checking…" : YesNo(health.Installed);
        HealthActiveValue.Text = health is null ? "Checking…"
            : !health.Installed ? "—"
            : YesNo(health.ActiveOnDevice);
        HealthIncludeValue.Text = health is null ? "Checking…"
            : !health.Installed ? "—"
            : health.IncludeLinePresent switch
            {
                true => "Yes",
                false => "No",
                // Another tool had Equalizer APO's config file open for the moment we looked. Not
                // a fault, and saying "No" would be a lie about the user's setup.
                null => "Couldn't read it just now",
            };
        HealthCheckedValue.Text = health is null
            ? "—"
            : health.CheckedAt.ToLocalTime().ToString("HH:mm:ss");

        OpenConfiguratorButton.IsEnabled = EapoDetection.GetConfiguratorPath() is not null;

        // Offered only when it is genuinely available for THIS device, and never when the device
        // is already being processed — a "repair" that has nothing to repair is a button that
        // invites a UAC prompt for nothing. The reason it is unavailable becomes the tooltip, so
        // a disabled button still explains itself.
        _repairUnavailableReason = health is null || health.ActiveOnDevice
            ? null
            : EapoRepair.WhyNotAvailable(health.EndpointGuid);
        bool canRepair = health is { ActiveOnDevice: false } && _repairUnavailableReason is null;
        RepairEapoButton.IsEnabled = canRepair;
        RepairEapoButton.ToolTip = canRepair
            ? "Switch Equalizer APO back on for the device you're using now."
            : _repairUnavailableReason;

        ApplyEapoBanner();
    }

    /// <summary>Null when the automatic repair is available. Also decides which job the banner's
    /// first button has.</summary>
    private string? _repairUnavailableReason;

    /// <summary>Shows or hides "Undo repair". App calls this after every repair attempt and at
    /// startup: the button exists exactly as long as a backup of the user's original settings
    /// does.</summary>
    public void SetEapoUndoAvailable(bool available) =>
        UndoRepairButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The one line under the repair buttons: progress while the elevated helper runs,
    /// then its verdict. Buttons are disabled while it is busy so a second prompt cannot be
    /// stacked on the first.</summary>
    public void SetEapoRepairStatus(string text, bool busy)
    {
        EapoRepairStatusText.Text = text;
        EapoRepairStatusText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        if (busy)
        {
            RepairEapoButton.IsEnabled = false;
            UndoRepairButton.IsEnabled = false;
            EapoBannerRepairButton.IsEnabled = false;
        }
        else
        {
            // Re-enabled from the fresh reading App takes right after this, not from here: what
            // the buttons should offer depends on the state the helper left behind.
            UndoRepairButton.IsEnabled = true;
            EapoBannerRepairButton.IsEnabled = true;
        }
    }

    private void OnRepairEapo(object sender, RoutedEventArgs e) => RepairEapoRequested?.Invoke();

    private void OnUndoEapoRepair(object sender, RoutedEventArgs e) => UndoEapoRepairRequested?.Invoke();

    private static string YesNo(bool value) => value ? "Yes" : "No";

    /// <summary>The banner itself: hidden while healthy (and while nothing has been measured), and
    /// otherwise carrying the words for THIS fault in THIS volume mode. The mode-switch button is
    /// collapsed in Windows volume mode because there it would do nothing — an offer that changes
    /// nothing is worse than no offer.</summary>
    private void ApplyEapoBanner()
    {
        if (!_eapoApplies || _health is null || _health.Healthy)
        {
            EapoBanner.Visibility = Visibility.Collapsed;
            return;
        }
        EapoBannerTitle.Text = EapoHealthCopy.BannerTitle(_health);
        EapoBannerBody.Text = EapoHealthCopy.BannerBody(_health, _volumeMode);
        // One button, two jobs, and which one it has depends on what is actually possible: when
        // AorinEQ can switch the device back on itself, that is what the button offers; otherwise
        // it opens the tool that can (Equalizer APO's Configurator, or the setup guide when there
        // is no install to configure). It never offers a repair that would then refuse.
        _bannerOffersRepair = _health is { Installed: true, ActiveOnDevice: false }
            && _repairUnavailableReason is null;
        EapoBannerRepairButton.Content = _bannerOffersRepair
            ? "Repair automatically"
            : EapoHealthCopy.RepairButtonText(_health);
        EapoBannerRepairButton.IsEnabled = true;
        EapoSwitchModeButton.Visibility =
            _volumeMode == VolumeModes.System ? Visibility.Collapsed : Visibility.Visible;
        EapoBanner.Visibility = Visibility.Visible;
    }

    /// <summary>The banner's repair button. Equalizer APO's own Configurator is the thing that
    /// re-ticks a device, so that is what this opens when there is one; with no install there is
    /// nothing to configure and the setup guide — which downloads and runs the real installer — is
    /// the honest destination. Both cases are reachable: the Configurator is missing exactly when
    /// Equalizer APO is not installed, and also when an install is damaged.
    ///
    /// The choice is made on whether a Configurator EXISTS, not on whether launching it succeeded:
    /// launching fails when the user declines the elevation prompt, and answering "no" to UAC must
    /// not then spring an installer wizard on them.</summary>
    private void OnEapoRepair(object sender, RoutedEventArgs e)
    {
        if (_bannerOffersRepair)
            RepairEapoRequested?.Invoke();
        else if (EapoDetection.GetConfiguratorPath() is null)
            SetupGuideRequested?.Invoke();
        else
            TryLaunchConfigurator();
    }

    private bool _bannerOffersRepair;

    private void OnEapoSwitchMode(object sender, RoutedEventArgs e) => SwitchToSystemModeRequested?.Invoke();

    /// <summary>Launches EAPO's Configurator (elevated — it registers APOs on devices). False when
    /// there is no Configurator to launch, or the user declined the elevation prompt.</summary>
    private void OnOpenConfigurator(object sender, RoutedEventArgs e) => TryLaunchConfigurator();

    private static bool TryLaunchConfigurator()
    {
        var path = EapoDetection.GetConfiguratorPath();
        if (path is null) return false;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Verb = "runas",
            });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // UAC declined — the user said no, so nothing else should happen either
        }
    }

    private void OnOpenSetupGuide(object sender, RoutedEventArgs e) => SetupGuideRequested?.Invoke();

    private void OnOpenEqualizer(object sender, RoutedEventArgs e) => EqualizerRequested?.Invoke();

    /// <summary>Re-syncs every control (General tab and OSD tab alike) from current app state.
    /// Called both after autostart/RunAsAdmin changes and every time Settings is (re)opened —
    /// which is also when the skins folder gets rescanned.</summary>
    public void SyncState(bool autostartEnabled, bool runAsAdmin, bool isElevated, Settings settings,
        EapoHealthSnapshot? health)
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
        // The mode comes from settings, so a mode change made anywhere (radios, the banner button,
        // the setup guide) re-renders the banner against the mode the app is really in.
        SetEapoHealth(health ?? _health, settings.VolumeMode, EapoDependency.Applies(settings));

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
    /// can't emit a transient "no skin selected" change.
    ///
    /// Each row is labelled with the skin's CREDIT (title and author when it has them, the folder
    /// name otherwise) while the Tag stays the folder name — the Tag is what selection, settings
    /// and every lookup use, so a display name can never become an identity.</summary>
    private void PopulateSkins(string currentSkinName)
    {
        SkinCombo.SelectionChanged -= OnSkinChanged;
        try
        {
            SkinCombo.Items.Clear();
            foreach (var skin in SkinLoader.Scan(ApoPaths.GetSkinsRoot()))
            {
                var item = new ComboBoxItem
                {
                    Content = skin.DisplayLabel,
                    Tag = skin.Name,
                    IsEnabled = skin.IsValid,
                    ToolTip = SkinTooltip(skin),
                };
                SkinCombo.Items.Add(item);
            }
            SelectByTag(SkinCombo, currentSkinName);
        }
        finally
        {
            SkinCombo.SelectionChanged += OnSkinChanged;
        }
        UpdateSkinCredit();
    }

    /// <summary>Everything about a skin that doesn't fit the one-line label: its folder (which the
    /// label hides when the skin has a title), its description, tags and source, and — for a skin
    /// that won't load — why.</summary>
    private static string SkinTooltip(SkinInfo skin)
    {
        var lines = new List<string> { "Folder: " + skin.Name };
        if (skin.Meta.Description is { } description) lines.Add(description);
        if (skin.Meta.Version is { } version) lines.Add("Version " + version);
        if (skin.Meta.Tags.Count > 0) lines.Add("Tags: " + SkinMeta.FormatTags(skin.Meta.Tags));
        if (skin.Meta.SourceUrl is { } source) lines.Add(source);
        if (skin.Error is { } error) lines.Add(error);
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Shows who made the selected skin, under the picker. Collapsed when the skin has no
    /// credits at all — every skin authored before 3.2 — so nothing appears that says nothing.</summary>
    private void UpdateSkinCredit()
    {
        var name = SelectedTag(SkinCombo);
        var meta = name is null
            ? SkinMeta.None
            : SkinLoader.Load(Path.Combine(ApoPaths.GetSkinsRoot(), name)).Meta;

        var parts = new List<string>();
        if (meta.Title is { } title) parts.Add(title);
        if (meta.Author is { } author) parts.Add("by " + author);
        if (meta.Version is { } version) parts.Add("v" + version);

        SkinCreditText.Text = string.Join(" · ", parts);
        SkinCreditText.Visibility = parts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
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

    private void OnSkinChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSkinCredit();
        RaiseOsdSettingsChanged();
    }

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
