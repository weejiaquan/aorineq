using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ApoVolume.Core;

namespace ApoVolume.UI;

/// <summary>Studio window for creating/editing skins: imports PNG/sprite-sheet/GIF layers,
/// previews the fill (animated, when layers are) with the exact composition the real
/// <see cref="SkinOsdWindow"/> uses, lets the user drag the percent number into place, shares
/// skins as zip files, and saves via <see cref="SkinWriter"/>. One instance per app (OnClosing
/// cancels-and-hides like SettingsWindow); App owns the lifetime.</summary>
public partial class SkinDesignerWindow : Window
{
    private readonly Func<Settings> _currentSettings; // live OSD settings, for the desktop test

    private string? _emptySource;
    private string? _fullSource;
    private SkinFrames? _emptyFrames;
    private SkinFrames? _fullFrames;
    private int _imgWidth;   // logical frame size
    private int _imgHeight;
    private string? _editingSkinName; // null = designing a new skin
    // True from construction (same pattern as SettingsWindow): sliders with an initial Value
    // raise ValueChanged DURING InitializeComponent, before sibling elements exist — the guard
    // must already be up. PopulateSkinList drops it once the window is fully built.
    private bool _initializing = true;
    private bool _draggingNumber;
    private SkinOsdWindow? _testOsd;
    private string? _testFolder;
    private readonly DispatcherTimer _emptyAnimTimer = new();
    private readonly DispatcherTimer _fullAnimTimer = new();
    private int _emptyFrameIndex;
    private int _fullFrameIndex;

    /// <summary>Raised after a successful save or zip import with the skin's name. App refreshes
    /// the Settings picker and hot-reloads the live OSD when the active skin was touched.</summary>
    public event Action<string>? SkinSaved;

    public SkinDesignerWindow(Func<Settings> currentSettings)
    {
        _currentSettings = currentSettings;
        InitializeComponent();
        _emptyAnimTimer.Tick += (_, _) => AdvanceFrame(isEmpty: true);
        _fullAnimTimer.Tick += (_, _) => AdvanceFrame(isEmpty: false);
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false)
            {
                _emptyAnimTimer.Stop();
                _fullAnimTimer.Stop();
            }
            else
            {
                RestartAnimationTimers();
            }
        };
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
        _emptySource = info.EmptyPath;
        _fullSource = info.FullPath;
        EmptyPathText.Text = info.EmptyPath;
        FullPathText.Text = info.FullPath;
        _initializing = true; // bulk control update must not re-enter OnControlChanged per control
        NameBox.Text = info.Name;
        ShowNumberCheck.IsChecked = info.Text is { Show: true };
        NumberXBox.Text = (info.Text?.X ?? 10).ToString();
        NumberYBox.Text = (info.Text?.Y ?? 5).ToString();
        ScaleSlider.Value = info.Scale;
        FpsBox.Text = info.Fps.ToString("0.##");
        EmptyFramesBox.Text = info.EmptyFrames.ToString();
        FullFramesBox.Text = info.FullFrames.ToString();
        _initializing = false;
        ReloadPreviewData();
        StatusText.Text = $"Editing '{info.Name}'. Change the name before saving to create a copy.";
    }

    private void ClearEditor()
    {
        _editingSkinName = null;
        _emptySource = null;
        _fullSource = null;
        _emptyFrames = null;
        _fullFrames = null;
        _initializing = true;
        NameBox.Text = "";
        FpsBox.Text = "10";
        EmptyFramesBox.Text = "1";
        FullFramesBox.Text = "1";
        _initializing = false;
        EmptyPathText.Text = "—";
        FullPathText.Text = "—";
        ImageErrorText.Text = "";
        RestartAnimationTimers(); // no frames -> stops both
        RefreshPreview();
        Validate();
        StatusText.Text = "Pick two images to start a new skin.";
    }

    private void OnBrowseEmpty(object sender, RoutedEventArgs e) => Browse(isEmpty: true);
    private void OnBrowseFull(object sender, RoutedEventArgs e) => Browse(isEmpty: false);

    private void Browse(bool isEmpty)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Images (*.png;*.gif)|*.png;*.gif",
            Title = isEmpty ? "Choose the empty layer (0% artwork)" : "Choose the full layer (100% artwork)",
        };
        if (dialog.ShowDialog(this) != true) return;
        if (isEmpty) { _emptySource = dialog.FileName; EmptyPathText.Text = dialog.FileName; EmptyPathText.ToolTip = dialog.FileName; }
        else { _fullSource = dialog.FileName; FullPathText.Text = dialog.FileName; FullPathText.ToolTip = dialog.FileName; }
        ReloadPreviewData();
    }

    private void OnImportEmptyFrames(object sender, RoutedEventArgs e) => ImportFrames(isEmpty: true);
    private void OnImportFullFrames(object sender, RoutedEventArgs e) => ImportFrames(isEmpty: false);

    /// <summary>Builds a sprite sheet from a numbered PNG frame sequence (the Photoshop
    /// "Export Layers to Files" workflow): frames are sorted by filename, must share dimensions,
    /// get stacked vertically into a scratch PNG that becomes the layer source, and the layer's
    /// frame count is filled in automatically.</summary>
    private void ImportFrames(bool isEmpty)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "PNG frames (*.png)|*.png",
            Multiselect = true,
            Title = "Select the frames, in order (sorted by filename)",
        };
        if (dialog.ShowDialog(this) != true || dialog.FileNames.Length == 0) return;

        var files = dialog.FileNames.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        try
        {
            (int Width, int Height)? size = null;
            foreach (var file in files)
            {
                var s = PngHeader.Read(file);
                if (s is null)
                {
                    ImageErrorText.Text = Path.GetFileName(file) + " is not a valid PNG.";
                    return;
                }
                if (size is not null && s != size)
                {
                    ImageErrorText.Text =
                        $"{Path.GetFileName(file)} is {s.Value.Width}×{s.Value.Height} but earlier frames are " +
                        $"{size.Value.Width}×{size.Value.Height}. All frames must share dimensions.";
                    return;
                }
                size ??= s;
            }

            var (w, h) = size!.Value;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                for (int i = 0; i < files.Length; i++)
                    dc.DrawImage(SkinOsdWindow.LoadBitmap(files[i]), new Rect(0, i * h, w, h));
            }
            var sheet = new RenderTargetBitmap(w, h * files.Length, 96, 96, PixelFormats.Pbgra32);
            sheet.Render(visual);
            var scratchDir = Path.Combine(Path.GetTempPath(), "apo-volume-designer");
            Directory.CreateDirectory(scratchDir);
            var sheetPath = Path.Combine(scratchDir, Guid.NewGuid().ToString("N") + ".png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(sheet));
            using (var fs = File.Create(sheetPath))
                encoder.Save(fs);

            _initializing = true;
            if (isEmpty)
            {
                _emptySource = sheetPath;
                EmptyPathText.Text = $"{files.Length} frames → sheet";
                EmptyPathText.ToolTip = sheetPath;
                EmptyFramesBox.Text = files.Length.ToString();
            }
            else
            {
                _fullSource = sheetPath;
                FullPathText.Text = $"{files.Length} frames → sheet";
                FullPathText.ToolTip = sheetPath;
                FullFramesBox.Text = files.Length.ToString();
            }
            _initializing = false;
            ReloadPreviewData();
            StatusText.Text = $"Assembled {files.Length} frames into a sprite sheet.";
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException
            or FileFormatException or ArgumentException)
        {
            ImageErrorText.Text = "Frame import failed: " + ex.Message;
        }
    }

    /// <summary>Re-derives everything the preview needs from the current sources and animation
    /// fields: per-layer header validation (PNG or GIF), logical-frame-size equality, decoded
    /// frame lists. All errors land in ImageErrorText; Save/Test stay disabled until clean.</summary>
    private void ReloadPreviewData()
    {
        _emptyFrames = null;
        _fullFrames = null;
        RestartAnimationTimers(); // stale timers from the previous skin must not keep ticking

        var emptyMeta = ReadLayerMeta(_emptySource, ParseFrames(EmptyFramesBox));
        var fullMeta = ReadLayerMeta(_fullSource, ParseFrames(FullFramesBox));
        if (emptyMeta.Error is not null || fullMeta.Error is not null)
        {
            ImageErrorText.Text = emptyMeta.Error ?? fullMeta.Error!;
            RefreshPreview();
            Validate();
            return;
        }
        if (emptyMeta.Size is null || fullMeta.Size is null)
        {
            ImageErrorText.Text = ""; // one or both layers simply not chosen yet
            RefreshPreview();
            Validate();
            return;
        }
        if (emptyMeta.Size != fullMeta.Size)
        {
            ImageErrorText.Text =
                $"Frame-size mismatch: empty is {emptyMeta.Size.Value.Width}×{emptyMeta.Size.Value.Height}, " +
                $"full is {fullMeta.Size.Value.Width}×{fullMeta.Size.Value.Height}. They must be identical.";
            RefreshPreview();
            Validate();
            return;
        }

        try
        {
            double fps = ParseFps();
            _emptyFrames = SkinFrames.Load(_emptySource!, ParseFrames(EmptyFramesBox), fps);
            _fullFrames = SkinFrames.Load(_fullSource!, ParseFrames(FullFramesBox), fps);
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException
            or FileFormatException or ArgumentException or OutOfMemoryException)
        {
            // OutOfMemoryException: untrusted shared skins — a decode blowing the budget must
            // surface as an error, not take the app down.
            _emptyFrames = null;
            _fullFrames = null;
            ImageErrorText.Text = "Image failed to decode: " + ex.Message;
            RefreshPreview();
            Validate();
            return;
        }

        ImageErrorText.Text = "";
        _imgWidth = emptyMeta.Size.Value.Width;
        _imgHeight = emptyMeta.Size.Value.Height;
        _emptyFrameIndex = 0;
        _fullFrameIndex = 0;
        // A GIF layer's frame count is the file's own; reflect it read-only in the box.
        _initializing = true;
        if (IsGif(_emptySource)) EmptyFramesBox.Text = _emptyFrames.Frames.Count.ToString();
        if (IsGif(_fullSource)) FullFramesBox.Text = _fullFrames.Frames.Count.ToString();
        EmptyFramesBox.IsEnabled = !IsGif(_emptySource);
        FullFramesBox.IsEnabled = !IsGif(_fullSource);
        _initializing = false;
        RefreshPreview();
        RestartAnimationTimers();
        Validate();
    }

    private static bool IsGif(string? path) =>
        path is not null && Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase);

    /// <summary>Header-level metadata for one layer: logical frame size, or an error.</summary>
    private static ((int Width, int Height)? Size, string? Error) ReadLayerMeta(string? path, int declaredFrames)
    {
        if (path is null) return (null, null);
        if (IsGif(path))
        {
            var size = GifHeader.Read(path);
            return size is null
                ? (null, Path.GetFileName(path) + " is not a valid GIF.")
                : (size, null);
        }
        var pngSize = PngHeader.Read(path);
        if (pngSize is null) return (null, Path.GetFileName(path) + " is not a valid PNG.");
        if (pngSize.Value.Height % declaredFrames != 0)
            return (null, $"{Path.GetFileName(path)} height {pngSize.Value.Height} is not divisible by {declaredFrames} frames.");
        return ((pngSize.Value.Width, pngSize.Value.Height / declaredFrames), null);
    }

    private static int ParseFrames(System.Windows.Controls.TextBox box) =>
        int.TryParse(box.Text, out var n) && n >= 1 ? n : 1;

    private double ParseFps() =>
        double.TryParse(FpsBox.Text, out var f) ? Math.Clamp(f, 1.0, 60.0) : 10.0;

    private void AdvanceFrame(bool isEmpty)
    {
        if (isEmpty)
        {
            if (_emptyFrames is not { IsAnimated: true }) return;
            _emptyFrameIndex = (_emptyFrameIndex + 1) % _emptyFrames.Frames.Count;
            EmptyImage.Source = _emptyFrames.Frames[_emptyFrameIndex];
            _emptyAnimTimer.Interval = _emptyFrames.Delays[_emptyFrameIndex];
        }
        else
        {
            if (_fullFrames is not { IsAnimated: true }) return;
            _fullFrameIndex = (_fullFrameIndex + 1) % _fullFrames.Frames.Count;
            FullImage.Source = _fullFrames.Frames[_fullFrameIndex];
            _fullAnimTimer.Interval = _fullFrames.Delays[_fullFrameIndex];
        }
    }

    private void RestartAnimationTimers()
    {
        _emptyAnimTimer.Stop();
        _fullAnimTimer.Stop();
        if (!IsVisible) return;
        if (_emptyFrames is { IsAnimated: true })
        {
            _emptyAnimTimer.Interval = _emptyFrames.Delays[_emptyFrameIndex];
            _emptyAnimTimer.Start();
        }
        if (_fullFrames is { IsAnimated: true })
        {
            _fullAnimTimer.Interval = _fullFrames.Delays[_fullFrameIndex];
            _fullAnimTimer.Start();
        }
    }

    /// <summary>Single change handler for every editor control: keeps labels in sync and
    /// re-renders. Frame/fps fields require re-decoding, everything else only re-composes.</summary>
    private void OnControlChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        FillLabel.Text = $"{(int)FillSlider.Value}%";
        ScaleLabel.Text = ScaleSlider.Value.ToString("0.00");
        if (ReferenceEquals(sender, FpsBox) || ReferenceEquals(sender, EmptyFramesBox)
            || ReferenceEquals(sender, FullFramesBox))
        {
            ReloadPreviewData();
            return;
        }
        RefreshPreview();
        Validate();
    }

    private void OnNumberBoxKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnControlChanged(sender, e);
    }

    /// <summary>Re-composes the preview exactly like SkinOsdWindow.ShowVolume: sizes at
    /// width*scale, clips full to SkinMath.FillWidth, dims for mute, places the number
    /// at (x*scale, y*scale).</summary>
    private void RefreshPreview()
    {
        if (_emptyFrames is null || _fullFrames is null)
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
        EmptyImage.Source = _emptyFrames.Frames[_emptyFrameIndex % _emptyFrames.Frames.Count];
        FullImage.Source = _fullFrames.Frames[_fullFrameIndex % _fullFrames.Frames.Count];

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

    /// <summary>Save/Test require both layers decoded and a valid name; Export requires a saved skin.</summary>
    private void Validate()
    {
        bool imagesOk = _emptyFrames is not null && _fullFrames is not null;
        string? nameError = SkinWriter.ValidateName(NameBox.Text);
        SaveButton.IsEnabled = imagesOk && nameError is null;
        TestButton.IsEnabled = imagesOk;
        ExportZipButton.IsEnabled = _editingSkinName is not null;
    }

    private SkinConfig CurrentConfig()
    {
        SkinText? text = ShowNumberCheck.IsChecked == true
            ? new SkinText(true,
                int.TryParse(NumberXBox.Text, out var x) ? x : 0,
                int.TryParse(NumberYBox.Text, out var y) ? y : 0)
            : null;
        // GIF layers self-describe; recording 1 keeps skin.json meaningful for the loader.
        return new SkinConfig(text, ScaleSlider.Value, ParseFps(),
            IsGif(_emptySource) ? 1 : ParseFrames(EmptyFramesBox),
            IsGif(_fullSource) ? 1 : ParseFrames(FullFramesBox));
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

        try
        {
            CleanupTestOsd();
            var folder = SkinWriter.Save(ApoPaths.GetSkinsRoot(), name, _emptySource, _fullSource,
                CurrentConfig());
            // Adopt the saved copies as the working sources (keeping each source's extension —
            // a GIF layer saved as empty.gif), so further edits are in-place.
            _emptySource = Path.Combine(folder, "empty" + (IsGif(_emptySource) ? ".gif" : ".png"));
            _fullSource = Path.Combine(folder, "full" + (IsGif(_fullSource) ? ".gif" : ".png"));
            EmptyPathText.Text = _emptySource;
            FullPathText.Text = _fullSource;
            _editingSkinName = name;
            PopulateSkinList(selectName: name);
            StatusText.Text = $"Saved '{name}'.";
            Validate();
            SkinSaved?.Invoke(name);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            StatusText.Text = ex.Message;
        }
    }

    /// <summary>Imports a shared skin zip: name prefilled from the zip filename, collision
    /// confirmed, then the imported skin opens in the editor and the app is notified.</summary>
    private void OnImportZip(object sender, RoutedEventArgs e)
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
                StatusText.Text = $"Zip filename can't be used as the skin name: {nameError}";
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
            PopulateSkinList(selectName: null);
            // Selecting the item runs the normal skin-selection path, loading it into the editor.
            foreach (ComboBoxItem item in SkinSelect.Items)
            {
                if ((string?)item.Tag == name) { SkinSelect.SelectedItem = item; break; }
            }
            StatusText.Text = $"Imported '{name}'.";
            SkinSaved?.Invoke(name);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            StatusText.Text = ex.Message;
        }
    }

    /// <summary>Exports the loaded skin's on-disk state (save first to include unsaved edits).</summary>
    private void OnExportZip(object sender, RoutedEventArgs e)
    {
        if (_editingSkinName is null) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Skin zip (*.zip)|*.zip",
            FileName = _editingSkinName + ".zip",
            Title = "Export skin as zip",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            SkinArchive.Export(Path.Combine(ApoPaths.GetSkinsRoot(), _editingSkinName), dialog.FileName);
            StatusText.Text = $"Exported '{_editingSkinName}' to {dialog.FileName}. " +
                "(Exports the last saved state — Save first to include current edits.)";
        }
        catch (InvalidOperationException ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    /// <summary>Writes the draft to a temp skin folder and shows it in a REAL SkinOsdWindow with
    /// the user's current OSD settings — true rendering (including animation) plus real per-pixel
    /// click-through and drag/wheel. Interacting with the test OSD drives the designer's fill
    /// slider. The previous test window/folder is torn down on re-test, save, and hide.</summary>
    private void OnTestOnDesktop(object sender, RoutedEventArgs e)
    {
        if (_emptySource is null || _fullSource is null) return;
        CleanupTestOsd();
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "apo-volume-skin-preview");
            var name = Guid.NewGuid().ToString("N");
            var folder = SkinWriter.Save(root, name, _emptySource, _fullSource, CurrentConfig());
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
            or IOException or NotSupportedException or FileFormatException or OutOfMemoryException)
        {
            // Imaging exceptions: corrupt file passed the header check but failed to decode
            // inside the SkinOsdWindow constructor — same containment policy as App.ApplyOsdConfig.
            // OutOfMemoryException: same untrusted-input policy as the preview decode path.
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
