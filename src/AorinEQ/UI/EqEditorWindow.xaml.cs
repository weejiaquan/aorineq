using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AorinEQ.Core;
// WinForms is referenced app-wide (tray icon); pin the WPF types this window means.
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using Path = System.IO.Path;
using TextBox = System.Windows.Controls.TextBox;
using Color = System.Windows.Media.Color;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Cursors = System.Windows.Input.Cursors;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace AorinEQ.UI;

/// <summary>The full parametric EQ editor: log-frequency response plot with draggable band
/// nodes, per-scope preset management (Global + one tab per render device), and live
/// post-EQ meters/spectrum from WASAPI loopback. The editor owns NO durable state — every
/// change is pushed immediately through <see cref="ScopeChanged"/> and read back from the
/// app's settings on scope switches, so the config file always reflects what's on screen.</summary>
public partial class EqEditorWindow : Wpf.Ui.Controls.FluentWindow
{
    // Frequency axis, curve resolution and dB scales live in EqCurveRenderer, which the
    // read-only previews (the apply-preset dialog, Simple mode) draw with too.
    private const double FMin = EqCurveRenderer.FMin, FMax = EqCurveRenderer.FMax;
    private const int CurvePoints = EqCurveRenderer.CurvePoints;
    private const int FftSize = 4096;
    private static readonly int[] DbRanges = EqCurveRenderer.DbRanges;
    private static readonly double[] GridFrequencies = { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 };
    private const double SpectrumTopDb = 0, SpectrumBottomDb = -90;

    private readonly Func<Settings> _getSettings;
    private readonly Func<string?> _getActiveDeviceId;
    private readonly Func<string?, double?> _getVolumeDbFor; // per device id; null = no volume preamp (system mode / global scope)

    /// <summary>(deviceId, scope) — deviceId null means the Global scope. Raised on every
    /// edit; the app merges, re-renders the config file, and persists.</summary>
    public event Action<string?, EqScopeSetting>? ScopeChanged;

    /// <summary>The user picked Simple or Advanced; the app persists the choice.</summary>
    public event Action<string>? EditorModeChanged;

    private string _editorMode = EqEditorModes.Advanced;

    /// <summary>Simple mode: three macro sliders, a read-only curve, no band surfaces.</summary>
    private bool SimpleMode => _editorMode == EqEditorModes.Simple;

    // Working copy of the current scope (pushed on every mutation, reloaded on tab switch).
    private string? _scopeDeviceId;
    /// <summary>Whether the user has picked a scope yet. Needed because null is a REAL scope
    /// (Global): without this the "restore the current tab" lookup would always match Global
    /// on first open and the editor could never start on the active device.</summary>
    private bool _scopeChosen;
    private List<EqBand> _bands = new();
    private string _presetName = "";
    private double _presetPreampDb;
    private bool _eqEnabled = true;
    /// <summary>Whether the last three bands are Simple mode's sliders (persisted per scope as
    /// <see cref="EqScopeSetting.MacroBands"/>) rather than bands the user means as bands.</summary>
    private bool _macroBands;

    private int _selectedBand = -1;
    private int _dbRange = 24;
    private bool _syncing;          // programmatic UI updates must not re-enter handlers
    private int _draggingBand = -1;

    /// <summary>Colours for every custom-drawn surface in this window. Re-resolved whenever
    /// Windows switches theme — the plot, meters and spectrum are drawn by hand, so nothing in the
    /// Fluent dictionaries can retheme them for us. See <see cref="EqPalette"/>.</summary>
    private EqPalette _palette = EqPalette.For(SystemTheme.AppsUseLightTheme());

    // Plot elements (persistent between redraws where possible).
    private readonly Polygon _spectrumPolygon = new() { IsHitTestVisible = false };
    private readonly List<UIElement> _gridElements = new();
    private readonly List<UIElement> _curveElements = new();
    private readonly List<Ellipse> _nodes = new();

    // Loopback capture + analysis state (capture thread writes, UI timer reads).
    private readonly LoopbackCapture _capture = new();
    private readonly ClipDetector _clip = new();
    private readonly object _meterLock = new();
    private readonly float[] _ring = new float[FftSize];
    private int _ringPos;
    private double _blockPeakL = MeterMath.FloorDb, _blockRmsL = MeterMath.FloorDb;
    private double _blockPeakR = MeterMath.FloorDb, _blockRmsR = MeterMath.FloorDb;
    private double _shownRmsL = MeterMath.FloorDb, _shownRmsR = MeterMath.FloorDb;
    private double _shownPeakL = MeterMath.FloorDb, _shownPeakR = MeterMath.FloorDb;
    private readonly System.Windows.Threading.DispatcherTimer _frameTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(33), // ~30 fps cap
    };

    private sealed record ScopeTab(string? DeviceId, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>One band's column in the strip. Held so value updates (e.g. from dragging the
    /// node on the plot) can write into the existing boxes instead of rebuilding the strip —
    /// rebuilding on every mouse-move would be visibly slow at 24+ bands and would destroy
    /// the focus/caret of whatever the user is typing in.</summary>
    private sealed record BandColumn(
        Border Frame, ComboBox Type, TextBox Fc, TextBox Gain, TextBox Q, TextBlock Index);

    private readonly List<BandColumn> _columns = new();

    public EqEditorWindow(Func<Settings> getSettings, Func<string?> getActiveDeviceId,
        Func<string?, double?> getVolumeDbFor)
    {
        _getSettings = getSettings;
        _getActiveDeviceId = getActiveDeviceId;
        _getVolumeDbFor = getVolumeDbFor;
        InitializeComponent();

        ApplyPalette();

        BandTypeCombo.ItemsSource = Enum.GetValues<EqBandType>();
        _capture.SamplesAvailable += OnSamples;
        _frameTimer.Tick += (_, _) => OnFrame();

        PreviewKeyDown += OnWindowKeyDown;
        Loaded += (_, _) =>
        {
            // The face comes first: loading a scope already has to know whether the macro bands
            // need to exist.
            _editorMode = EqEditorModes.Resolve(_getSettings());
            PopulateScopeTabs();
            RefreshPresetList();
            ApplyEditorMode();
            // Pin the resolved face the first time the editor opens. Without this, a first-time
            // user who starts in Simple and moves a slider then HAS bands — and would be resolved
            // into Advanced on the next open, having never asked for it.
            if (EqEditorModes.Normalize(_getSettings().EqEditorMode) == EqEditorModes.Unset)
                EditorModeChanged?.Invoke(_editorMode);
            _capture.Start();
            _frameTimer.Start();
        };
        Closed += (_, _) =>
        {
            _frameTimer.Stop();
            _capture.Dispose();
        };

        // Subscribed LAST, after everything above that can throw. ApplicationThemeManager.Changed
        // is a STATIC event and this window is created and destroyed repeatedly, so a subscription
        // that outlives its window roots it forever — and a constructor that threw after
        // subscribing would never reach the OnClosed that removes it, because a window that was
        // never shown is never closed.
        Wpf.Ui.Appearance.ApplicationThemeManager.Changed += OnAppThemeChanged;
    }

    /// <summary>Releases the static theme subscription. An override rather than a Closed handler:
    /// this is the one teardown step whose omission leaks the whole window, so it belongs somewhere
    /// that cannot be reordered away from the subscription that pairs with it.</summary>
    protected override void OnClosed(EventArgs e)
    {
        Wpf.Ui.Appearance.ApplicationThemeManager.Changed -= OnAppThemeChanged;
        base.OnClosed(e);
    }

    /// <summary>The Windows default render device changed while the editor is open:
    /// re-highlight the tabs and re-attach the loopback capture to the new endpoint.</summary>
    public void OnActiveDeviceChanged()
    {
        PopulateScopeTabs();
        _capture.Restart();
    }

    /// <summary>The app-side preset list changed (tray switch, protocol install): refresh.</summary>
    public void RefreshFromApp()
    {
        RefreshPresetList();
        LoadScope(_scopeDeviceId);
    }

    // ---- Scope tabs ----

    private void PopulateScopeTabs()
    {
        var activeId = _getActiveDeviceId();
        var tabs = new List<ScopeTab> { new(null, "Global") };
        foreach (var endpoint in AudioEndpoint.GetRenderEndpoints())
        {
            var marker = endpoint.Id == activeId ? "● " : "";
            tabs.Add(new ScopeTab(endpoint.Id, marker + endpoint.FriendlyName));
        }
        _syncing = true;
        ScopeTabs.ItemsSource = tabs;
        // Keep the user's chosen tab across refreshes; on first open start on the ACTIVE
        // device (what they hear right now), falling back to Global when it isn't listed.
        var select = (_scopeChosen ? tabs.FirstOrDefault(t => t.DeviceId == _scopeDeviceId) : null)
            ?? tabs.FirstOrDefault(t => t.DeviceId == activeId)
            ?? tabs[0];
        ScopeTabs.SelectedItem = select;
        _syncing = false;
        LoadScope(select.DeviceId);
    }

    private void OnScopeSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || ScopeTabs.SelectedItem is not ScopeTab tab)
            return;
        _scopeChosen = true; // an explicit pick survives later tab refreshes
        LoadScope(tab.DeviceId);
    }

    private void LoadScope(string? deviceId)
    {
        // A scope can be reloaded from OUTSIDE the editor (a tray preset switch, a protocol
        // link, the default device changing) while the user is mid-gesture. Both kinds of
        // in-flight edit have to be dropped here, before _bands is replaced, or they would land
        // on the incoming chain: a live plot drag, and any strip box holding uncommitted text.
        EndDrag();
        RetireStripBoxes();
        _scopeDeviceId = deviceId;
        var settings = _getSettings();
        var scope = deviceId is null
            ? settings.GlobalEq
            : settings.DeviceEq is not null && settings.DeviceEq.TryGetValue(deviceId, out var s) ? s : null;
        _bands = scope?.Bands is { } bands ? bands.ToList() : new List<EqBand>();
        _presetName = scope?.PresetName ?? "";
        _presetPreampDb = scope?.PresetPreampDb ?? 0;
        _eqEnabled = scope?.Enabled ?? true;
        _macroBands = scope?.MacroBands ?? false;
        _selectedBand = _bands.Count > 0 ? 0 : -1;

        _syncing = true;
        EqEnabledCheck.IsChecked = _eqEnabled;
        _syncing = false;
        StripHintText.Text = "";
        RebuildFromModel();
    }

    /// <summary>THE routine that makes every editor surface show the current scope's model: the
    /// preset label, the numeric side panel, the band strip, the Simple-mode sliders and note,
    /// and the plot. Every path that replaces or reshapes the chain — a preset switch, an AutoEq
    /// or file import, pasted text, Clear all, an aorineq:// preset link, a scope switch, a
    /// mode switch — ends here.
    ///
    /// It exists because the strip used to be rebuilt only by the paths that incrementally
    /// changed it (+ / × / a node drag), so a bulk replace left it showing the previous chain
    /// while the curve was already correct. Surfaces that are refreshed separately drift; this
    /// one cannot.</summary>
    private void RebuildFromModel()
    {
        // A replaced chain can be shorter than the one that was selected from.
        _selectedBand = EqStripModel.ClampSelection(_selectedBand, _bands.Count);
        SyncPresetCombo();
        SyncBandPanel();
        RebuildBandStrip();
        SyncSimpleControls();
        RedrawAll();
    }

    /// <summary>Replaces the whole chain from an external source (a preset, a file, pasted text,
    /// a link). Selection starts at the first band, and the macro trio stops being the sliders'
    /// — the new chain is not the one they were controlling.</summary>
    private void ReplaceChain(IReadOnlyList<EqBand> bands)
    {
        _bands = bands.ToList();
        _macroBands = false;
        _selectedBand = _bands.Count > 0 ? 0 : -1;
    }

    // ---- Simple / Advanced ----

    private void OnEditorModeChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing)
            return;
        var mode = SimpleModeRadio.IsChecked == true ? EqEditorModes.Simple : EqEditorModes.Advanced;
        if (mode == _editorMode)
            return;
        _editorMode = mode;
        ApplyEditorMode();
        EditorModeChanged?.Invoke(mode);
    }

    /// <summary>Shows the face the current mode calls for. Advanced is the v2.0 editor
    /// unchanged; Simple hides every per-band surface, makes the curve read-only and puts the
    /// three macro bands in place.</summary>
    private void ApplyEditorMode()
    {
        bool simple = SimpleMode;
        _syncing = true;
        SimpleModeRadio.IsChecked = simple;
        AdvancedModeRadio.IsChecked = !simple;
        _syncing = false;

        var advanced = simple ? Visibility.Collapsed : Visibility.Visible;
        AdvancedPresetTools.Visibility = advanced;
        ClearAllButton.Visibility = advanced;
        BandPanel.Visibility = advanced;
        BandStripPanel.Visibility = advanced;
        SimplePanel.Visibility = simple ? Visibility.Visible : Visibility.Collapsed;
        // Read-only curve: no dragging, no double-click-to-add, no wheel-to-Q.
        Plot.IsHitTestVisible = !simple;

        RebuildFromModel();
    }

    /// <summary>Points the Simple-mode controls at the current scope. Deliberately READ-ONLY:
    /// merely switching to Simple mode (or opening the editor in it) must not touch the chain —
    /// an earlier shape wrote three 0 dB bands here, which renamed the scope's preset to
    /// "(custom)" and rewrote aorineq.txt before the user had adjusted anything. The trio is
    /// created by the first slider move instead, which IS a real edit.</summary>
    private void SyncSimpleControls()
    {
        if (!SimpleMode)
            return;
        bool room = EqSimpleMode.HasRoom(_bands, _macroBands);
        BassSlider.IsEnabled = MidSlider.IsEnabled = TrebleSlider.IsEnabled = room;
        ShowMacroGains(EqSimpleMode.ReadOrZero(_bands, _macroBands));
        SimpleNoteText.Text = !room
            ? $"This scope already has {_bands.Count} bands — there's no room for the "
                + $"bass/mid/treble controls (the limit is {EqPreset.MaxBands}). "
                + "Switch to Advanced to edit it."
            : CoexistenceNote();
    }

    /// <summary>Says out loud when other bands are along for the ride, so nothing about them is
    /// a surprise: they are left exactly as they are.</summary>
    private string CoexistenceNote()
    {
        var foreign = EqSimpleMode.ForeignBands(_bands, _macroBands);
        if (foreign.Count == 0)
            return "";
        var what = _presetName.Length > 0 && _presetName != EqPreset.CustomName
            ? $"'{_presetName}'"
            : "your existing chain";
        return $"Adjusting bass/mid/treble on top of {what} "
            + $"({foreign.Count} band{(foreign.Count == 1 ? "" : "s")}) — those bands are left "
            + "untouched. Switch to Advanced to edit them.";
    }

    private void ShowMacroGains(MacroGains gains)
    {
        _syncing = true;
        BassSlider.Value = gains.BassDb;
        MidSlider.Value = gains.MidDb;
        TrebleSlider.Value = gains.TrebleDb;
        _syncing = false;
        UpdateMacroReadouts(gains);
    }

    private void UpdateMacroReadouts(MacroGains gains)
    {
        BassReadout.Text = FormatMacro(gains.BassDb);
        MidReadout.Text = FormatMacro(gains.MidDb);
        TrebleReadout.Text = FormatMacro(gains.TrebleDb);
    }

    private static string FormatMacro(double db) =>
        string.Create(CultureInfo.InvariantCulture, $"{db:+0.0;-0.0;0.0} dB");

    private void OnMacroSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || !SimpleMode)
            return;
        // 0.1 dB steps: what the ParametricEQ text format carries, so the slider position and
        // the file always agree.
        var gains = new MacroGains(
            Math.Round(BassSlider.Value, 1),
            Math.Round(MidSlider.Value, 1),
            Math.Round(TrebleSlider.Value, 1));
        if (!EqSimpleMode.HasRoom(_bands, _macroBands))
            return;
        int before = _bands.Count;
        _bands = EqSimpleMode.Apply(_bands, _macroBands, gains).ToList();
        // The sliders now own this chain's tail — recorded per scope so a chain that merely ends
        // in those three shapes is never mistaken for theirs.
        _macroBands = true;
        UpdateMacroReadouts(gains);
        OnBandsEdited(countChanged: _bands.Count != before);
    }

    private EqScopeSetting CurrentScope() =>
        new(_presetName, _presetPreampDb, _eqEnabled, _bands.ToArray(), _macroBands);

    private void PushScope() => ScopeChanged?.Invoke(_scopeDeviceId, CurrentScope());

    /// <summary>Every band mutation goes through here: custom-marks the preset name,
    /// pushes to the app, and redraws. <paramref name="countChanged"/> rebuilds the band strip
    /// (bands added/removed); otherwise the existing columns are just refreshed in place, so a
    /// drag doesn't tear down the controls the user may be typing in.</summary>
    private void OnBandsEdited(bool countChanged = false)
    {
        if (_presetName.Length > 0 && _presetName != EqPreset.CustomName)
            _presetName = EqPreset.CustomName;
        PushScope();
        if (countChanged)
        {
            RebuildFromModel();
            return;
        }
        // Value-only edit (a drag, a typed field, Flatten): refresh in place rather than
        // rebuilding, so the controls the user is typing in keep their focus and caret.
        SyncPresetCombo();
        RedrawCurves();
        SyncBandPanel();
        RefreshBandStripValues();
        SyncSimpleControls(); // e.g. Flatten must pull the macro sliders back to 0
    }

    // ---- Preset bar ----

    private void RefreshPresetList()
    {
        SyncPresetCombo();
    }

    private void SyncPresetCombo()
    {
        _syncing = true;
        var names = PresetStore.List(ApoPaths.GetPresetsRoot()).ToList();
        bool custom = _presetName.Length == 0 || _presetName == EqPreset.CustomName
            || !names.Contains(_presetName, StringComparer.OrdinalIgnoreCase);
        if (custom)
            names.Insert(0, EqPreset.CustomName);
        PresetCombo.ItemsSource = names;
        PresetCombo.SelectedItem = custom
            ? EqPreset.CustomName
            : names.First(n => string.Equals(n, _presetName, StringComparison.OrdinalIgnoreCase));
        _syncing = false;
    }

    private void OnPresetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || PresetCombo.SelectedItem is not string name || name == EqPreset.CustomName)
            return;
        if (PresetStore.Load(ApoPaths.GetPresetsRoot(), name) is not { } preset)
        {
            SyncPresetCombo(); // vanished on disk since listing
            return;
        }
        _presetName = preset.Name;
        _presetPreampDb = preset.PreampDb;
        ReplaceChain(preset.Bands);
        PushScope();
        RebuildFromModel();
    }

    private void OnSavePreset(object sender, RoutedEventArgs e)
    {
        if (_presetName.Length == 0 || _presetName == EqPreset.CustomName)
        {
            OnSavePresetAs(sender, e);
            return;
        }
        SavePresetNamed(_presetName);
    }

    private void OnSavePresetAs(object sender, RoutedEventArgs e)
    {
        var name = PromptForName("Save preset as", _presetName == EqPreset.CustomName ? "" : _presetName);
        if (name is null)
            return;
        if (PresetStore.ValidateName(name) is { } error)
        {
            MessageBox.Show(this, error, "AorinEQ", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SavePresetNamed(name.Trim());
    }

    private void SavePresetNamed(string name)
    {
        try
        {
            PresetStore.Save(ApoPaths.GetPresetsRoot(), name,
                new EqPreset(name, _presetPreampDb, _bands.ToArray()).Serialize());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, $"Couldn't save the preset: {ex.Message}", "AorinEQ",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _presetName = name;
        PushScope();
        SyncPresetCombo();
    }

    private void OnDeletePreset(object sender, RoutedEventArgs e)
    {
        if (_presetName.Length == 0 || _presetName == EqPreset.CustomName)
            return;
        if (MessageBox.Show(this, $"Delete preset '{_presetName}'?", "AorinEQ",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        PresetStore.Delete(ApoPaths.GetPresetsRoot(), _presetName);
        _presetName = EqPreset.CustomName; // the chain stays audible; only the file is gone
        PushScope();
        SyncPresetCombo();
    }

    private void OnImportFile(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "ParametricEQ files (*.txt)|*.txt|All files (*.*)|*.*",
            Title = "Import ParametricEQ preset",
        };
        if (dialog.ShowDialog(this) != true)
            return;
        string text;
        try
        {
            text = File.ReadAllText(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Couldn't read the file: {ex.Message}", "AorinEQ",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var name = PresetStore.SanitizeName(Path.GetFileNameWithoutExtension(dialog.FileName));
        var preset = EqPreset.Parse(name, text);
        if (preset.Bands.Count == 0)
        {
            MessageBox.Show(this, "That file doesn't contain any Equalizer APO filter lines.",
                "AorinEQ", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            PresetStore.Save(ApoPaths.GetPresetsRoot(), name, text); // import is file copy
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, $"Couldn't import: {ex.Message}", "AorinEQ",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ApplyPreset(preset);
    }

    private void OnAutoEq(object sender, RoutedEventArgs e) => OpenAutoEqImport("");

    /// <summary>Opens the AutoEq search, optionally pre-searched — the editor is where an
    /// imported profile lands, so an <c>aorineq://autoeq</c> deep link comes through here
    /// and follows exactly the same download-and-apply path as the toolbar button.</summary>
    public void OpenAutoEqImport(string model)
    {
        var dialog = new AutoEqImportDialog(model) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.ImportedPreset is { } preset)
            ApplyPreset(preset);
    }

    /// <summary>A freshly imported/downloaded preset becomes the current scope's chain (AutoEq,
    /// "Import file…"). A bulk replace like this goes through <see cref="RebuildFromModel"/>, so
    /// the band strip shows the new chain rather than the previous one.</summary>
    private void ApplyPreset(EqPreset preset)
    {
        _presetName = preset.Name;
        _presetPreampDb = preset.PreampDb;
        ReplaceChain(preset.Bands);
        PushScope();
        RebuildFromModel();
    }

    private void OnEqEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing)
            return;
        _eqEnabled = EqEnabledCheck.IsChecked == true;
        PushScope();
        RedrawCurves();
    }

    private void OnAutoPreamp(object sender, RoutedEventArgs e)
    {
        _presetPreampDb = EqResponse.SuggestPreampDb(_bands);
        PushScope();
        UpdatePreampReadout();
    }

    private string? PromptForName(string title, string initial)
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 8, 0, 12) };
        var ok = new Button { Content = "Save", Padding = new Thickness(12, 4, 12, 4), IsDefault = true };
        var cancel = new Button
        {
            Content = "Cancel", Padding = new Thickness(12, 4, 12, 4), IsCancel = true,
            Margin = new Thickness(8, 0, 0, 0),
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "Preset name:" });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var prompt = new Window
        {
            Title = title, Content = panel, Owner = this, Width = 340,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        ok.Click += (_, _) => prompt.DialogResult = true;
        prompt.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return prompt.ShowDialog() == true ? box.Text : null;
    }

    // ---- Band strip (Peace-style: one typable column per band, arbitrary count) ----

    /// <summary>Rebuilds every column. Called when the band COUNT changes or a scope loads —
    /// value-only changes go through <see cref="RefreshBandStripValues"/>.</summary>
    private void RebuildBandStrip()
    {
        RetireStripBoxes();
        BandStrip.Children.Clear();
        _columns.Clear();
        for (int i = 0; i < _bands.Count; i++)
            BandStrip.Children.Add(BuildColumn(i));
        RefreshBandStripValues();
        UpdateStripChrome();
    }

    private Border BuildColumn(int index)
    {
        var stack = new StackPanel { Width = 84, Margin = new Thickness(3, 0, 3, 0) };

        var header = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 3) };
        var indexLabel = new TextBlock
        {
            Foreground = Brush(_palette.TextDim),
            FontSize = 10, VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(indexLabel, Dock.Left);
        header.Children.Add(indexLabel);
        var remove = new Button
        {
            Content = "✕", FontSize = 10, Width = 18, Height = 18, Padding = new Thickness(0),
            Tag = index, ToolTip = "Remove this band",
        };
        remove.Click += (_, _) => RemoveBandAt(index);
        DockPanel.SetDock(remove, Dock.Right);
        header.Children.Add(remove);
        stack.Children.Add(header);

        var typeCombo = new ComboBox { ItemsSource = Enum.GetValues<EqBandType>(), FontSize = 11, Tag = index };
        typeCombo.SelectionChanged += OnStripTypeChanged;
        stack.Children.Add(typeCombo);

        stack.Children.Add(StripLabel("Hz"));
        var fc = StripBox(index, EqBandField.Fc);
        stack.Children.Add(fc);
        stack.Children.Add(StripLabel("dB"));
        var gain = StripBox(index, EqBandField.GainDb);
        stack.Children.Add(gain);
        stack.Children.Add(StripLabel("Q"));
        var q = StripBox(index, EqBandField.Q);
        stack.Children.Add(q);

        var frame = new Border
        {
            BorderThickness = new Thickness(1.5),
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(3),
            Background = Brush(_palette.PanelBackground),
            Child = stack,
            Tag = index,
        };
        // Clicking anywhere in the column selects that band (two-way sync with the plot node).
        frame.PreviewMouseLeftButtonDown += (_, _) => SelectBand(index);
        _columns.Add(new BandColumn(frame, typeCombo, fc, gain, q, indexLabel));
        return frame;
    }

    /// <summary>Windows switched light/dark: re-resolve the hand-drawn palette and repaint the
    /// surfaces that use it.</summary>
    private void OnAppThemeChanged(Wpf.Ui.Appearance.ApplicationTheme theme, Color accent)
    {
        ApplyPalette();
        RestyleStripColumns();
        RedrawAll();
    }

    /// <summary>Resolves the palette for the current theme and publishes it as this window's brush
    /// resources, which every DynamicResource in its XAML then picks up.</summary>
    private void ApplyPalette()
    {
        _palette = EqPaletteBrushes.Apply(Resources);
        _spectrumPolygon.Fill = Brush(_palette.Spectrum);
    }

    /// <summary>The band strip's columns are built in code, so their colours do not come from the
    /// XAML resources and have to be re-applied by hand after a theme change. The selected
    /// column's border is owned by SyncStripFromModel, which runs right after.</summary>
    private void RestyleStripColumns()
    {
        foreach (var column in _columns)
        {
            column.Index.Foreground = Brush(_palette.TextDim);
            column.Frame.Background = Brush(_palette.PanelBackground);
        }
        // Repaints the selected column's border, which is owned by the value sync rather than by
        // the loop above. Recolouring in place rather than rebuilding the strip is deliberate: a
        // rebuild retires the typable boxes, and a theme change must not discard what the user is
        // halfway through typing into one.
        RefreshBandStripValues();
    }

    private static SolidColorBrush Brush(System.Drawing.Color c) => EqPaletteBrushes.Brush(c);

    private TextBlock StripLabel(string text) => new()
    {
        Text = text, FontSize = 9.5,
        Foreground = Brush(_palette.TextDim),
        Margin = new Thickness(0, 3, 0, 1),
    };

    /// <summary>A directly typable field: select-all + type works, Tab moves on, Enter
    /// commits, and focus loss commits too.</summary>
    private TextBox StripBox(int index, EqBandField field)
    {
        var box = new TextBox { FontSize = 11.5, Tag = (index, field) };
        box.GotKeyboardFocus += (_, _) => { SelectBand(index); box.SelectAll(); };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitStripBox(box);
                e.Handled = true;
            }
        };
        box.LostFocus += (_, _) => CommitStripBox(box);
        return box;
    }

    /// <summary>Disowns the current strip's text boxes so a late commit from one of them cannot
    /// land on a chain it was never editing. A box that is being replaced still raises LostFocus
    /// AFTER the rebuild — focus moves out of a detached element — and
    /// <see cref="CommitStripBox"/> would then write that stale text into the new band list at
    /// the old index. Clearing the Tag is what makes the commit a no-op.</summary>
    private void RetireStripBoxes()
    {
        foreach (var column in _columns)
            column.Fc.Tag = column.Gain.Tag = column.Q.Tag = null;
    }

    private void CommitStripBox(TextBox box)
    {
        if (_syncing || box.Tag is not ValueTuple<int, EqBandField> tag)
            return;
        var (index, field) = tag;
        if (index < 0 || index >= _bands.Count)
            return;
        var updated = EqFieldInput.Apply(_bands[index], field, box.Text, out var outcome);
        ShowStripOutcome(outcome, field);
        if (updated == _bands[index])
        {
            RefreshBandStripValues(); // revert/no-op: put the stored value back in the box
            return;
        }
        _bands[index] = updated;
        OnBandsEdited();
    }

    /// <summary>The inline cue for a typed value that wasn't taken literally. Clears on the
    /// next accepted edit, so it never lingers as noise.</summary>
    private void ShowStripOutcome(EqFieldOutcome outcome, EqBandField field) =>
        StripHintText.Text = outcome switch
        {
            EqFieldOutcome.Reverted => $"{FieldName(field)}: not a number — kept the previous value.",
            EqFieldOutcome.Clamped => $"{FieldName(field)} was outside the supported range — clamped.",
            _ => "",
        };

    private static string FieldName(EqBandField field) => field switch
    {
        EqBandField.Fc => "Frequency",
        EqBandField.GainDb => "Gain",
        _ => "Q",
    };

    private void OnStripTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || sender is not ComboBox combo || combo.Tag is not int index
            || combo.SelectedItem is not EqBandType type || index >= _bands.Count)
            return;
        SelectBand(index);
        ApplyBandType(index, type);
    }

    /// <summary>Writes current band values into the existing columns (no control rebuild) and
    /// refreshes selection highlighting — the path used while dragging a node on the plot.</summary>
    private void RefreshBandStripValues()
    {
        if (_columns.Count != _bands.Count)
        {
            RebuildBandStrip();
            return;
        }
        _syncing = true;
        var model = EqStripModel.Build(_bands, _selectedBand);
        for (int i = 0; i < model.Count; i++)
        {
            var cell = model[i];
            var column = _columns[i];
            column.Index.Text = cell.Number.ToString(CultureInfo.InvariantCulture);
            column.Type.SelectedItem = cell.Type;
            // Don't fight the user's caret: skip the box currently being typed in.
            if (!column.Fc.IsKeyboardFocusWithin)
                column.Fc.Text = cell.Fc;
            if (!column.Gain.IsKeyboardFocusWithin)
                column.Gain.Text = cell.GainDb;
            if (!column.Q.IsKeyboardFocusWithin)
                column.Q.Text = cell.Q;
            column.Gain.IsEnabled = cell.GainEnabled; // gainless types have no Gain token at all
            column.Frame.BorderBrush = cell.Selected
                ? Brush(_palette.NodeSelected)
                : System.Windows.Media.Brushes.Transparent;
        }
        _syncing = false;
        UpdateStripChrome();
    }

    private void UpdateStripChrome()
    {
        bool atCap = _bands.Count >= EqPreset.MaxBands;
        AddBandButton.IsEnabled = !atCap;
        AddBandButton.ToolTip = atCap
            ? $"Band limit reached ({EqPreset.MaxBands} per scope)."
            : "Add a band (then type its frequency)";
        BandCountText.Text = $"{_bands.Count}/{EqPreset.MaxBands}";
    }

    /// <summary>Selects a band from either surface (strip column or plot node) and syncs the
    /// other one — the two views always highlight the same band.</summary>
    private void SelectBand(int index)
    {
        if (index < 0 || index >= _bands.Count || index == _selectedBand)
            return;
        _selectedBand = index;
        RedrawCurves();
        SyncBandPanel();
        RefreshBandStripValues();
    }

    private void OnAddBand(object sender, RoutedEventArgs e)
    {
        if (!EqPreset.TryAppend(_bands, EqPreset.NewBand()))
        {
            StripHintText.Text = $"Band limit reached ({EqPreset.MaxBands} per scope).";
            return;
        }
        StripHintText.Text = "";
        _selectedBand = _bands.Count - 1;
        OnBandsEdited(countChanged: true);
        // Focus the new column's frequency box so the user can type straight away.
        var column = _columns[^1];
        column.Fc.Focus();
        column.Fc.SelectAll();
        BandStrip.UpdateLayout();
        column.Frame.BringIntoView();
    }

    private void RemoveBandAt(int index)
    {
        if (index < 0 || index >= _bands.Count)
            return;
        _bands.RemoveAt(index);
        _selectedBand = Math.Min(_selectedBand, _bands.Count - 1);
        StripHintText.Text = "";
        OnBandsEdited(countChanged: true);
    }

    // ---- Toolbar: bulk text, flatten, clear ----

    private void OnEditAsText(object sender, RoutedEventArgs e)
    {
        var dialog = new EqTextDialog(new EqPreset(_presetName, _presetPreampDb, _bands.ToArray()))
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true || dialog.Result is not { } parsed)
            return;
        // Replaces the scope wholesale — the dialog guarantees the text parsed cleanly first.
        _presetPreampDb = parsed.PreampDb;
        ReplaceChain(parsed.Bands);
        StripHintText.Text = "";
        OnBandsEdited(countChanged: true);
    }

    /// <summary>Puts this scope's whole chain on the clipboard as an aorineq:// link. The
    /// preset travels INSIDE the link (nothing is hosted anywhere), so it can be pasted into a
    /// chat or a forum post and whoever clicks it gets the same confirm-and-preview dialog a
    /// hosted preset link shows.</summary>
    private void OnCopyShareLink(object sender, RoutedEventArgs e)
    {
        if (_bands.Count == 0)
        {
            StripHintText.Text = "There are no bands to share yet.";
            return;
        }
        if (!EqShare.TryBuildShareUrl(
                new EqPreset(_presetName, _presetPreampDb, _bands.ToArray()), out var url, out var error))
        {
            MessageBox.Show(this, error, "AorinEQ", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            System.Windows.Clipboard.SetText(url);
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            // Another process can hold the clipboard open; that's a transient failure, not a bug.
            MessageBox.Show(this, $"Couldn't copy the link: {ex.Message}", "AorinEQ",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        StripHintText.Text = $"Share link copied ({url.Length} characters).";
    }

    private void OnFlatten(object sender, RoutedEventArgs e)
    {
        // Gains to 0 and the scope's preset preamp to 0, keeping every band's type/Fc/Q so the
        // chain can be re-shaped from a flat baseline. No confirm: reloading a preset restores.
        _bands = EqPreset.Flatten(_bands).ToList();
        _presetPreampDb = 0;
        StripHintText.Text = "";
        OnBandsEdited();
    }

    private void OnClearBands(object sender, RoutedEventArgs e)
    {
        ReplaceChain(Array.Empty<EqBand>());
        _presetPreampDb = 0;
        StripHintText.Text = "";
        OnBandsEdited(countChanged: true);
    }

    // ---- Numeric side panel ----

    private void SyncBandPanel()
    {
        _syncing = true;
        bool has = _selectedBand >= 0 && _selectedBand < _bands.Count;
        BandTypeCombo.IsEnabled = FcBox.IsEnabled = GainBox.IsEnabled = QBox.IsEnabled
            = RemoveBandButton.IsEnabled = has;
        if (has)
        {
            var band = _bands[_selectedBand];
            BandTypeCombo.SelectedItem = band.Type;
            // Same formatting as the strip, so one value never reads two ways.
            FcBox.Text = EqStripModel.FormatFc(band.Fc);
            GainBox.Text = EqStripModel.FormatGain(band.GainDb);
            QBox.Text = EqStripModel.FormatQ(band.Q);
            GainBox.IsEnabled = band.HasGain;
        }
        else
        {
            BandTypeCombo.SelectedItem = null;
            FcBox.Text = GainBox.Text = QBox.Text = "";
        }
        DbRangeButton.Content = $"Scale: ±{_dbRange} dB";
        UpdatePreampReadout();
        _syncing = false;
    }

    private void UpdatePreampReadout()
    {
        var volumeText = _scopeDeviceId is null
            ? "global scope"
            : _getVolumeDbFor(_scopeDeviceId) is { } db
                ? string.Create(CultureInfo.InvariantCulture, $"volume {db:0.0} dB")
                : "volume: Windows (system mode)";
        PreampReadout.Text = string.Create(CultureInfo.InvariantCulture,
            $"Preset preamp {_presetPreampDb:0.0} dB · {volumeText}");
    }

    private void OnBandTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _selectedBand < 0 || BandTypeCombo.SelectedItem is not EqBandType type)
            return;
        ApplyBandType(_selectedBand, type);
    }

    /// <summary>Type change from either surface (side panel combo or a strip column).</summary>
    private void ApplyBandType(int index, EqBandType type)
    {
        var band = _bands[index];
        if (band.Type == type)
            return;
        // Gainless types zero the gain; Q jumps to the type's conventional default when
        // coming from a very different shape (a notch's Q 30 makes no sense on a shelf).
        double q = type == EqBandType.Notch ? EqPreset.DefaultNotchQ
            : band.Type == EqBandType.Notch ? EqPreset.DefaultQ
            : band.Q;
        _bands[index] = EqPreset.Clamp(band with
        {
            Type = type,
            GainDb = type is EqBandType.Peak or EqBandType.LowShelf or EqBandType.HighShelf ? band.GainDb : 0,
            Q = q,
        });
        OnBandsEdited();
    }

    private void OnBandBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            OnBandBoxCommit(sender, e);
    }

    /// <summary>Commits the numeric side panel through the SAME typed-field policy as the band
    /// strip (<see cref="EqFieldInput"/>): unparseable text reverts, out-of-range clamps, and
    /// either way the user gets an inline cue.</summary>
    private void OnBandBoxCommit(object sender, RoutedEventArgs e)
    {
        if (_syncing || _selectedBand < 0)
            return;
        var band = _bands[_selectedBand];
        var updated = EqFieldInput.Apply(band, EqBandField.Fc, FcBox.Text, out var fcOutcome);
        updated = EqFieldInput.Apply(updated, EqBandField.GainDb, GainBox.Text, out var gainOutcome);
        updated = EqFieldInput.Apply(updated, EqBandField.Q, QBox.Text, out var qOutcome);

        // Report the first field that wasn't taken literally (gainless types have no gain box
        // in play, so its outcome is ignored there).
        if (fcOutcome != EqFieldOutcome.Applied)
            ShowStripOutcome(fcOutcome, EqBandField.Fc);
        else if (band.HasGain && gainOutcome != EqFieldOutcome.Applied)
            ShowStripOutcome(gainOutcome, EqBandField.GainDb);
        else if (qOutcome != EqFieldOutcome.Applied)
            ShowStripOutcome(qOutcome, EqBandField.Q);
        else
            StripHintText.Text = "";

        if (updated == band)
        {
            SyncBandPanel(); // normalize the text back
            return;
        }
        _bands[_selectedBand] = updated;
        OnBandsEdited();
    }

    private void OnRemoveBand(object sender, RoutedEventArgs e) => RemoveSelectedBand();

    private void OnToggleDbRange(object sender, RoutedEventArgs e)
    {
        int index = Array.IndexOf(DbRanges, _dbRange);
        _dbRange = DbRanges[(index + 1) % DbRanges.Length];
        SyncBandPanel();
        RedrawAll();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        // Simple mode has no band selection to delete — and deleting a macro band from under
        // the sliders would be a surprise.
        if (e.Key == Key.Delete && !SimpleMode && Keyboard.FocusedElement is not TextBox)
        {
            RemoveSelectedBand();
            e.Handled = true;
        }
    }

    private void RemoveSelectedBand() => RemoveBandAt(_selectedBand);

    // ---- Plot: coordinate mapping ----

    private double XFromFreq(double freq) => EqCurveRenderer.XFromFreq(freq, Plot.ActualWidth);

    private double FreqFromX(double x) => EqCurveRenderer.FreqFromX(x, Plot.ActualWidth);

    private double YFromDb(double db) => EqCurveRenderer.YFromDb(db, Plot.ActualHeight, _dbRange);

    private double DbFromY(double y) => EqCurveRenderer.DbFromY(y, Plot.ActualHeight, _dbRange);

    private double YFromSpectrumDb(double db) =>
        Plot.ActualHeight * (1 - (Math.Clamp(db, SpectrumBottomDb, SpectrumTopDb) - SpectrumBottomDb)
            / (SpectrumTopDb - SpectrumBottomDb));

    // ---- Plot: drawing ----

    private void OnPlotSizeChanged(object sender, SizeChangedEventArgs e) => RedrawAll();

    private void RedrawAll()
    {
        RedrawGrid();
        RedrawCurves();
    }

    private void RedrawGrid()
    {
        foreach (var el in _gridElements)
            Plot.Children.Remove(el);
        _gridElements.Clear();
        if (Plot.ActualWidth < 50 || Plot.ActualHeight < 50)
            return;
        if (!Plot.Children.Contains(_spectrumPolygon))
        {
            Plot.Children.Add(_spectrumPolygon);
            System.Windows.Controls.Panel.SetZIndex(_spectrumPolygon, 1);
        }

        var lineBrush = Brush(_palette.Grid);
        var zeroBrush = Brush(_palette.ZeroLine);
        var textBrush = Brush(_palette.AxisText);

        foreach (var f in GridFrequencies)
        {
            double x = XFromFreq(f);
            AddGridElement(new Line
            {
                X1 = x, X2 = x, Y1 = 0, Y2 = Plot.ActualHeight,
                Stroke = lineBrush, StrokeThickness = 1,
            });
            AddGridElement(Label(f >= 1000 ? $"{f / 1000:0.#}k" : $"{f:0}", x + 3, Plot.ActualHeight - 16, textBrush));
        }
        for (int db = -_dbRange; db <= _dbRange; db += _dbRange / (_dbRange == 30 ? 5 : 4))
        {
            double y = YFromDb(db);
            AddGridElement(new Line
            {
                X1 = 0, X2 = Plot.ActualWidth, Y1 = y, Y2 = y,
                Stroke = db == 0 ? zeroBrush : lineBrush, StrokeThickness = db == 0 ? 1.4 : 1,
            });
            // Skipped near the bottom edge, where the dB label would sit on top of the
            // frequency labels drawn along the axis.
            if (db != 0 && y < Plot.ActualHeight - 22)
                AddGridElement(Label($"{db:+0;-0} dB", 4, y - 15, textBrush));
        }
    }

    private void AddGridElement(UIElement element)
    {
        if (element is Shape s)
            s.IsHitTestVisible = false;
        _gridElements.Add(element);
        Plot.Children.Add(element);
        System.Windows.Controls.Panel.SetZIndex(element, 0);
    }

    private static TextBlock Label(string text, double x, double y, Brush brush)
    {
        var tb = new TextBlock { Text = text, Foreground = brush, FontSize = 10, IsHitTestVisible = false };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        return tb;
    }

    private void RedrawCurves()
    {
        foreach (var el in _curveElements)
            Plot.Children.Remove(el);
        _curveElements.Clear();
        _nodes.Clear();
        if (Plot.ActualWidth < 50 || Plot.ActualHeight < 50)
            return;

        var freqs = EqResponse.LogFrequencies(CurvePoints);
        double bypassOpacity = _eqEnabled ? 1.0 : 0.35; // visual A/B cue

        // Per-band faint curves.
        for (int i = 0; i < _bands.Count; i++)
        {
            var polyline = new Polyline
            {
                Stroke = Brush(i == _selectedBand ? _palette.BandSelectedFill : _palette.BandFill),
                StrokeThickness = 1,
                IsHitTestVisible = false,
                Opacity = bypassOpacity,
            };
            var response = EqResponse.ResponseDb(new[] { _bands[i] }, freqs);
            for (int p = 0; p < freqs.Length; p++)
                polyline.Points.Add(new Point(XFromFreq(freqs[p]), YFromDb(response[p])));
            AddCurveElement(polyline, 2);
        }

        // Summed bold curve.
        if (_bands.Count > 0)
        {
            var summed = new Polyline
            {
                Stroke = Brush(_palette.Curve),
                StrokeThickness = 2.4,
                IsHitTestVisible = false,
                Opacity = bypassOpacity,
            };
            var response = EqResponse.ResponseDb(_bands, freqs);
            for (int p = 0; p < freqs.Length; p++)
                summed.Points.Add(new Point(XFromFreq(freqs[p]), YFromDb(response[p])));
            AddCurveElement(summed, 2);
        }

        // Draggable nodes (index via Tag). Simple mode shows the curve read-only, so it draws
        // no targets at all rather than drawing dead ones.
        for (int i = 0; !SimpleMode && i < _bands.Count; i++)
        {
            var band = _bands[i];
            var node = new Ellipse
            {
                Width = 14, Height = 14,
                Fill = Brush(i == _selectedBand ? _palette.NodeSelected : _palette.Node),
                Stroke = Brush(_palette.NodeStroke),
                StrokeThickness = 1.2,
                Tag = i,
                Cursor = Cursors.SizeAll,
                Opacity = bypassOpacity,
                ToolTip = $"{band.Type} · {band.Fc:0.#} Hz · {band.GainDb:0.0} dB · Q {band.Q:0.00}",
            };
            Canvas.SetLeft(node, XFromFreq(band.Fc) - 7);
            Canvas.SetTop(node, YFromDb(band.HasGain ? band.GainDb : 0) - 7);
            _nodes.Add(node);
            AddCurveElement(node, 3);
        }
    }

    private void AddCurveElement(UIElement element, int z)
    {
        _curveElements.Add(element);
        Plot.Children.Add(element);
        System.Windows.Controls.Panel.SetZIndex(element, z);
    }

    // ---- Plot: interaction ----

    private int NodeIndexAt(object? source) =>
        source is Ellipse { Tag: int index } ? index : -1;

    private void OnPlotMouseDown(object sender, MouseButtonEventArgs e)
    {
        Plot.Focus();
        int index = NodeIndexAt(e.OriginalSource);
        if (e.ClickCount == 2)
        {
            if (index >= 0)
            {
                _selectedBand = index;
                RemoveSelectedBand();
            }
            else
            {
                var pos = e.GetPosition(Plot);
                if (!EqPreset.TryAppend(_bands, new EqBand(EqBandType.Peak,
                        FreqFromX(pos.X), Math.Round(DbFromY(pos.Y), 1), 1.0)))
                {
                    StripHintText.Text = $"Band limit reached ({EqPreset.MaxBands} per scope).";
                    e.Handled = true;
                    return;
                }
                _selectedBand = _bands.Count - 1;
                OnBandsEdited(countChanged: true);
            }
            e.Handled = true;
            return;
        }
        if (index >= 0)
        {
            _selectedBand = index;
            _draggingBand = index;
            Plot.CaptureMouse();
            RedrawCurves();
            SyncBandPanel();
            RefreshBandStripValues(); // selecting a node highlights its column
        }
    }

    private void OnPlotMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingBand < 0 || _draggingBand >= _bands.Count)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag(); // button released outside the window: stop tracking
            return;
        }
        var pos = e.GetPosition(Plot);
        var band = _bands[_draggingBand];
        double gain = band.HasGain ? Math.Round(DbFromY(pos.Y), 1) : 0;
        _bands[_draggingBand] = EqPreset.Clamp(band with
        {
            Fc = Math.Round(FreqFromX(pos.X), 1),
            GainDb = gain,
        });
        OnBandsEdited();
    }

    private void OnPlotMouseUp(object sender, MouseButtonEventArgs e) => EndDrag();

    /// <summary>Also wired to the canvas's LostMouseCapture: capture can be stolen (the
    /// right-click type menu, window deactivation, an Alt-Tab mid-drag), and without this the
    /// drag would stay "live" and keep rewriting the band on the next mouse move.</summary>
    private void OnPlotLostCapture(object sender, MouseEventArgs e) => EndDrag();

    private void EndDrag()
    {
        if (_draggingBand < 0)
            return;
        _draggingBand = -1;
        if (Plot.IsMouseCaptured)
            Plot.ReleaseMouseCapture();
    }

    private void OnPlotMouseWheel(object sender, MouseWheelEventArgs e)
    {
        int index = NodeIndexAt(e.OriginalSource);
        if (index < 0)
            index = _selectedBand; // wheel anywhere adjusts the selected band's Q
        if (index < 0 || index >= _bands.Count)
            return;
        var band = _bands[index];
        double factor = e.Delta > 0 ? 1.12 : 1 / 1.12;
        _selectedBand = index;
        _bands[index] = EqPreset.Clamp(band with { Q = band.Q * factor });
        OnBandsEdited();
        e.Handled = true;
    }

    private void OnPlotRightClick(object sender, MouseButtonEventArgs e)
    {
        int index = NodeIndexAt(e.OriginalSource);
        if (index < 0)
            return;
        _selectedBand = index;
        RedrawCurves();
        SyncBandPanel();
        RefreshBandStripValues();
        var menu = new ContextMenu();
        foreach (var type in Enum.GetValues<EqBandType>())
        {
            var item = new MenuItem
            {
                Header = type.ToString(),
                IsChecked = _bands[index].Type == type,
            };
            var chosen = type;
            item.Click += (_, _) =>
            {
                BandTypeCombo.SelectedItem = chosen; // funnels through OnBandTypeChanged
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
        e.Handled = true;
    }

    // ---- Meters + spectrum ----

    /// <summary>Capture thread: fold the block into the analysis ring and the meter block
    /// values. Kept tiny — the UI timer does all drawing.</summary>
    private void OnSamples(float[] left, float[] right)
    {
        double peakL = MeterMath.PeakDb(left), rmsL = MeterMath.RmsDb(left);
        double peakR = MeterMath.PeakDb(right), rmsR = MeterMath.RmsDb(right);
        lock (_meterLock)
        {
            _blockPeakL = Math.Max(_blockPeakL, peakL);
            _blockRmsL = Math.Max(_blockRmsL, rmsL);
            _blockPeakR = Math.Max(_blockPeakR, peakR);
            _blockRmsR = Math.Max(_blockRmsR, rmsR);
            _clip.Observe(Math.Max(peakL, peakR));
            for (int i = 0; i < left.Length; i++)
            {
                _ring[_ringPos] = (left[i] + right[i]) * 0.5f;
                _ringPos = (_ringPos + 1) % _ring.Length;
            }
        }
    }

    /// <summary>UI timer (~30 fps): meters, clip indicator, spectrum, readouts.</summary>
    private void OnFrame()
    {
        double peakL, rmsL, peakR, rmsR;
        bool clipLatched;
        int clipCount;
        var snapshot = new float[_ring.Length];
        lock (_meterLock)
        {
            peakL = _blockPeakL; rmsL = _blockRmsL;
            peakR = _blockPeakR; rmsR = _blockRmsR;
            _blockPeakL = _blockPeakR = _blockRmsL = _blockRmsR = MeterMath.FloorDb;
            clipLatched = _clip.Latched;
            clipCount = _clip.Count;
            // Unroll the ring so index 0 is the oldest sample.
            int head = _ringPos;
            Array.Copy(_ring, head, snapshot, 0, _ring.Length - head);
            Array.Copy(_ring, 0, snapshot, _ring.Length - head, head);
        }

        // Ballistics: instant attack, smooth release.
        _shownRmsL = Math.Max(rmsL, _shownRmsL - 2.5);
        _shownRmsR = Math.Max(rmsR, _shownRmsR - 2.5);
        _shownPeakL = Math.Max(peakL, _shownPeakL - 1.5);
        _shownPeakR = Math.Max(peakR, _shownPeakR - 1.5);

        DrawMeter(RmsBarL, PeakTickL, _shownRmsL, _shownPeakL);
        DrawMeter(RmsBarR, PeakTickR, _shownRmsR, _shownPeakR);
        LevelText.Text = _shownPeakL <= MeterMath.FloorDb && _shownPeakR <= MeterMath.FloorDb
            ? "silent"
            : $"peak {Math.Max(_shownPeakL, _shownPeakR):0.0} dBFS";

        ClipIndicator.Background = Brush(clipLatched ? _palette.ClipLatched : _palette.ClipIdle);
        ClipIndicator.Foreground = Brush(clipLatched ? _palette.ClipLatchedText : _palette.ClipIdleText);
        ClipCountText.Text = clipCount == 0 ? "" : $"clipped {clipCount}×";

        UpdateSpectrum(snapshot);
        UpdatePreampReadout();
    }

    private void DrawMeter(System.Windows.Shapes.Rectangle rms, System.Windows.Shapes.Rectangle peakTick,
        double rmsDb, double peakDb)
    {
        double height = ((FrameworkElement)rms.Parent).ActualHeight;
        if (height <= 0)
            return;
        double Scale(double db) => height * Math.Clamp((db - SpectrumBottomDb) / (SpectrumTopDb - SpectrumBottomDb), 0, 1);
        rms.Height = Scale(rmsDb);
        peakTick.Margin = new Thickness(0, 0, 0, Math.Min(Scale(peakDb), height - 2));
    }

    private void UpdateSpectrum(float[] samples)
    {
        if (_capture.SampleRate <= 0 || Plot.ActualWidth < 50)
        {
            _spectrumPolygon.Points.Clear();
            return;
        }
        var db = Fft.SpectrumDb(samples);
        int bandCount = Math.Max(32, (int)(Plot.ActualWidth / 8));
        var bands = Fft.LogBins(db, _capture.SampleRate, FMin, FMax, bandCount);

        var points = new PointCollection { new Point(0, Plot.ActualHeight) };
        for (int i = 0; i < bands.Length; i++)
        {
            double x = Plot.ActualWidth * (i + 0.5) / bands.Length;
            points.Add(new Point(x, YFromSpectrumDb(bands[i])));
        }
        points.Add(new Point(Plot.ActualWidth, Plot.ActualHeight));
        _spectrumPolygon.Points = points;
    }

    private void OnResetClip(object sender, RoutedEventArgs e)
    {
        lock (_meterLock)
        {
            _clip.Reset();
        }
    }
}
