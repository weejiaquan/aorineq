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

    /// <summary>Raised whenever any OSD control changes (once fully initialized). App merges the
    /// snapshot into its persisted Settings — see <see cref="OsdSettings"/>'s remarks.</summary>
    public event Action<OsdSettings>? OsdSettingsChanged;

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

        ApplyOsdSettings(settings);
        PopulateSkins(settings.SkinName);

        _initializing = false;
    }

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

        ApplyOsdSettings(settings);
        PopulateSkins(settings.SkinName);

        _initializing = false;
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
