using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ApoVolume.Core;
// WinForms is referenced app-wide (tray icon); pin the WPF types this window means.
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
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

namespace ApoVolume.UI;

/// <summary>The full parametric EQ editor: log-frequency response plot with draggable band
/// nodes, per-scope preset management (Global + one tab per render device), and live
/// post-EQ meters/spectrum from WASAPI loopback. The editor owns NO durable state — every
/// change is pushed immediately through <see cref="ScopeChanged"/> and read back from the
/// app's settings on scope switches, so the config file always reflects what's on screen.</summary>
public partial class EqEditorWindow : Window
{
    private const double FMin = 20, FMax = 20000;
    private const int CurvePoints = 240;
    private const int FftSize = 4096;
    private static readonly int[] DbRanges = { 12, 24, 30 };
    private static readonly double[] GridFrequencies = { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 };
    private const double SpectrumTopDb = 0, SpectrumBottomDb = -90;

    private readonly Func<Settings> _getSettings;
    private readonly Func<string?> _getActiveDeviceId;
    private readonly Func<string?, double?> _getVolumeDbFor; // per device id; null = no volume preamp (system mode / global scope)

    /// <summary>(deviceId, scope) — deviceId null means the Global scope. Raised on every
    /// edit; the app merges, re-renders the config file, and persists.</summary>
    public event Action<string?, EqScopeSetting>? ScopeChanged;

    // Working copy of the current scope (pushed on every mutation, reloaded on tab switch).
    private string? _scopeDeviceId;
    private List<EqBand> _bands = new();
    private string _presetName = "";
    private double _presetPreampDb;
    private bool _eqEnabled = true;

    private int _selectedBand = -1;
    private int _dbRange = 24;
    private bool _syncing;          // programmatic UI updates must not re-enter handlers
    private int _draggingBand = -1;

    // Plot elements (persistent between redraws where possible).
    private readonly Polygon _spectrumPolygon = new()
    {
        Fill = new SolidColorBrush(Color.FromArgb(48, 120, 200, 255)),
        IsHitTestVisible = false,
    };
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

    public EqEditorWindow(Func<Settings> getSettings, Func<string?> getActiveDeviceId,
        Func<string?, double?> getVolumeDbFor)
    {
        _getSettings = getSettings;
        _getActiveDeviceId = getActiveDeviceId;
        _getVolumeDbFor = getVolumeDbFor;
        InitializeComponent();

        BandTypeCombo.ItemsSource = Enum.GetValues<EqBandType>();
        _capture.SamplesAvailable += OnSamples;
        _frameTimer.Tick += (_, _) => OnFrame();

        PreviewKeyDown += OnWindowKeyDown;
        Loaded += (_, _) =>
        {
            PopulateScopeTabs();
            RefreshPresetList();
            _capture.Start();
            _frameTimer.Start();
        };
        Closed += (_, _) =>
        {
            _frameTimer.Stop();
            _capture.Dispose();
        };
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
        var current = _scopeDeviceId;
        _syncing = true;
        ScopeTabs.ItemsSource = tabs;
        var select = tabs.FirstOrDefault(t => t.DeviceId == current)
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
        LoadScope(tab.DeviceId);
    }

    private void LoadScope(string? deviceId)
    {
        _scopeDeviceId = deviceId;
        var settings = _getSettings();
        var scope = deviceId is null
            ? settings.GlobalEq
            : settings.DeviceEq is not null && settings.DeviceEq.TryGetValue(deviceId, out var s) ? s : null;
        _bands = scope?.Bands is { } bands ? bands.ToList() : new List<EqBand>();
        _presetName = scope?.PresetName ?? "";
        _presetPreampDb = scope?.PresetPreampDb ?? 0;
        _eqEnabled = scope?.Enabled ?? true;
        _selectedBand = _bands.Count > 0 ? 0 : -1;

        _syncing = true;
        EqEnabledCheck.IsChecked = _eqEnabled;
        _syncing = false;
        SyncPresetCombo();
        SyncBandPanel();
        RedrawAll();
    }

    private EqScopeSetting CurrentScope() =>
        new(_presetName, _presetPreampDb, _eqEnabled, _bands.ToArray());

    private void PushScope() => ScopeChanged?.Invoke(_scopeDeviceId, CurrentScope());

    /// <summary>Every band mutation goes through here: custom-marks the preset name,
    /// pushes to the app, and redraws.</summary>
    private void OnBandsEdited()
    {
        if (_presetName.Length > 0 && _presetName != "(custom)")
            _presetName = "(custom)";
        SyncPresetCombo();
        PushScope();
        RedrawCurves();
        SyncBandPanel();
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
        bool custom = _presetName.Length == 0 || _presetName == "(custom)"
            || !names.Contains(_presetName, StringComparer.OrdinalIgnoreCase);
        if (custom)
            names.Insert(0, "(custom)");
        PresetCombo.ItemsSource = names;
        PresetCombo.SelectedItem = custom
            ? "(custom)"
            : names.First(n => string.Equals(n, _presetName, StringComparison.OrdinalIgnoreCase));
        _syncing = false;
    }

    private void OnPresetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || PresetCombo.SelectedItem is not string name || name == "(custom)")
            return;
        if (PresetStore.Load(ApoPaths.GetPresetsRoot(), name) is not { } preset)
        {
            SyncPresetCombo(); // vanished on disk since listing
            return;
        }
        _presetName = preset.Name;
        _presetPreampDb = preset.PreampDb;
        _bands = preset.Bands.ToList();
        _selectedBand = _bands.Count > 0 ? 0 : -1;
        PushScope();
        SyncBandPanel();
        RedrawAll();
    }

    private void OnSavePreset(object sender, RoutedEventArgs e)
    {
        if (_presetName.Length == 0 || _presetName == "(custom)")
        {
            OnSavePresetAs(sender, e);
            return;
        }
        SavePresetNamed(_presetName);
    }

    private void OnSavePresetAs(object sender, RoutedEventArgs e)
    {
        var name = PromptForName("Save preset as", _presetName == "(custom)" ? "" : _presetName);
        if (name is null)
            return;
        if (PresetStore.ValidateName(name) is { } error)
        {
            MessageBox.Show(this, error, "apo-volume", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            MessageBox.Show(this, $"Couldn't save the preset: {ex.Message}", "apo-volume",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _presetName = name;
        PushScope();
        SyncPresetCombo();
    }

    private void OnDeletePreset(object sender, RoutedEventArgs e)
    {
        if (_presetName.Length == 0 || _presetName == "(custom)")
            return;
        if (MessageBox.Show(this, $"Delete preset '{_presetName}'?", "apo-volume",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        PresetStore.Delete(ApoPaths.GetPresetsRoot(), _presetName);
        _presetName = "(custom)"; // the chain stays audible; only the file is gone
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
            MessageBox.Show(this, $"Couldn't read the file: {ex.Message}", "apo-volume",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var name = PresetStore.SanitizeName(Path.GetFileNameWithoutExtension(dialog.FileName));
        var preset = EqPreset.Parse(name, text);
        if (preset.Bands.Count == 0)
        {
            MessageBox.Show(this, "That file doesn't contain any Equalizer APO filter lines.",
                "apo-volume", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            PresetStore.Save(ApoPaths.GetPresetsRoot(), name, text); // import is file copy
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, $"Couldn't import: {ex.Message}", "apo-volume",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ApplyPreset(preset);
    }

    private void OnAutoEq(object sender, RoutedEventArgs e)
    {
        var dialog = new AutoEqImportDialog { Owner = this };
        if (dialog.ShowDialog() == true && dialog.ImportedPreset is { } preset)
            ApplyPreset(preset);
    }

    /// <summary>A freshly imported/downloaded preset becomes the current scope's chain.</summary>
    private void ApplyPreset(EqPreset preset)
    {
        _presetName = preset.Name;
        _presetPreampDb = preset.PreampDb;
        _bands = preset.Bands.ToList();
        _selectedBand = _bands.Count > 0 ? 0 : -1;
        PushScope();
        SyncPresetCombo();
        SyncBandPanel();
        RedrawAll();
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
            FcBox.Text = band.Fc.ToString("0.##", CultureInfo.InvariantCulture);
            GainBox.Text = band.GainDb.ToString("0.0", CultureInfo.InvariantCulture);
            QBox.Text = band.Q.ToString("0.00", CultureInfo.InvariantCulture);
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
        var band = _bands[_selectedBand];
        // Gainless types zero the gain; Q jumps to the type's conventional default when
        // coming from a very different shape (a notch's Q 30 makes no sense on a shelf).
        double q = type == EqBandType.Notch ? EqPreset.DefaultNotchQ
            : band.Type == EqBandType.Notch ? EqPreset.DefaultQ
            : band.Q;
        _bands[_selectedBand] = EqPreset.Clamp(band with
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

    private void OnBandBoxCommit(object sender, RoutedEventArgs e)
    {
        if (_syncing || _selectedBand < 0)
            return;
        var band = _bands[_selectedBand];
        double fc = ParseOr(FcBox.Text, band.Fc);
        double gain = ParseOr(GainBox.Text, band.GainDb);
        double q = ParseOr(QBox.Text, band.Q);
        var updated = EqPreset.Clamp(band with { Fc = fc, GainDb = gain, Q = q });
        if (updated == band)
        {
            SyncBandPanel(); // normalize the text back
            return;
        }
        _bands[_selectedBand] = updated;
        OnBandsEdited();
    }

    private static double ParseOr(string text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
        && double.IsFinite(v) ? v : fallback;

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
        if (e.Key == Key.Delete && Keyboard.FocusedElement is not TextBox)
        {
            RemoveSelectedBand();
            e.Handled = true;
        }
    }

    private void RemoveSelectedBand()
    {
        if (_selectedBand < 0 || _selectedBand >= _bands.Count)
            return;
        _bands.RemoveAt(_selectedBand);
        _selectedBand = Math.Min(_selectedBand, _bands.Count - 1);
        OnBandsEdited();
    }

    // ---- Plot: coordinate mapping ----

    private double XFromFreq(double freq) =>
        Plot.ActualWidth * Math.Log(Math.Clamp(freq, FMin, FMax) / FMin) / Math.Log(FMax / FMin);

    private double FreqFromX(double x) =>
        FMin * Math.Exp(Math.Clamp(x, 0, Plot.ActualWidth) / Math.Max(Plot.ActualWidth, 1)
            * Math.Log(FMax / FMin));

    private double YFromDb(double db) =>
        Plot.ActualHeight / 2 - db * Plot.ActualHeight / (2.0 * _dbRange);

    private double DbFromY(double y) =>
        (Plot.ActualHeight / 2 - y) * 2.0 * _dbRange / Math.Max(Plot.ActualHeight, 1);

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

        var lineBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x38));
        var zeroBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x58));
        var textBrush = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x78));

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
            if (db != 0)
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
                Stroke = new SolidColorBrush(i == _selectedBand
                    ? Color.FromArgb(150, 255, 200, 90)
                    : Color.FromArgb(80, 140, 170, 255)),
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
                Stroke = new SolidColorBrush(Color.FromRgb(0x6F, 0xA8, 0xFF)),
                StrokeThickness = 2.4,
                IsHitTestVisible = false,
                Opacity = bypassOpacity,
            };
            var response = EqResponse.ResponseDb(_bands, freqs);
            for (int p = 0; p < freqs.Length; p++)
                summed.Points.Add(new Point(XFromFreq(freqs[p]), YFromDb(response[p])));
            AddCurveElement(summed, 2);
        }

        // Draggable nodes (index via Tag).
        for (int i = 0; i < _bands.Count; i++)
        {
            var band = _bands[i];
            var node = new Ellipse
            {
                Width = 14, Height = 14,
                Fill = new SolidColorBrush(i == _selectedBand
                    ? Color.FromRgb(0xFF, 0xC8, 0x5A)
                    : Color.FromRgb(0x6F, 0xA8, 0xFF)),
                Stroke = Brushes.White,
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
                _bands.Add(EqPreset.Clamp(new EqBand(EqBandType.Peak,
                    FreqFromX(pos.X), Math.Round(DbFromY(pos.Y), 1), 1.0)));
                _selectedBand = _bands.Count - 1;
                OnBandsEdited();
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

        ClipIndicator.Background = clipLatched ? Brushes.Firebrick : new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        ClipIndicator.Foreground = clipLatched ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77));
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
