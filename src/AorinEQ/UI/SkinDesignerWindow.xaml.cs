using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AorinEQ.Core;

namespace AorinEQ.UI;

/// <summary>Studio window for creating/editing skins: imports PNG/sprite-sheet/GIF layers,
/// previews the fill (animated, when layers are) with the exact composition the real
/// <see cref="SkinOsdWindow"/> uses, lets the user drag the percent number into place, shares
/// skins as zip files, and saves via <see cref="SkinWriter"/>. One instance per app (OnClosing
/// cancels-and-hides like SettingsWindow); App owns the lifetime.</summary>
public partial class SkinDesignerWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly Func<Settings> _currentSettings; // live OSD settings, for the desktop test

    private string? _emptySource;
    private string? _fullSource;
    private string? _mutedSource; // optional muted artwork; null = dim-the-empty-layer fallback
    private SkinFrames? _emptyFrames;
    private SkinFrames? _fullFrames;
    private SkinFrames? _mutedFrames;
    private int _imgWidth;   // logical frame size
    private int _imgHeight;
    private double _lastTextWidth; // measured percent-text width (scale-multiplied), for align/drag math
    private string? _editingSkinName; // null = designing a new skin
    // True from construction (same pattern as SettingsWindow): sliders with an initial Value
    // raise ValueChanged DURING InitializeComponent, before sibling elements exist — the guard
    // must already be up. PopulateSkinList drops it once the window is fully built.
    private bool _initializing = true;

    private enum DragTarget { None, Number, RangeStart, RangeEnd }
    private enum PreviewLayer { Empty, Full, Muted }
    private DragTarget _dragging = DragTarget.None;
    private SkinOsdWindow? _testOsd;
    private string? _testFolder;
    private readonly DispatcherTimer _emptyAnimTimer = new();
    private readonly DispatcherTimer _fullAnimTimer = new();
    private readonly DispatcherTimer _mutedAnimTimer = new();
    private int _emptyFrameIndex;
    private int _fullFrameIndex;
    private int _mutedFrameIndex;

    // Text-style colors kept as hex strings; the swatch buttons show them and the color picker
    // edits them. Text color always set; outline/shadow null = that effect off.
    private string _textColor = "#FFFFFFFF";
    private string _outlineColor = "#FF000000";
    private string _shadowColor = "#FF000000";

    /// <summary>Raised after a successful save or zip import with the skin's name. App refreshes
    /// the Settings picker and hot-reloads the live OSD when the active skin was touched.</summary>
    public event Action<string>? SkinSaved;

    public SkinDesignerWindow(Func<Settings> currentSettings)
    {
        _currentSettings = currentSettings;
        InitializeComponent();
        _initializing = true;
        foreach (var family in Fonts.SystemFontFamilies
            .Select(f => f.Source).OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            FontCombo.Items.Add(family);
        _initializing = false;
        _emptyAnimTimer.Tick += (_, _) => AdvanceFrame(PreviewLayer.Empty);
        _fullAnimTimer.Tick += (_, _) => AdvanceFrame(PreviewLayer.Full);
        _mutedAnimTimer.Tick += (_, _) => AdvanceFrame(PreviewLayer.Muted);
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false)
            {
                _emptyAnimTimer.Stop();
                _fullAnimTimer.Stop();
                _mutedAnimTimer.Stop();
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
        _mutedSource = info.MutedPath;
        ShowLayerPath(EmptyPathText, info.EmptyPath);
        ShowLayerPath(FullPathText, info.FullPath);
        ShowLayerPath(MutedPathText, info.MutedPath);
        _initializing = true; // bulk control update must not re-enter OnControlChanged per control
        NameBox.Text = info.Name;
        ShowNumberCheck.IsChecked = info.Text is { Show: true };
        NumberXBox.Text = (info.Text?.X ?? 10).ToString();
        NumberYBox.Text = (info.Text?.Y ?? 5).ToString();
        LoadTextStyle(info.Text);
        ScaleSlider.Value = info.Scale;
        FpsBox.Text = info.Fps.ToString("0.##");
        EmptyFramesBox.Text = info.EmptyFrames.ToString();
        FullFramesBox.Text = info.FullFrames.ToString();
        MutedFramesBox.Text = info.MutedFrames.ToString();
        MutedDimSlider.Value = info.MutedDim;
        MutedDimLabel.Text = info.MutedDim.ToString("0.00");
        FillStartBox.Text = info.FillStartX.ToString();
        FillEndBox.Text = info.FillEndX.ToString();
        _initializing = false;
        ReloadPreviewData();
        StatusText.Text = $"Editing '{info.Name}'. Change the name before saving to create a copy.";
    }

    private void ClearEditor()
    {
        _editingSkinName = null;
        _emptySource = null;
        _fullSource = null;
        _mutedSource = null;
        _emptyFrames = null;
        _fullFrames = null;
        _mutedFrames = null;
        _initializing = true;
        NameBox.Text = "";
        FpsBox.Text = "10";
        EmptyFramesBox.Text = "1";
        FullFramesBox.Text = "1";
        MutedFramesBox.Text = "1";
        MutedDimSlider.Value = 0.6;
        MutedDimLabel.Text = "0.60";
        FillStartBox.Text = "";
        FillEndBox.Text = "";
        LoadTextStyle(null); // reset styling controls to defaults
        _initializing = false;
        ShowLayerPath(EmptyPathText, null);
        ShowLayerPath(FullPathText, null);
        ShowLayerPath(MutedPathText, null);
        ImageErrorText.Text = "";
        RestartAnimationTimers(); // no frames -> stops both
        RefreshPreview();
        Validate();
        StatusText.Text = "Pick two images to start a new skin.";
    }

    private void OnBrowseEmpty(object sender, RoutedEventArgs e) => Browse(PreviewLayer.Empty);
    private void OnBrowseFull(object sender, RoutedEventArgs e) => Browse(PreviewLayer.Full);
    private void OnBrowseMuted(object sender, RoutedEventArgs e) => Browse(PreviewLayer.Muted);

    private void OnClearMuted(object sender, RoutedEventArgs e)
    {
        _mutedSource = null;
        _mutedFrames = null;
        ShowLayerPath(MutedPathText, null);
        _initializing = true;
        MutedFramesBox.Text = "1";
        _initializing = false;
        ReloadPreviewData();
    }

    private void Browse(PreviewLayer layer)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Images (*.png;*.gif)|*.png;*.gif",
            Title = layer switch
            {
                PreviewLayer.Empty => "Choose the empty layer (0% artwork)",
                PreviewLayer.Full => "Choose the full layer (100% artwork)",
                _ => "Choose the muted artwork (shown instead of the dimmed bar)",
            },
        };
        if (dialog.ShowDialog(this) != true) return;
        SetLayerSource(layer, dialog.FileName);
        if (layer != PreviewLayer.Muted)
        {
            _initializing = true;
            FillStartBox.Text = ""; // new artwork: range resets to full width in ReloadPreviewData
            FillEndBox.Text = "";
            _initializing = false;
        }
        ReloadPreviewData();
    }

    private void SetLayerSource(PreviewLayer layer, string path)
    {
        switch (layer)
        {
            case PreviewLayer.Empty:
                _emptySource = path; ShowLayerPath(EmptyPathText, path);
                break;
            case PreviewLayer.Full:
                _fullSource = path; ShowLayerPath(FullPathText, path);
                break;
            case PreviewLayer.Muted:
                _mutedSource = path; ShowLayerPath(MutedPathText, path);
                break;
        }
    }

    /// <summary>The one way a layer's source is shown: shortened to fit the options column, with
    /// the full path as the tooltip. Null clears the row back to its placeholder.</summary>
    private static void ShowLayerPath(TextBlock target, string? path)
    {
        target.Text = path is null ? "—" : FileNames.PathForDisplay(path);
        target.ToolTip = path;
    }

    /// <summary>The options panel scrolls on the wheel from anywhere in it. Handling the
    /// tunnelling event at the ScrollViewer is what makes that true over the combo boxes, which
    /// handle the bubbling MouseWheel themselves — and it stops a wheel aimed at the panel from
    /// silently changing the font or the alignment sitting under the pointer.</summary>
    private void OnOptionsWheel(object sender, MouseWheelEventArgs e)
    {
        OptionsScroll.ScrollToVerticalOffset(OptionsScroll.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private void OnImportEmptyFrames(object sender, RoutedEventArgs e) => ImportFrames(PreviewLayer.Empty);
    private void OnImportFullFrames(object sender, RoutedEventArgs e) => ImportFrames(PreviewLayer.Full);
    private void OnImportMutedFrames(object sender, RoutedEventArgs e) => ImportFrames(PreviewLayer.Muted);

    /// <summary>Builds a sprite sheet from a numbered PNG frame sequence (the Photoshop
    /// "Export Layers to Files" workflow): frames are sorted by filename, must share dimensions,
    /// get stacked vertically into a scratch PNG that becomes the layer source, and the layer's
    /// frame count is filled in automatically.</summary>
    private void ImportFrames(PreviewLayer layer)
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
            var scratchDir = Path.Combine(Path.GetTempPath(), "aorineq-designer");
            Directory.CreateDirectory(scratchDir);
            var sheetPath = Path.Combine(scratchDir, Guid.NewGuid().ToString("N") + ".png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(sheet));
            using (var fs = File.Create(sheetPath))
                encoder.Save(fs);

            _initializing = true;
            switch (layer)
            {
                case PreviewLayer.Empty:
                    _emptySource = sheetPath;
                    EmptyPathText.Text = $"{files.Length} frames → sheet";
                    EmptyPathText.ToolTip = sheetPath;
                    EmptyFramesBox.Text = files.Length.ToString();
                    break;
                case PreviewLayer.Full:
                    _fullSource = sheetPath;
                    FullPathText.Text = $"{files.Length} frames → sheet";
                    FullPathText.ToolTip = sheetPath;
                    FullFramesBox.Text = files.Length.ToString();
                    break;
                case PreviewLayer.Muted:
                    _mutedSource = sheetPath;
                    MutedPathText.Text = $"{files.Length} frames → sheet";
                    MutedPathText.ToolTip = sheetPath;
                    MutedFramesBox.Text = files.Length.ToString();
                    break;
            }
            if (layer != PreviewLayer.Muted)
            {
                FillStartBox.Text = ""; // new artwork: range resets to full width in ReloadPreviewData
                FillEndBox.Text = "";
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
        _mutedFrames = null;
        RestartAnimationTimers(); // stale timers from the previous skin must not keep ticking

        var emptyMeta = ReadLayerMeta(_emptySource, ParseFrames(EmptyFramesBox));
        var fullMeta = ReadLayerMeta(_fullSource, ParseFrames(FullFramesBox));
        var mutedMeta = ReadLayerMeta(_mutedSource, ParseFrames(MutedFramesBox));
        if (emptyMeta.Error is not null || fullMeta.Error is not null || mutedMeta.Error is not null)
        {
            ImageErrorText.Text = emptyMeta.Error ?? fullMeta.Error ?? mutedMeta.Error!;
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
        if (mutedMeta.Size is not null && mutedMeta.Size != emptyMeta.Size)
        {
            ImageErrorText.Text =
                $"Frame-size mismatch: muted is {mutedMeta.Size.Value.Width}×{mutedMeta.Size.Value.Height} " +
                $"but the skin is {emptyMeta.Size.Value.Width}×{emptyMeta.Size.Value.Height}. They must be identical.";
            RefreshPreview();
            Validate();
            return;
        }

        try
        {
            double fps = ParseFps();
            _emptyFrames = SkinFrames.Load(_emptySource!, ParseFrames(EmptyFramesBox), fps);
            _fullFrames = SkinFrames.Load(_fullSource!, ParseFrames(FullFramesBox), fps);
            if (_mutedSource is not null)
                _mutedFrames = SkinFrames.Load(_mutedSource, ParseFrames(MutedFramesBox), fps);
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException
            or FileFormatException or ArgumentException or OutOfMemoryException)
        {
            // OutOfMemoryException: untrusted shared skins — a decode blowing the budget must
            // surface as an error, not take the app down.
            _emptyFrames = null;
            _fullFrames = null;
            _mutedFrames = null;
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
        _mutedFrameIndex = 0;
        // Blank/unparsable range boxes take the full width of the (possibly new) artwork.
        _initializing = true;
        if (!int.TryParse(FillStartBox.Text, out _)) FillStartBox.Text = "0";
        if (!int.TryParse(FillEndBox.Text, out _)) FillEndBox.Text = _imgWidth.ToString();
        _initializing = false;
        // A GIF layer's frame count is the file's own; reflect it read-only in the box.
        _initializing = true;
        if (IsGif(_emptySource)) EmptyFramesBox.Text = _emptyFrames.Frames.Count.ToString();
        if (IsGif(_fullSource)) FullFramesBox.Text = _fullFrames.Frames.Count.ToString();
        if (IsGif(_mutedSource) && _mutedFrames is not null) MutedFramesBox.Text = _mutedFrames.Frames.Count.ToString();
        EmptyFramesBox.IsEnabled = !IsGif(_emptySource);
        FullFramesBox.IsEnabled = !IsGif(_fullSource);
        MutedFramesBox.IsEnabled = _mutedSource is not null && !IsGif(_mutedSource);
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

    private int ParseFillStart() =>
        int.TryParse(FillStartBox.Text, out var v) ? Math.Clamp(v, 0, Math.Max(0, _imgWidth)) : 0;

    private int ParseFillEnd() =>
        int.TryParse(FillEndBox.Text, out var v) ? Math.Clamp(v, 0, Math.Max(0, _imgWidth)) : _imgWidth;

    private void AdvanceFrame(PreviewLayer layer)
    {
        switch (layer)
        {
            case PreviewLayer.Empty:
                if (_emptyFrames is not { IsAnimated: true }) return;
                _emptyFrameIndex = (_emptyFrameIndex + 1) % _emptyFrames.Frames.Count;
                EmptyImage.Source = _emptyFrames.Frames[_emptyFrameIndex];
                _emptyAnimTimer.Interval = _emptyFrames.Delays[_emptyFrameIndex];
                break;
            case PreviewLayer.Full:
                if (_fullFrames is not { IsAnimated: true }) return;
                _fullFrameIndex = (_fullFrameIndex + 1) % _fullFrames.Frames.Count;
                FullImage.Source = _fullFrames.Frames[_fullFrameIndex];
                _fullAnimTimer.Interval = _fullFrames.Delays[_fullFrameIndex];
                break;
            case PreviewLayer.Muted:
                if (_mutedFrames is not { IsAnimated: true }) return;
                _mutedFrameIndex = (_mutedFrameIndex + 1) % _mutedFrames.Frames.Count;
                MutedImage.Source = _mutedFrames.Frames[_mutedFrameIndex];
                _mutedAnimTimer.Interval = _mutedFrames.Delays[_mutedFrameIndex];
                break;
        }
    }

    private void RestartAnimationTimers()
    {
        _emptyAnimTimer.Stop();
        _fullAnimTimer.Stop();
        _mutedAnimTimer.Stop();
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
        if (_mutedFrames is { IsAnimated: true })
        {
            _mutedAnimTimer.Interval = _mutedFrames.Delays[_mutedFrameIndex];
            _mutedAnimTimer.Start();
        }
    }

    /// <summary>Single change handler for every editor control: keeps labels in sync and
    /// re-renders. Frame/fps fields require re-decoding, everything else only re-composes.</summary>
    private void OnControlChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        FillLabel.Text = $"{(int)FillSlider.Value}%";
        ScaleLabel.Text = ScaleSlider.Value.ToString("0.00");
        MutedDimLabel.Text = MutedDimSlider.Value.ToString("0.00");
        if (ReferenceEquals(sender, FpsBox) || ReferenceEquals(sender, EmptyFramesBox)
            || ReferenceEquals(sender, FullFramesBox) || ReferenceEquals(sender, MutedFramesBox))
        {
            ReloadPreviewData();
            return;
        }
        if (ReferenceEquals(sender, FillStartBox) || ReferenceEquals(sender, FillEndBox))
        {
            // Canonicalize: the box shows the value actually used (clamped into the image), so
            // what the user sees is exactly what Save round-trips.
            _initializing = true;
            FillStartBox.Text = ParseFillStart().ToString();
            FillEndBox.Text = ParseFillEnd().ToString();
            _initializing = false;
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
        int fillStart = ParseFillStart();
        int fillEnd = ParseFillEnd();
        double fillWidth = SkinMath.FillWidth(_imgWidth, percent, fillStart, fillEnd) * scale;
        FillClip.Rect = new Rect(0, 0, fillWidth, h);
        // Empty shows everything except the filled bar span [fillStart..fillWidth] (matches
        // SkinOsdWindow), so decoration outside the fill range keeps showing and there's no
        // double-darkening under a translucent full. Muted: full hidden, empty covers all.
        EmptyImage.Clip = muted
            ? null
            : SkinComposite.ComplementClip(fillStart * scale, fillWidth, w, h);
        // Mute preview mirrors SkinOsdWindow: dedicated muted artwork replaces everything;
        // otherwise the empty layer dims by the mutedDim slider. The slider only means anything
        // without a muted layer, so it's disabled while one is set.
        bool useMutedLayer = muted && _mutedFrames is not null;
        MutedImage.Visibility = useMutedLayer ? Visibility.Visible : Visibility.Collapsed;
        if (_mutedFrames is not null)
            MutedImage.Source = _mutedFrames.Frames[_mutedFrameIndex % _mutedFrames.Frames.Count];
        EmptyImage.Visibility = useMutedLayer ? Visibility.Hidden : Visibility.Visible;
        FullImage.Visibility = muted ? Visibility.Hidden : Visibility.Visible;
        EmptyImage.Opacity = muted && !useMutedLayer ? MutedDimSlider.Value : 1.0;
        MutedDimSlider.IsEnabled = _mutedSource is null;

        // Range handles ride on the artwork at their pixel positions (centered on the value).
        RangeStartHandle.Visibility = Visibility.Visible;
        RangeEndHandle.Visibility = Visibility.Visible;
        RangeStartHandle.Margin = new Thickness(fillStart * scale - RangeStartHandle.Width / 2, 0, 0, 0);
        RangeEndHandle.Margin = new Thickness(fillEnd * scale - RangeEndHandle.Width / 2, 0, 0, 0);

        bool showNumber = ShowNumberCheck.IsChecked == true;
        PercentPath.Visibility = showNumber ? Visibility.Visible : Visibility.Collapsed;
        TextStylePanel.IsEnabled = showNumber;
        if (showNumber)
        {
            var style = CurrentSkinText()!;
            _lastTextWidth = PercentTextRenderer.Update(PercentPath, style, percent.ToString(), scale,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            int x = int.TryParse(NumberXBox.Text, out var px) ? px : 0;
            int y = int.TryParse(NumberYBox.Text, out var py) ? py : 0;
            // X is the alignment anchor, same math as SkinOsdWindow.ShowVolume.
            PercentPath.Margin = new Thickness(
                SkinMath.AlignedTextX(x * scale, _lastTextWidth, style.Align), y * scale, 0, 0);
        }
    }

    /// <summary>Save/Test require both layers decoded, a valid name, and a sane fill range;
    /// Export requires a saved skin.</summary>
    private const string RangeErrorMessage = "Fill range: the 0% position must be left of the 100% position.";

    private void Validate()
    {
        bool imagesOk = _emptyFrames is not null && _fullFrames is not null;
        bool mutedOk = _mutedSource is null || _mutedFrames is not null; // chosen muted art must decode
        bool rangeOk = !imagesOk || ParseFillStart() < ParseFillEnd();
        if (imagesOk && !rangeOk)
            ImageErrorText.Text = RangeErrorMessage;
        else if (ImageErrorText.Text == RangeErrorMessage)
            ImageErrorText.Text = ""; // fixed without an image reload: stale error must clear
        string? nameError = SkinWriter.ValidateName(NameBox.Text);
        SaveButton.IsEnabled = imagesOk && mutedOk && rangeOk && nameError is null;
        TestButton.IsEnabled = imagesOk && mutedOk && rangeOk;
        ExportZipButton.IsEnabled = _editingSkinName is not null;
    }

    /// <summary>Builds the SkinText from the current controls, or null when Show is unchecked.</summary>
    private SkinText? CurrentSkinText()
    {
        if (ShowNumberCheck.IsChecked != true) return null;
        return new SkinText(true,
            int.TryParse(NumberXBox.Text, out var x) ? x : 0,
            int.TryParse(NumberYBox.Text, out var y) ? y : 0,
            Color: _textColor,
            // Editable combo: use the typed/selected text so an uninstalled authored font is
            // preserved, not silently replaced with the fallback.
            FontFamily: string.IsNullOrWhiteSpace(FontCombo.Text) ? "Segoe UI" : FontCombo.Text.Trim(),
            FontSize: double.TryParse(FontSizeBox.Text, out var fs) ? Math.Clamp(fs, 4, 200) : 14,
            Bold: BoldCheck.IsChecked == true,
            OutlineColor: OutlineCheck.IsChecked == true ? _outlineColor : null,
            OutlineWidth: double.TryParse(OutlineWidthBox.Text, out var ow) ? Math.Clamp(ow, 0, 20) : 0,
            ShadowColor: ShadowCheck.IsChecked == true ? _shadowColor : null,
            ShadowBlur: double.TryParse(ShadowBlurBox.Text, out var sb) ? Math.Clamp(sb, 0, 50) : 4,
            ShadowDepth: double.TryParse(ShadowDepthBox.Text, out var sd) ? Math.Clamp(sd, 0, 50) : 2,
            Align: SelectedAlign());
    }

    private string SelectedAlign() =>
        (AlignCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "left";

    /// <summary>Populates the text-style controls from a SkinText (or defaults when null).</summary>
    private void LoadTextStyle(SkinText? t)
    {
        _textColor = t?.Color ?? "#FFFFFFFF";
        _outlineColor = t?.OutlineColor ?? "#FF000000";
        _shadowColor = t?.ShadowColor ?? "#FF000000";
        SelectFont(t?.FontFamily ?? "Segoe UI");
        SelectAlign(t?.Align ?? "left");
        FontSizeBox.Text = (t?.FontSize ?? 14).ToString("0.##");
        BoldCheck.IsChecked = t?.Bold ?? false; // default = SemiBold baseline, so a new plain number saves as {show,x,y}
        OutlineCheck.IsChecked = t?.OutlineColor is not null;
        OutlineWidthBox.Text = (t?.OutlineWidth is > 0 ? t.OutlineWidth : 2).ToString("0.##");
        ShadowCheck.IsChecked = t?.ShadowColor is not null;
        ShadowBlurBox.Text = (t?.ShadowBlur ?? 4).ToString("0.##");
        ShadowDepthBox.Text = (t?.ShadowDepth ?? 2).ToString("0.##");
        UpdateSwatches();
    }

    private void SelectFont(string family)
    {
        foreach (string item in FontCombo.Items)
        {
            if (string.Equals(item, family, StringComparison.OrdinalIgnoreCase))
            {
                FontCombo.SelectedItem = item;
                return;
            }
        }
        // Unknown/uninstalled family: keep the authored name as editable text so Save preserves
        // it verbatim instead of rewriting it to the fallback.
        FontCombo.SelectedIndex = -1;
        FontCombo.Text = family;
    }

    private void SelectAlign(string align)
    {
        foreach (ComboBoxItem item in AlignCombo.Items)
        {
            if ((string)item.Tag == align)
            {
                AlignCombo.SelectedItem = item;
                return;
            }
        }
        AlignCombo.SelectedIndex = 0; // loader normalizes, but default to Left defensively
    }

    private void UpdateSwatches()
    {
        TextColorSwatch.Background = SwatchBrush(_textColor);
        OutlineColorSwatch.Background = SwatchBrush(_outlineColor);
        ShadowColorSwatch.Background = SwatchBrush(_shadowColor);
    }

    private static System.Windows.Media.Brush SwatchBrush(string hex)
    {
        try { return new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)); }
        catch (FormatException) { return System.Windows.Media.Brushes.White; }
        catch (NotSupportedException) { return System.Windows.Media.Brushes.White; }
    }

    private void OnPickTextColor(object sender, RoutedEventArgs e) => PickColor(ref _textColor);
    private void OnPickOutlineColor(object sender, RoutedEventArgs e)
    {
        if (PickColor(ref _outlineColor)) OutlineCheck.IsChecked = true; // choosing a color turns it on
    }
    private void OnPickShadowColor(object sender, RoutedEventArgs e)
    {
        if (PickColor(ref _shadowColor)) ShadowCheck.IsChecked = true;
    }

    /// <summary>Opens the native Windows color picker (WinForms, already referenced) seeded with
    /// the current color; on OK stores it back as #AARRGGBB and refreshes preview + swatches.</summary>
    private bool PickColor(ref string target)
    {
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
        try
        {
            var current = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(target);
            dialog.Color = System.Drawing.Color.FromArgb(current.A, current.R, current.G, current.B);
        }
        catch (FormatException) { }
        catch (NotSupportedException) { }

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            return false;
        var c = dialog.Color;
        // ColorDialog drops alpha; keep the previous alpha so a translucent color stays translucent.
        byte alpha = 0xFF;
        try { alpha = ((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(target)).A; }
        catch (FormatException) { }
        catch (NotSupportedException) { }
        target = $"#{alpha:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
        UpdateSwatches();
        RefreshPreview();
        Validate();
        return true;
    }

    private SkinConfig CurrentConfig()
    {
        SkinText? text = CurrentSkinText();
        // GIF layers self-describe; recording 1 keeps skin.json meaningful for the loader.
        // A full-width fill range is the default and is omitted from skin.json entirely.
        int fillStart = ParseFillStart();
        int fillEnd = ParseFillEnd();
        bool customRange = fillStart != 0 || fillEnd != _imgWidth;
        return new SkinConfig(text, ScaleSlider.Value, ParseFps(),
            IsGif(_emptySource) ? 1 : ParseFrames(EmptyFramesBox),
            IsGif(_fullSource) ? 1 : ParseFrames(FullFramesBox),
            customRange ? fillStart : null,
            customRange ? fillEnd : null,
            // Rounded so slider tick accumulation (12 × 0.05) can't miss the 0.6 default check.
            MutedFrames: _mutedSource is null || IsGif(_mutedSource) ? 1 : ParseFrames(MutedFramesBox),
            MutedDim: Math.Round(MutedDimSlider.Value, 2));
    }

    // ----- preview dragging: the percent number and the two fill-range handles are all
    // grabbable; positions are stored in image pixels (divide by scale). -----

    private static bool HitsElement(MouseButtonEventArgs e, FrameworkElement element)
    {
        if (element.Visibility != Visibility.Visible) return false;
        var pos = e.GetPosition(element);
        return pos.X >= 0 && pos.Y >= 0 && pos.X <= element.ActualWidth && pos.Y <= element.ActualHeight;
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Range handles first (they overlay the artwork), then the number.
        DragTarget target;
        if (HitsElement(e, RangeStartHandle)) target = DragTarget.RangeStart;
        else if (HitsElement(e, RangeEndHandle)) target = DragTarget.RangeEnd;
        else if (ShowNumberCheck.IsChecked == true && HitsElement(e, PercentPath)) target = DragTarget.Number;
        else return;

        _dragging = PreviewCanvas.CaptureMouse() ? target : DragTarget.None;
        e.Handled = _dragging != DragTarget.None;
    }

    private void OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragging == DragTarget.None || e.LeftButton != MouseButtonState.Pressed) return;
        double scale = ScaleSlider.Value;
        var pos = e.GetPosition(PreviewCanvas);

        _initializing = true; // box updates must not trigger OnControlChanged re-entry
        switch (_dragging)
        {
            case DragTarget.Number:
            {
                // Center the text on the cursor and clamp its left edge into the artwork; the
                // stored X is then the ANCHOR for the current alignment (margin position mapped
                // back through the alignment offset), so a centered number drops centered under
                // the cursor and stays put when the digit count changes.
                string align = SelectedAlign();
                double leftEdge = Math.Clamp(pos.X - _lastTextWidth / 2,
                    0, Math.Max(0, _imgWidth * scale - _lastTextWidth));
                double anchor = leftEdge - SkinMath.AlignedTextX(0, _lastTextWidth, align);
                int y = (int)Math.Round(pos.Y / scale - PercentPath.ActualHeight / (2 * scale));
                NumberXBox.Text = ((int)Math.Round(anchor / scale)).ToString();
                NumberYBox.Text = Math.Clamp(y, 0, Math.Max(0, _imgHeight - (int)(PercentPath.ActualHeight / scale))).ToString();
                break;
            }
            case DragTarget.RangeStart:
            {
                // The 0% handle stays left of the 100% handle by at least one pixel.
                int x = (int)Math.Round(pos.X / scale);
                FillStartBox.Text = Math.Clamp(x, 0, ParseFillEnd() - 1).ToString();
                break;
            }
            case DragTarget.RangeEnd:
            {
                int x = (int)Math.Round(pos.X / scale);
                FillEndBox.Text = Math.Clamp(x, ParseFillStart() + 1, _imgWidth).ToString();
                break;
            }
        }
        _initializing = false;
        RefreshPreview();
    }

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging == DragTarget.None) return;
        _dragging = DragTarget.None;
        PreviewCanvas.ReleaseMouseCapture();
        Validate(); // a finished range drag re-checks save-ability
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
                "AorinEQ", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (choice != MessageBoxResult.Yes) return;
        }

        try
        {
            CleanupTestOsd();
            var folder = SkinWriter.Save(ApoPaths.GetSkinsRoot(), name, _emptySource, _fullSource,
                CurrentConfig(), _mutedSource);
            // Adopt the saved copies as the working sources (keeping each source's extension —
            // a GIF layer saved as empty.gif), so further edits are in-place.
            _emptySource = Path.Combine(folder, "empty" + (IsGif(_emptySource) ? ".gif" : ".png"));
            _fullSource = Path.Combine(folder, "full" + (IsGif(_fullSource) ? ".gif" : ".png"));
            ShowLayerPath(EmptyPathText, _emptySource);
            ShowLayerPath(FullPathText, _fullSource);
            if (_mutedSource is not null)
            {
                _mutedSource = Path.Combine(folder, "muted" + (IsGif(_mutedSource) ? ".gif" : ".png"));
                ShowLayerPath(MutedPathText, _mutedSource);
            }
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
                    "AorinEQ", MessageBoxButton.YesNo, MessageBoxImage.Question);
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
            var root = Path.Combine(Path.GetTempPath(), "aorineq-skin-preview");
            var name = Guid.NewGuid().ToString("N");
            var folder = SkinWriter.Save(root, name, _emptySource, _fullSource, CurrentConfig(), _mutedSource);
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
