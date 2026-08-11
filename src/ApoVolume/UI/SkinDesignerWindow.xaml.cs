using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ApoVolume.Core;

namespace ApoVolume.UI;

/// <summary>Studio window for creating/editing skins: imports empty/full PNGs, previews the fill
/// with the exact composition the real <see cref="SkinOsdWindow"/> uses, lets the user drag the
/// percent number into place, and saves via <see cref="SkinWriter"/>. One instance per app
/// (OnClosing cancels-and-hides like SettingsWindow); App owns the lifetime.</summary>
public partial class SkinDesignerWindow : Window
{
    private readonly Func<Settings> _currentSettings; // live OSD settings, for the desktop test

    private string? _emptySource;
    private string? _fullSource;
    private BitmapImage? _emptyBitmap;
    private BitmapImage? _fullBitmap;
    private int _imgWidth;
    private int _imgHeight;
    private string? _editingSkinName; // null = designing a new skin
    private bool _initializing;
    private bool _draggingNumber;
    private SkinOsdWindow? _testOsd;
    private string? _testFolder;

    /// <summary>Raised after a successful save with the saved skin's name. App refreshes the
    /// Settings picker and hot-reloads the live OSD when the active skin was edited.</summary>
    public event Action<string>? SkinSaved;

    public SkinDesignerWindow(Func<Settings> currentSettings)
    {
        _currentSettings = currentSettings;
        InitializeComponent();
        PopulateSkinList(selectName: null);
        Validate();
    }

    /// <summary>Rebuilds the skin dropdown: "New skin…" (Tag null) + every scanned folder
    /// (invalid ones disabled with the error as tooltip, same policy as the Settings picker).</summary>
    private void PopulateSkinList(string? selectName)
    {
        _initializing = true;
        try
        {
            SkinSelect.Items.Clear();
            SkinSelect.Items.Add(new ComboBoxItem { Content = "✱ New skin…", Tag = null });
            foreach (var skin in SkinLoader.Scan(ApoPaths.GetSkinsRoot()))
            {
                var item = new ComboBoxItem { Content = skin.Name, Tag = skin.Name, IsEnabled = skin.IsValid };
                if (!skin.IsValid) item.ToolTip = skin.Error;
                SkinSelect.Items.Add(item);
            }
            SkinSelect.SelectedIndex = 0;
            if (selectName is not null)
            {
                foreach (ComboBoxItem item in SkinSelect.Items)
                {
                    if ((string?)item.Tag == selectName) { SkinSelect.SelectedItem = item; break; }
                }
            }
        }
        finally
        {
            _initializing = false;
        }
    }

    private void OnSkinSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        var name = (string?)(SkinSelect.SelectedItem as ComboBoxItem)?.Tag;
        if (name is null)
        {
            ClearEditor();
            return;
        }
        var info = SkinLoader.Load(Path.Combine(ApoPaths.GetSkinsRoot(), name));
        if (!info.IsValid)
        {
            StatusText.Text = info.Error;
            return;
        }
        _editingSkinName = info.Name;
        NameBox.Text = info.Name;
        SetImage(isEmpty: true, info.EmptyPath);
        SetImage(isEmpty: false, info.FullPath);
        _initializing = true; // bulk control update must not re-enter OnControlChanged per control
        ShowNumberCheck.IsChecked = info.Text is { Show: true };
        NumberXBox.Text = (info.Text?.X ?? 10).ToString();
        NumberYBox.Text = (info.Text?.Y ?? 5).ToString();
        ScaleSlider.Value = info.Scale;
        _initializing = false;
        RefreshPreview();
        Validate();
        StatusText.Text = $"Editing '{info.Name}'. Change the name before saving to create a copy.";
    }

    private void ClearEditor()
    {
        _editingSkinName = null;
        _emptySource = null;
        _fullSource = null;
        _emptyBitmap = null;
        _fullBitmap = null;
        NameBox.Text = "";
        EmptyPathText.Text = "—";
        FullPathText.Text = "—";
        ImageErrorText.Text = "";
        RefreshPreview();
        Validate();
        StatusText.Text = "Pick two PNGs to start a new skin.";
    }

    private void OnBrowseEmpty(object sender, RoutedEventArgs e) => Browse(isEmpty: true);
    private void OnBrowseFull(object sender, RoutedEventArgs e) => Browse(isEmpty: false);

    private void Browse(bool isEmpty)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "PNG images (*.png)|*.png",
            Title = isEmpty ? "Choose empty.png (0% artwork)" : "Choose full.png (100% artwork)",
        };
        if (dialog.ShowDialog(this) != true) return;
        SetImage(isEmpty, dialog.FileName);
        RefreshPreview();
        Validate();
    }

    /// <summary>Validates and adopts one source image. Both images must be real PNGs with
    /// matching dimensions; violations surface in ImageErrorText and disable Save/Test.</summary>
    private void SetImage(bool isEmpty, string path)
    {
        var size = PngHeader.Read(path);
        if (size is null)
        {
            ImageErrorText.Text = Path.GetFileName(path) + " is not a valid PNG.";
            return;
        }
        if (isEmpty) { _emptySource = path; EmptyPathText.Text = path; EmptyPathText.ToolTip = path; }
        else { _fullSource = path; FullPathText.Text = path; FullPathText.ToolTip = path; }

        var emptySize = _emptySource is null ? null : PngHeader.Read(_emptySource);
        var fullSize = _fullSource is null ? null : PngHeader.Read(_fullSource);
        if (emptySize is not null && fullSize is not null && emptySize != fullSize)
        {
            ImageErrorText.Text =
                $"Dimension mismatch: empty is {emptySize.Value.Width}×{emptySize.Value.Height}, " +
                $"full is {fullSize.Value.Width}×{fullSize.Value.Height}. They must be identical.";
            _emptyBitmap = null;
            _fullBitmap = null;
            return;
        }
        ImageErrorText.Text = "";
        if (emptySize is not null && fullSize is not null)
        {
            // A truncated/corrupt PNG can pass the header check yet throw from WPF's decoder —
            // same containment policy as App.ApplyOsdConfig: surface, don't crash the designer.
            try
            {
                _emptyBitmap = SkinOsdWindow.LoadBitmap(_emptySource!);
                _fullBitmap = SkinOsdWindow.LoadBitmap(_fullSource!);
            }
            catch (Exception ex) when (ex is NotSupportedException or IOException
                or FileFormatException or ArgumentException) // FileFormatException: System.IO (WindowsBase)
            {
                _emptyBitmap = null;
                _fullBitmap = null;
                ImageErrorText.Text = "Image failed to decode: " + ex.Message;
                return;
            }
            _imgWidth = emptySize.Value.Width;
            _imgHeight = emptySize.Value.Height;
        }
    }

    /// <summary>Single change handler for every editor control: keeps labels in sync and
    /// re-renders. Safe to call for any control because the preview reads all state fresh.</summary>
    private void OnControlChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        FillLabel.Text = $"{(int)FillSlider.Value}%";
        ScaleLabel.Text = ScaleSlider.Value.ToString("0.00");
        RefreshPreview();
        Validate();
    }

    private void OnNumberBoxKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnControlChanged(sender, e);
    }

    /// <summary>Re-composes the preview exactly like SkinOsdWindow.ShowVolume: sizes at
    /// width*scale, clips full.png to SkinMath.FillWidth, dims for mute, places the number
    /// at (x*scale, y*scale).</summary>
    private void RefreshPreview()
    {
        if (_emptyBitmap is null || _fullBitmap is null)
        {
            PreviewCanvas.Visibility = Visibility.Collapsed;
            return;
        }
        PreviewCanvas.Visibility = Visibility.Visible;

        double scale = ScaleSlider.Value;
        double w = _imgWidth * scale;
        double h = _imgHeight * scale;
        PreviewCanvas.Width = w;
        PreviewCanvas.Height = h;
        EmptyImage.Source = _emptyBitmap;
        FullImage.Source = _fullBitmap;

        int percent = (int)FillSlider.Value;
        bool muted = MuteCheck.IsChecked == true;
        FillClip.Rect = new Rect(0, 0, SkinMath.FillWidth(_imgWidth, percent) * scale, h);
        FullImage.Visibility = muted ? Visibility.Hidden : Visibility.Visible;
        EmptyImage.Opacity = muted ? 0.6 : 1.0;

        bool showNumber = ShowNumberCheck.IsChecked == true;
        PercentTextBlock.Visibility = showNumber ? Visibility.Visible : Visibility.Collapsed;
        if (showNumber)
        {
            PercentTextBlock.Text = percent.ToString();
            int x = int.TryParse(NumberXBox.Text, out var px) ? px : 0;
            int y = int.TryParse(NumberYBox.Text, out var py) ? py : 0;
            PercentTextBlock.Margin = new Thickness(x * scale, y * scale, 0, 0);
        }
    }

    /// <summary>Save/Test require both images, matching dimensions, and a valid name.</summary>
    private void Validate()
    {
        bool imagesOk = _emptyBitmap is not null && _fullBitmap is not null;
        string? nameError = SkinWriter.ValidateName(NameBox.Text);
        SaveButton.IsEnabled = imagesOk && nameError is null;
        TestButton.IsEnabled = imagesOk;
    }

    // ----- number dragging: grab the percent text anywhere on the preview and drop it; the
    // X/Y boxes track live. Coordinates are stored in image pixels (divide by scale). -----

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ShowNumberCheck.IsChecked != true || PercentTextBlock.Visibility != Visibility.Visible)
            return;
        // Only start a drag when the press lands on the number itself.
        var posOnText = e.GetPosition(PercentTextBlock);
        if (posOnText.X < 0 || posOnText.Y < 0 ||
            posOnText.X > PercentTextBlock.ActualWidth || posOnText.Y > PercentTextBlock.ActualHeight)
            return;
        _draggingNumber = PreviewCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_draggingNumber || e.LeftButton != MouseButtonState.Pressed) return;
        double scale = ScaleSlider.Value;
        var pos = e.GetPosition(PreviewCanvas);
        // Center the text on the cursor; clamp so the number stays inside the artwork.
        int x = (int)Math.Round(pos.X / scale - PercentTextBlock.ActualWidth / (2 * scale));
        int y = (int)Math.Round(pos.Y / scale - PercentTextBlock.ActualHeight / (2 * scale));
        x = Math.Clamp(x, 0, Math.Max(0, _imgWidth - (int)(PercentTextBlock.ActualWidth / scale)));
        y = Math.Clamp(y, 0, Math.Max(0, _imgHeight - (int)(PercentTextBlock.ActualHeight / scale)));
        _initializing = true; // box updates must not trigger OnControlChanged re-entry
        NumberXBox.Text = x.ToString();
        NumberYBox.Text = y.ToString();
        _initializing = false;
        RefreshPreview();
    }

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingNumber) return;
        _draggingNumber = false;
        PreviewCanvas.ReleaseMouseCapture();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_emptySource is null || _fullSource is null) return;
        var name = NameBox.Text.Trim();

        // Saving under a different existing skin's name overwrites it — confirm first.
        // Re-saving the skin being edited is a plain save, no prompt.
        var targetFolder = Path.Combine(ApoPaths.GetSkinsRoot(), name);
        if (!string.Equals(name, _editingSkinName, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(targetFolder))
        {
            var choice = System.Windows.MessageBox.Show(
                $"A skin named '{name}' already exists. Overwrite it?",
                "apo-volume", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (choice != MessageBoxResult.Yes) return;
        }

        SkinText? text = ShowNumberCheck.IsChecked == true
            ? new SkinText(true,
                int.TryParse(NumberXBox.Text, out var x) ? x : 0,
                int.TryParse(NumberYBox.Text, out var y) ? y : 0)
            : null;
        try
        {
            CleanupTestOsd();
            var folder = SkinWriter.Save(ApoPaths.GetSkinsRoot(), name, _emptySource, _fullSource,
                text, ScaleSlider.Value);
            // Adopt the saved copies as the working sources, so further edits are in-place.
            _emptySource = Path.Combine(folder, "empty.png");
            _fullSource = Path.Combine(folder, "full.png");
            EmptyPathText.Text = _emptySource;
            FullPathText.Text = _fullSource;
            _editingSkinName = name;
            PopulateSkinList(selectName: name);
            StatusText.Text = $"Saved '{name}'.";
            SkinSaved?.Invoke(name);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            StatusText.Text = ex.Message;
        }
    }

    /// <summary>Writes the draft to a temp skin folder and shows it in a REAL SkinOsdWindow with
    /// the user's current OSD settings — true rendering plus real per-pixel click-through and
    /// drag/wheel. Interacting with the test OSD drives the designer's fill slider (and nothing
    /// app-side). The previous test window/folder is torn down on re-test, save, and hide.</summary>
    private void OnTestOnDesktop(object sender, RoutedEventArgs e)
    {
        if (_emptySource is null || _fullSource is null) return;
        CleanupTestOsd();
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "apo-volume-skin-preview");
            var name = Guid.NewGuid().ToString("N");
            var folder = SkinWriter.Save(root, name, _emptySource, _fullSource,
                ShowNumberCheck.IsChecked == true
                    ? new SkinText(true,
                        int.TryParse(NumberXBox.Text, out var x) ? x : 0,
                        int.TryParse(NumberYBox.Text, out var y) ? y : 0)
                    : null,
                ScaleSlider.Value);
            var info = SkinLoader.Load(folder);
            if (!info.IsValid)
            {
                StatusText.Text = info.Error;
                return;
            }
            _testFolder = folder;
            _testOsd = new SkinOsdWindow(info);
            _testOsd.ApplyConfig(_currentSettings());
            _testOsd.PercentChangedByUser += p => FillSlider.Value = p; // studio loop: OSD -> slider
            _testOsd.ShowVolume((int)FillSlider.Value, MuteCheck.IsChecked == true, interactive: true);
            StatusText.Text = "Showing on desktop — click/drag/scroll it like the real OSD.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException
            or IOException or NotSupportedException)
        {
            // NotSupportedException etc.: BitmapImage decode of a corrupt PNG inside the
            // SkinOsdWindow constructor — same containment policy as App.ApplyOsdConfig.
            StatusText.Text = ex.Message;
        }
    }

    private void CleanupTestOsd()
    {
        _testOsd?.Close();
        _testOsd = null;
        if (_testFolder is not null)
        {
            try { Directory.Delete(_testFolder, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            _testFolder = null;
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true; // App owns lifetime; hide like SettingsWindow
        CleanupTestOsd();
        Hide();
    }
}
