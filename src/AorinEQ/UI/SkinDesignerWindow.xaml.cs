using System.IO;
using System.Windows;
using System.Windows.Controls;
using AorinEQ.Core;
// WPF and WinForms are both referenced by this project, so the names below are
// ambiguous without an explicit alias. Every one of these is the WPF type.
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;

namespace AorinEQ.UI;

/// <summary>The Skin Designer: Layers on the left, the skin drawn by the production renderers in
/// the middle, the selected layer's Properties on the right.
///
/// The window holds no skin state of its own. The skin is a <see cref="SkinDocument"/> in a
/// <see cref="DesignerHistory"/>; every change is a <see cref="SkinEdits"/> function committed to
/// the history, and every history change re-derives what is drawn (<see cref="OnDocumentChanged"/>).
/// Selection, zoom and the preview strip are view state and never enter the history.
///
/// One instance per app: closing hides it (App owns the lifetime) and App disposes it at exit,
/// which deletes this session's scratch sprite sheets.</summary>
public partial class SkinDesignerWindow : Wpf.Ui.Controls.FluentWindow, IDisposable
{
    private const string ImageFilter = "Images (*.png;*.gif)|*.png;*.gif";

    private readonly Func<Settings> _currentSettings;
    private readonly string _skinsRoot;
    private readonly ImageProbe _probe = new();
    private readonly string _scratchFolder =
        Path.Combine(Path.GetTempPath(), "aorineq-designer", Guid.NewGuid().ToString("N"));
    /// <summary>The preview OSD's wheel accumulator: the studio keeps its own rather than sharing
    /// App's, because the preview drives the designer's fill slider, not the machine's volume.</summary>
    private readonly ScrollStep _previewScroll = new();
    private DesignerHistory _history;
    private SkinInfo _info;
    private IReadOnlyList<SkinIssue> _issues = [];
    private string? _loadedName;
    private SkinLayer _selection = SkinLayer.SkinInfo;
    private SkinOsdWindow? _testOsd;
    private string? _testFolder;
    private bool _populatingSkins;
    private bool _ready;
    private bool _disposed;

    /// <summary>Raised after a successful save or zip import with the skin's name. App refreshes
    /// the Settings picker and hot-reloads the live OSD when the active skin was touched.</summary>
    public event Action<string>? SkinSaved;

    /// <summary>The colour picker's recent colours changed; App persists them (shared with the HUD).</summary>
    public event Action<IReadOnlyList<string>>? RecentColorsChanged;

    /// <summary>A side panel was resized; App persists the widths.</summary>
    public event Action<double, double>? PanelWidthsChanged;

    public SkinDesignerWindow(Func<Settings> currentSettings, string skinsRoot)
    {
        _currentSettings = currentSettings;
        _skinsRoot = skinsRoot;
        InitializeComponent();
        var settings = currentSettings();
        LayersColumn.Width = new GridLength(settings.DesignerLayersWidth);
        PropertiesColumn.Width = new GridLength(settings.DesignerPropertiesWidth);
        foreach (var board in BoardHosts)
            board.Background = DesignerBrushes.Checkerboard;
        PreviewDevice.Text = Loc.T("designer.notice.sample");
        _history = new DesignerHistory(SkinDocument.New(Loc.T("designer.untitled")));
        _history.Changed += OnDocumentChanged;
        _info = _history.Current.ToInfo(_probe);
        Loc.LanguageChanged += OnLanguageChanged;
        WireOverlay();
        WireCanvasInput();
        SetZoom(1);
        _ready = true;
        PopulateSkinList(null);
        OnDocumentChanged();
        StatusText.Text = Loc.T("designer.status.pick-two-images-to");
    }

    internal SkinDocument Document => _history.Current;
    internal DesignerHistory History => _history;
    internal SkinInfo Info => _info;
    internal IReadOnlyList<SkinIssue> Issues => _issues;
    internal SkinLayer Selection => _selection;
    internal string ScratchFolder => _scratchFolder;

    internal void Commit(SkinDocument next, string? mergeKey = null) => _history.Commit(next, mergeKey);

    internal void EndMerge() => _history.EndMerge();

    /// <summary>Makes <paramref name="layer"/> the selection: the tree, the Properties panel and
    /// the canvas outline follow.</summary>
    internal void Select(SkinLayer layer)
    {
        SetSelection(layer);
        RefreshLayers();
    }

    /// <summary>The one selection path, for everything but the tree's own rebuild: the tree calls
    /// this directly for a click it is already showing (rebuilding it under the mouse would lose
    /// its focus), so a tree click and a canvas click update the same things.</summary>
    private void SetSelection(SkinLayer layer)
    {
        ClosePicker();
        _selection = layer;
        Overlay.Selected = layer;
        RefreshProperties(rebuild: true);
    }

    /// <summary>Loads a skin from the skins folder as the document. A strip or notice whose art
    /// failed to load stays in the document as the loader's reason - a blocking issue on its layer,
    /// with Remove / Turn off in the Layers tree and Properties - so a save can never delete that
    /// art without the user deciding to.</summary>
    internal bool LoadSkin(string name)
    {
        var info = SkinLoader.Load(Path.Combine(_skinsRoot, name));
        if (!info.IsValid)
        {
            StatusText.Text = info.Error;
            return false;
        }
        Load(SkinDocument.FromInfo(info), info.Name, Loc.T("designer.status.editing-change-the-name", info.Name));
        SelectSkinItem(info.Name);
        return true;
    }

    internal void NewDocument()
    {
        Load(SkinDocument.New(Loc.T("designer.untitled")), null, Loc.T("designer.status.pick-two-images-to"));
        SelectSkinItem(null);
    }

    private void Load(SkinDocument document, string? loadedName, string status)
    {
        ClosePicker();
        _history.Changed -= OnDocumentChanged;
        _history = new DesignerHistory(document);
        _history.Changed += OnDocumentChanged;
        _loadedName = loadedName;
        SetSelection(SkinLayer.SkinInfo);
        CleanupTestOsd();
        // Before the canvas draws: a decode failure it reports must not be overwritten by this.
        StatusText.Text = status;
        OnDocumentChanged();
        RefreshProperties(rebuild: true);
    }

    /// <summary>Everything drawn is re-derived from the document here, after every change.</summary>
    private void OnDocumentChanged()
    {
        _info = _history.Current.ToInfo(_probe);
        _issues = _history.Current.Issues(_probe);
        if (ShownLayer(_selection) is var shown && shown != _selection)
        {
            // The selected part left the tree (an undone "Add AirPlay strip"): its parent instead.
            SetSelection(shown);
        }
        RefreshTitle();
        RefreshLayers();
        RefreshProperties();
        RefreshCanvas();
        RefreshCommands();
    }

    private void RefreshTitle()
    {
        var title = (_history.IsDirty ? "• " : "") + Loc.T("designer.title.aorineq-skin-designer");
        Title = title;
        DesignerTitleBar.Title = title;
    }

    /// <summary>Save is disabled while anything blocks it, and its tooltip lists what.</summary>
    private void RefreshCommands()
    {
        UndoButton.IsEnabled = _history.CanUndo;
        RedoButton.IsEnabled = _history.CanRedo;
        SaveButton.IsEnabled = _issues.Count == 0;
        SaveButton.ToolTip = _issues.Count == 0 ? null : BlockedMessage();
        TestButton.IsEnabled = _info.IsValid;
        ExportZipButton.IsEnabled = _loadedName is not null;
        ShareButton.IsEnabled = _loadedName is not null;
    }

    private string BlockedMessage() =>
        Loc.T("designer.save.blocked", string.Join(Environment.NewLine, _issues.Select(i => LayerName(i.Layer) + ": " + i.Message)));

    /// <summary>Writes the document with <see cref="SkinWriter"/>. Saving under the name of a
    /// DIFFERENT existing skin overwrites it, so that is confirmed first; re-saving the skin being
    /// edited is a plain save.
    ///
    /// The art is looked at again first: the issues are only re-derived on an edit, and art deleted
    /// or replaced on disk since would otherwise be saved over (the writer skips copying a file
    /// onto itself) or leave a half-written copy.</summary>
    internal bool Save()
    {
        OnDocumentChanged();
        if (_issues.Count > 0)
        {
            StatusText.Text = BlockedMessage();
            return false;
        }
        var doc = _history.Current;
        var name = doc.Name.Trim();
        if (!string.Equals(name, _loadedName, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(Path.Combine(_skinsRoot, name)))
        {
            var choice = MessageBox.Show(Loc.T("designer.a-skin-named-already-exists-overwrite", name),
                "AorinEQ", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (choice != MessageBoxResult.Yes) return false;
        }

        try
        {
            CleanupTestOsd();
            SkinWriter.Save(_skinsRoot, name, doc.EmptyPath, doc.FullPath, doc.SaveConfig(_probe), doc.MutedPath);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            StatusText.Text = ex.Message;
            return false;
        }

        _loadedName = name;
        _history.MarkSaved();
        PopulateSkinList(name);
        StatusText.Text = Loc.T("designer.status.saved", name);
        SkinSaved?.Invoke(name);
        return true;
    }

    /// <summary>Rebuilds the skin picker from the skins folder (invalid skins disabled, with the
    /// error as tooltip - the Settings picker's policy) and selects <paramref name="selectName"/>.</summary>
    private void PopulateSkinList(string? selectName)
    {
        _populatingSkins = true;
        try
        {
            SkinSelect.Items.Clear();
            foreach (var skin in SkinLoader.Scan(_skinsRoot))
            {
                var item = new ComboBoxItem { Content = skin.Name, Tag = skin.Name, IsEnabled = skin.IsValid };
                if (!skin.IsValid) item.ToolTip = skin.Error;
                SkinSelect.Items.Add(item);
            }
        }
        finally
        {
            _populatingSkins = false;
        }
        SelectSkinItem(selectName);
    }

    private void SelectSkinItem(string? name)
    {
        _populatingSkins = true;
        SkinSelect.SelectedItem = SkinSelect.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Tag as string, name, StringComparison.OrdinalIgnoreCase));
        _populatingSkins = false;
    }

    private void OnSkinSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_populatingSkins || SkinSelect.SelectedItem is not ComboBoxItem { Tag: string name }) return;
        // A skin that broke after the list was read: the picker goes back to what is still open.
        if (!LoadSkin(name)) SelectSkinItem(_loadedName);
    }

    private void OnNew(object sender, RoutedEventArgs e) => NewDocument();

    private void OnUndo(object sender, RoutedEventArgs e) => Undo();

    private void OnRedo(object sender, RoutedEventArgs e) => Redo();

    // A drag still running ends first, so it stays one undo step and is not resumed on top of the undo.
    private void Undo()
    {
        Overlay.FinishDrag();
        _history.Undo();
    }

    private void Redo()
    {
        Overlay.FinishDrag();
        _history.Redo();
    }

    private void OnSave(object sender, RoutedEventArgs e) => Save();

    /// <summary>Imports a shared skin zip: name from the zip's file name, collision confirmed, then
    /// the imported skin opens and the app is told.</summary>
    private void OnImportZip(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Skin zip (*.zip)|*.zip",
            Title = Loc.T("designer.import-a-shared-skin"),
        };
        if (dialog.ShowDialog(this) != true) return;

        var name = SkinArchive.DefaultName(dialog.FileName).Trim();
        try
        {
            if (SkinWriter.ValidateName(name) is { } nameError)
            {
                StatusText.Text = Loc.T("designer.status.zip-filename-can-t", nameError);
                return;
            }
            if (Directory.Exists(Path.Combine(_skinsRoot, name)))
            {
                var choice = MessageBox.Show(Loc.T("designer.a-skin-named-already-exists-overwrite", name),
                    "AorinEQ", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (choice != MessageBoxResult.Yes) return;
            }
            SkinArchive.Import(dialog.FileName, _skinsRoot, name);
            PopulateSkinList(null);
            LoadSkin(name);
            StatusText.Text = Loc.T("designer.status.imported", name);
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
        if (ExportToZip(Loc.T("designer.export-skin-as-zip")) is not { } zipPath) return;
        StatusText.Text = Loc.T("designer.exported-to-exports-the-last-saved", _loadedName, zipPath);
    }

    /// <summary>Share: the same export, plus the aorineq:// install link on the clipboard. The link
    /// is a TEMPLATE - the zip has to be hosted somewhere over https first - so the status line
    /// says so rather than implying a finished link.</summary>
    private void OnShare(object sender, RoutedEventArgs e)
    {
        if (ExportToZip(Loc.T("designer.share-skin-as-zip")) is not { } zipPath) return;
        var link = SkinShare.BuildInstallLinkTemplate(_loadedName!);
        try
        {
            Clipboard.SetText(link);
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            // Another process can hold the clipboard open; the zip is still exported.
            StatusText.Text = Loc.T("designer.exported-to-but-the-clipboard-was", _loadedName, zipPath, ex.Message, link);
            return;
        }
        StatusText.Text = Loc.T("designer.exported-to-including-a-preview-image", _loadedName, zipPath, link, SkinShare.HostingHint);
    }

    private string? ExportToZip(string dialogTitle)
    {
        if (_loadedName is null) return null;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Skin zip (*.zip)|*.zip",
            FileName = _loadedName + ".zip",
            Title = dialogTitle,
        };
        if (dialog.ShowDialog(this) != true) return null;
        try
        {
            SkinArchive.Export(Path.Combine(_skinsRoot, _loadedName), dialog.FileName);
            return dialog.FileName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            StatusText.Text = ex.Message;
            return null;
        }
    }

    private void OnTestOnDesktop(object sender, RoutedEventArgs e) => TestOnDesktop();

    /// <summary>Writes the draft to a temp skin folder and shows it in a REAL SkinOsdWindow with
    /// the user's OSD settings - true rendering, animation, click-through and drag/wheel, all
    /// driving the preview strip's fill. The previous test window and folder go first.</summary>
    internal bool TestOnDesktop()
    {
        if (!_info.IsValid) return false;
        CleanupTestOsd();
        try
        {
            var info = SaveTestCopy();
            _testOsd = new SkinOsdWindow(info);
            _testOsd.ApplyConfig(_currentSettings());
            _testOsd.PercentChangedByUser += p => PreviewFill.Value = p;
            // Same rules as App's wheel - whole notches, Ctrl/Shift steps, the user's direction.
            _testOsd.VolumeScrolled += n =>
            {
                var s = _currentSettings();
                PreviewFill.Value += _previewScroll.Feed(n.RawDelta, ScrollStep.StepFor(n.Ctrl, n.Shift, s.StepPercent), s.ScrollInverted);
            };
            _testOsd.ShowVolume(PreviewPercent, PreviewMuted, interactive: true);
            StatusText.Text = Loc.T("designer.status.showing-on-desktop-click");
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException || SkinFrames.IsImagingFailure(ex))
        {
            StatusText.Text = ex.Message;
            return false;
        }
    }

    /// <summary>Saves the document under a fresh name in %TEMP%\aorineq-skin-preview and loads it
    /// back - the test OSD shows exactly what a save would load.</summary>
    private SkinInfo SaveTestCopy()
    {
        var doc = _history.Current;
        var root = Path.Combine(Path.GetTempPath(), "aorineq-skin-preview");
        _testFolder = SkinWriter.Save(root, Guid.NewGuid().ToString("N"), doc.EmptyPath, doc.FullPath,
            doc.SaveConfig(_probe), doc.MutedPath);
        var info = SkinLoader.Load(_testFolder);
        return info.IsValid ? info : throw new InvalidOperationException(info.Error);
    }

    private void CleanupTestOsd()
    {
        _testOsd?.Close();
        _testOsd = null;
        if (_testFolder is null) return;
        try { Directory.Delete(_testFolder, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        _testFolder = null;
    }

    private void OnSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) =>
        PanelWidthsChanged?.Invoke(LayersColumn.ActualWidth, PropertiesColumn.ActualWidth);

    /// <summary>Strings set from code (the title's dirty mark, the tree, the panels) are rebuilt in
    /// the new language; XAML strings repaint through their bindings.</summary>
    private void OnLanguageChanged()
    {
        ClosePicker(); // the panel it edits is rebuilt below
        RefreshTitle();
        RefreshLayers();
        RefreshProperties(rebuild: true);
        RefreshCanvas(); // the measured text outlines follow the language's sample text
        RefreshCommands();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Disposed (app exit, test teardown): really close. Otherwise App owns the lifetime and a
        // close only hides, like SettingsWindow.
        if (_disposed)
        {
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        ClosePicker();
        CleanupTestOsd();
        Hide();
    }

    /// <summary>Releases what outlives a hidden window: the static language subscription, the test
    /// OSD and its folder, the picker, animation timers and this session's scratch sheets. After
    /// this, <see cref="Window.Close"/> really closes the window.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        // The picker's pending colour reaches the document before the history is let go.
        ClosePicker();
        if (_picker is not null)
        {
            _picker.EyedropperRequested -= StartEyedropper;
            _picker.ColorChanged -= OnPickerColorChanged;
            _picker.Closed -= OnPickerClosed;
            _picker.RecentsChanged -= OnPickerRecentsChanged;
            _picker = null;
        }
        _disposed = true;
        Loc.LanguageChanged -= OnLanguageChanged;
        _history.Changed -= OnDocumentChanged;
        CleanupTestOsd();
        _skinView?.StopAnimations();
        try
        {
            if (Directory.Exists(_scratchFolder)) Directory.Delete(_scratchFolder, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
