using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AorinEQ.Core;
// WPF and WinForms are both referenced by this project, so the names below are
// ambiguous without an explicit alias. Every one of these is the WPF type.
using ComboBox = System.Windows.Controls.ComboBox;
using Cursors = System.Windows.Input.Cursors;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace AorinEQ.UI;

/// <summary>The canvas: the volume bar, the AirPlay strip directly under it and the notice below,
/// each drawn by the production control fed from <see cref="SkinDocument.ToInfo"/> - so what the
/// designer shows is what the OSD will draw - plus the preview strip, which is view state only.</summary>
public partial class SkinDesignerWindow
{
    private SkinView? _skinView;
    private (SkinNotice Look, double Scale)? _shownNotice;
    private bool _stripSkinned;

    internal SkinView? VolumeView => _skinView;

    private int PreviewPercent => (int)PreviewFill.Value;

    private bool PreviewMuted => PreviewMute.IsChecked == true;

    private string PreviewDeviceName =>
        string.IsNullOrWhiteSpace(PreviewDevice.Text) ? Loc.T("designer.notice.sample") : PreviewDevice.Text;

    private void RefreshCanvas()
    {
        PreviewFillLabel.Text = PreviewPercent + "%";
        RefreshVolumeBoard();
        RefreshAirPlayBoard();
        RefreshNoticeBoard();
        RefreshScene();
    }

    private void RefreshVolumeBoard()
    {
        if (!_info.IsValid)
        {
            ShowVolumePlaceholder();
            return;
        }
        try
        {
            if (_skinView is null)
            {
                _skinView = new SkinView(_info);
                VolumeBoard.Child = _skinView;
            }
            else
            {
                _skinView.Update(_info);
            }
            _skinView.SetVolume(PreviewPercent, PreviewMuted);
        }
        catch (Exception ex) when (SkinFrames.IsImagingFailure(ex))
        {
            ShowVolumePlaceholder();
            StatusText.Text = Loc.T("designer.image-failed-to-decode") + ex.Message;
        }
    }

    /// <summary>No drawable volume bar: say what is missing, in the bar's own place.</summary>
    private void ShowVolumePlaceholder()
    {
        _skinView?.StopAnimations();
        _skinView = null;
        VolumePlaceholder.Text = _issues.FirstOrDefault(i =>
                SkinLayers.BoardOf(i.Layer) == SkinLayer.VolumeBar)?.Message
            ?? Loc.T("designer.canvas.no-art");
        VolumeBoard.Child = VolumePlaceholder;
    }

    private void RefreshAirPlayBoard()
    {
        bool skinned = _info.AirPlay is not null;
        try
        {
            AirPlayPreview.SetSkin(_info.AirPlay, _info.Scale);
        }
        catch (Exception ex) when (SkinFrames.IsImagingFailure(ex))
        {
            skinned = false;
            AirPlayPreview.SetSkin(null, 1.0);
            StatusText.Text = Loc.T("designer.image-failed-to-decode") + ex.Message;
        }
        _stripSkinned = skinned;
        // A native strip takes its host's width: the volume bar's, as in SkinOsdWindow.
        AirPlayBoard.Width = skinned ? double.NaN
            : _info.IsValid ? _info.Width * _info.Scale : OsdWindow.DarkPillWidth;
        AirPlayPreview.SetState(PreviewAirPlayState());
    }

    /// <summary>The notice as the OSD draws it: art at the skin's scale, a plate at 1 - and while
    /// the skin describes none, the app's own dark pill.</summary>
    private void RefreshNoticeBoard()
    {
        var look = _info.Notice ?? SkinLoader.DefaultNotice;
        (SkinNotice Look, double Scale) shown = (look, look.HasArt ? _info.Scale : 1.0);
        if (shown != _shownNotice)
        {
            _shownNotice = shown;
            NoticePreview.Apply(shown.Look, shown.Scale);
        }
        NoticePreview.SetText(PreviewDeviceName);
    }

    private static readonly SkinHandle[] AreaHandles =
        [SkinHandle.Move, SkinHandle.TopLeft, SkinHandle.TopRight, SkinHandle.BottomLeft, SkinHandle.BottomRight];

    private SkinDocument? _dragStart;

    private readonly DesignerOverlay _overlay = new();

    internal DesignerOverlay Overlay => _overlay;

    /// <summary>Lays the overlay over the artboards (inside the zoom transform) and routes it. The
    /// scene is re-measured whenever the overlay's size changes - the first layout after the window
    /// is shown included, since the scene built before it knows no positions.</summary>
    private void WireOverlay()
    {
        Grid.SetColumn(_overlay, 1);
        Grid.SetRowSpan(_overlay, 4);
        Artboards.Children.Add(_overlay);
        Overlay.SizeChanged += (_, _) => RefreshScene();
        Overlay.LayerClicked += layer => { if (layer != _selection) Select(layer); };
        Overlay.DragStarted += BeginDrag;
        Overlay.Dragged += ApplyDrag;
        Overlay.DragCompleted += EndDrag;
        Overlay.PointerMoved += (board, x, y) =>
            CursorText.Text = board is null ? "" : Loc.T("designer.status.cursor", x, y);
    }

    /// <summary>A drag works from the document at mouse-down, as one merge run: one undo step.</summary>
    internal void BeginDrag()
    {
        EndMerge();
        _dragStart = _history.Current;
    }

    internal void ApplyDrag(SkinLayer layer, SkinHandle handle, int dx, int dy)
    {
        if (_dragStart is { } start) Commit(SkinEdits.Drag(start, layer, handle, dx, dy, _probe), "canvas-drag");
    }

    internal void EndDrag()
    {
        EndMerge();
        _dragStart = null;
        ApplyOverhang();
    }

    /// <summary>Tells the overlay where every artboard and grabbable part is, measured the way the
    /// renderers draw them. Each artboard's elements are listed bottom to top, as the overlay picks
    /// topmost first: the strip's click areas under its labels, so a click on a label picks it.</summary>
    private void RefreshScene()
    {
        Artboards.UpdateLayout();
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var boards = new List<SceneBoard>();
        var elements = new List<SceneElement>();

        if (_skinView is not null)
        {
            boards.Add(Board(SkinLayer.VolumeBar, VolumeBoard, _info.Scale, _info.Width, _info.Height, _info.FillStartX, _info.FillEndX));
            if (_info.Text is { Show: true } text)
                elements.Add(new SceneElement(SkinLayer.PercentText, SkinLayer.VolumeBar,
                    TextBounds(text, PreviewPercent.ToString(CultureInfo.CurrentCulture), ppd, FontWeights.SemiBold), [SkinHandle.Move]));
        }

        if (_stripSkinned && _info.AirPlay is { } strip)
        {
            boards.Add(Board(SkinLayer.AirPlay, AirPlayBoard, _info.Scale, strip.Width, strip.Height, strip.FillStartX, strip.FillEndX));
            elements.Add(new SceneElement(SkinLayer.AirPlayDropdown, SkinLayer.AirPlay, Rectangle(strip.DropdownHit), AreaHandles));
            if (strip.ConnectHit is { } power)
                elements.Add(new SceneElement(SkinLayer.AirPlayPower, SkinLayer.AirPlay, Rectangle(power), AreaHandles));
            if (strip.Name is { Show: true } name)
                elements.Add(new SceneElement(SkinLayer.AirPlayName, SkinLayer.AirPlay,
                    TextBounds(name, AirPlayPreview.DisplayName, ppd, FontWeights.Normal), [SkinHandle.Move]));
            if (strip.Percent is { Show: true } level)
                elements.Add(new SceneElement(SkinLayer.AirPlayLevel, SkinLayer.AirPlay,
                    TextBounds(level, AirPlayPreview.State.VolumePercent.ToString(CultureInfo.CurrentCulture), ppd, FontWeights.Normal), [SkinHandle.Move]));
        }
        else
        {
            boards.Add(Board(SkinLayer.AirPlay, AirPlayBoard, 1, (int)AirPlayBoard.ActualWidth, (int)AirPlayBoard.ActualHeight, null, null));
        }

        if (_info.Notice is { HasArt: true } art)
        {
            boards.Add(Board(SkinLayer.Notice, NoticeBoard, _info.Scale, art.Width, art.Height, null, null));
            elements.Add(new SceneElement(SkinLayer.NoticeText, SkinLayer.Notice,
                new Rect(art.Text.X, art.Text.Y, art.TextWidth, art.Text.FontSize * SkinClamps.LineHeightRatio),
                [SkinHandle.Move, SkinHandle.Right]));
            if (art.Glyph.Show)
                elements.Add(new SceneElement(SkinLayer.NoticeGlyph, SkinLayer.Notice,
                    new Rect(art.Glyph.X, art.Glyph.Y, art.Glyph.Size, art.Glyph.Size * SkinClamps.LineHeightRatio), [SkinHandle.Move]));
        }
        else
        {
            boards.Add(Board(SkinLayer.Notice, NoticeBoard, 1, (int)NoticeBoard.ActualWidth, (int)NoticeBoard.ActualHeight, null, null));
        }

        Overlay.Scene = new DesignerScene(boards, elements);
        ApplyOverhang();
    }

    /// <summary>What of the overlay's hit area lies past the artboards (text stopped at a far edge
    /// and its handles) gets canvas to scroll to, in whole pixels. Frozen while a drag runs: the
    /// canvas is centred, so growing it would move the overlay under a still pointer and run the
    /// drag ahead of it. The drag's end applies it.</summary>
    private void ApplyOverhang()
    {
        if (_dragStart is not null) return;
        var area = Overlay.HitArea;
        Artboards.Margin = new Thickness(0, 0, Math.Max(0, Math.Ceiling(area.Right - Overlay.ActualWidth)),
            Math.Max(0, Math.Ceiling(area.Bottom - Overlay.ActualHeight)));
    }

    private SceneBoard Board(SkinLayer layer, FrameworkElement host, double scale, int width, int height, int? fillStart, int? fillEnd) =>
        new(layer, host.TranslatePoint(new Point(0, 0), Overlay), scale, width, height, fillStart, fillEnd);

    /// <summary>A text's outline on the canvas, measured at the weight its renderer draws it at.</summary>
    private static Rect TextBounds(SkinText text, string sample, double pixelsPerDip, FontWeight unboldWeight)
    {
        var size = PercentTextRenderer.Measure(text, sample, pixelsPerDip, unboldWeight);
        return new Rect(SkinMath.AlignedTextX(text.X, size.Width, text.Align), text.Y, size.Width, size.Height);
    }

    private static Rect Rectangle(SkinHitRect r) => new(r.X, r.Y, r.Width, r.Height);

    /// <summary>The strip's preview state: a live session, a chosen receiver not connected, or no
    /// receiver at all.</summary>
    internal AirPlayBarState PreviewAirPlayState() => PreviewAirPlayMode.SelectedIndex switch
    {
        0 => new AirPlayBarState(true, PreviewDeviceName, true, true, (int)PreviewLevel.Value),
        1 => new AirPlayBarState(true, PreviewDeviceName, false, false, (int)PreviewLevel.Value),
        _ => new AirPlayBarState(true, "", false, false, 0),
    };

    private void OnPreviewValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_ready) RefreshCanvas();
    }

    private void OnPreviewToggled(object sender, RoutedEventArgs e)
    {
        if (_ready) RefreshCanvas();
    }

    private void OnPreviewModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) RefreshCanvas();
    }

    private void OnPreviewDeviceChanged(object sender, TextChangedEventArgs e)
    {
        if (_ready) RefreshCanvas();
    }

    private double _zoom = 1;
    private Point? _panOrigin;
    private Point _panStartOffset;

    internal double Zoom => _zoom;

    /// <summary>What has the keyboard: the canvas (arrows nudge, Delete clears), a text input
    /// (keys are its own), or anything else (the tree, a button).</summary>
    internal enum KeyboardTarget { Canvas, Text, Other }

    internal void SetZoom(double zoom)
    {
        _zoom = CanvasZoom.Clamp(zoom);
        ZoomTransform.ScaleX = _zoom;
        ZoomTransform.ScaleY = _zoom;
        // From 200 % the art's own pixels are shown rather than a blur of them, as image editors do.
        RenderOptions.SetBitmapScalingMode(Artboards, _zoom >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.Unspecified);
        // Snap thresholds and handle sizes are screen pixels: the overlay converts them by this.
        Overlay.Zoom = _zoom;
        ApplyOverhang(); // the handles' room past the artboards is screen pixels too
        ZoomText.Text = Math.Round(_zoom * 100).ToString(CultureInfo.CurrentCulture) + "%";
    }

    /// <summary>The zoom that shows all the artboards in the canvas - decided by the skin's size,
    /// as the artboards are drawn from it.</summary>
    internal void ZoomToFit()
    {
        Artboards.UpdateLayout();
        var margin = ZoomHost.Margin;
        SetZoom(CanvasZoom.Fit(Artboards.ActualWidth, Artboards.ActualHeight,
            CanvasScroll.ViewportWidth - margin.Left - margin.Right, CanvasScroll.ViewportHeight - margin.Top - margin.Bottom));
    }

    /// <summary>Zooms keeping the content under <paramref name="cursor"/> (viewport pixels) still.
    /// The zoomed content does not start at the scroll extent's origin - the canvas margin is
    /// outside the zoom, and content smaller than the viewport is centred - so the cursor is taken
    /// relative to where the content starts, before and after.</summary>
    internal void ZoomAt(double zoom, Point cursor)
    {
        double before = _zoom, h = CanvasScroll.HorizontalOffset, v = CanvasScroll.VerticalOffset;
        var from = ContentOrigin();
        SetZoom(zoom);
        CanvasScroll.UpdateLayout();
        var to = ContentOrigin();
        CanvasScroll.ScrollToHorizontalOffset(CanvasZoom.OffsetFor(h, cursor.X - from.X, before, _zoom) + to.X - from.X);
        CanvasScroll.ScrollToVerticalOffset(CanvasZoom.OffsetFor(v, cursor.Y - from.Y, before, _zoom) + to.Y - from.Y);
    }

    /// <summary>Where the zoomed content starts in the canvas's scroll extent.</summary>
    private Point ContentOrigin() =>
        ZoomHost.TranslatePoint(new Point(0, 0), CanvasScroll) + new Vector(CanvasScroll.HorizontalOffset, CanvasScroll.VerticalOffset);

    private void WireCanvasInput()
    {
        CanvasScroll.PreviewMouseWheel += (_, e) =>
        {
            if (HandleWheel(e.Delta, Keyboard.Modifiers, e.GetPosition(CanvasScroll))) e.Handled = true;
        };
        // Space+drag and middle-drag pan; both are taken before the overlay sees the press.
        CanvasScroll.PreviewMouseDown += (_, e) =>
        {
            if (BeginPan(e.ChangedButton, Keyboard.IsKeyDown(Key.Space), e.GetPosition(CanvasScroll))) e.Handled = true;
        };
        CanvasScroll.PreviewMouseMove += (_, e) =>
        {
            if (_panOrigin is null) return;
            PanTo(e.GetPosition(CanvasScroll));
            e.Handled = true;
        };
        CanvasScroll.PreviewMouseUp += (_, e) =>
        {
            if (_panOrigin is null) return;
            CanvasScroll.ReleaseMouseCapture();
            e.Handled = true;
        };
        // Every way a pan ends - the button released, the capture taken away - goes through here.
        CanvasScroll.LostMouseCapture += (_, _) =>
        {
            _panOrigin = null;
            CanvasScroll.ClearValue(CursorProperty);
        };
    }

    /// <summary>Ctrl+wheel zooms one step at <paramref name="cursor"/> (viewport pixels); a plain
    /// wheel is not taken, and scrolls. Whether it was taken.</summary>
    internal bool HandleWheel(int delta, ModifierKeys modifiers, Point cursor)
    {
        if (!modifiers.HasFlag(ModifierKeys.Control)) return false;
        ZoomAt(CanvasZoom.WheelZoom(_zoom, delta), cursor);
        return true;
    }

    /// <summary>A press of <paramref name="button"/> at <paramref name="at"/> (viewport pixels)
    /// starts a pan when it is the middle button, or the left with Space held. Whether it did.</summary>
    internal bool BeginPan(MouseButton button, bool spaceHeld, Point at)
    {
        if (button != MouseButton.Middle && !(button == MouseButton.Left && spaceHeld)) return false;
        if (!CanvasScroll.CaptureMouse()) return false;
        _panOrigin = at;
        _panStartOffset = new Point(CanvasScroll.HorizontalOffset, CanvasScroll.VerticalOffset);
        CanvasScroll.Cursor = Cursors.ScrollAll;
        return true;
    }

    /// <summary>The pointer of a running pan is at <paramref name="at"/>: the canvas follows it.</summary>
    internal void PanTo(Point at)
    {
        if (_panOrigin is not { } origin) return;
        CanvasScroll.ScrollToHorizontalOffset(_panStartOffset.X - (at.X - origin.X));
        CanvasScroll.ScrollToVerticalOffset(_panStartOffset.Y - (at.Y - origin.Y));
    }

    private void OnZoomOut(object sender, RoutedEventArgs e) => SetZoom(CanvasZoom.Out(_zoom));

    private void OnZoomIn(object sender, RoutedEventArgs e) => SetZoom(CanvasZoom.In(_zoom));

    private void OnZoomFit(object sender, RoutedEventArgs e) => ZoomToFit();

    private void OnZoomActual(object sender, RoutedEventArgs e) => SetZoom(1);

    // The canvas's keys are taken on the way down, before the scroll viewer round it scrolls by
    // the arrows. Anything else's keys are its own first (a scrub label's Esc, a text box's
    // Ctrl+Z, the tree's arrows) and reach the window only when it leaves them, on the way up.
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (!e.Handled && TargetOf(Keyboard.FocusedElement) == KeyboardTarget.Canvas)
            e.Handled = HandleShortcut(KeyOf(e), e.KeyboardDevice.Modifiers, KeyboardTarget.Canvas);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && TargetOf(Keyboard.FocusedElement) is var target && target != KeyboardTarget.Canvas)
            e.Handled = HandleShortcut(KeyOf(e), e.KeyboardDevice.Modifiers, target);
    }

    private static Key KeyOf(KeyEventArgs e) => e.Key == Key.System ? e.SystemKey : e.Key;

    /// <summary>Who a key is for when <paramref name="focused"/> has the keyboard.</summary>
    internal KeyboardTarget TargetOf(object? focused) => focused switch
    {
        TextBoxBase or ComboBox { IsEditable: true } => KeyboardTarget.Text,
        null => KeyboardTarget.Canvas,
        _ when ReferenceEquals(focused, Overlay) || ReferenceEquals(focused, this) => KeyboardTarget.Canvas,
        _ => KeyboardTarget.Other,
    };

    /// <summary>Ctrl+S saves (committing the field being typed in first), Ctrl+0 fits, Ctrl+1 is
    /// 100 %, Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z undo and redo unless a text input has the keyboard
    /// (then they are its own). Esc deselects unless a text input has the keyboard. With the canvas
    /// focused: arrows nudge 1 skin px (Shift: 10), Delete clears optional art or the power area.
    /// Whether the key was taken.</summary>
    internal bool HandleShortcut(Key key, ModifierKeys modifiers, KeyboardTarget target)
    {
        bool shift = modifiers.HasFlag(ModifierKeys.Shift);
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            switch (key)
            {
                case Key.S:
                    Keyboard.ClearFocus();
                    Save();
                    return true;
                case Key.D0 or Key.NumPad0:
                    ZoomToFit();
                    return true;
                case Key.D1 or Key.NumPad1:
                    SetZoom(1);
                    return true;
                case Key.Z when target != KeyboardTarget.Text && shift:
                case Key.Y when target != KeyboardTarget.Text:
                    Redo();
                    return true;
                case Key.Z when target != KeyboardTarget.Text:
                    Undo();
                    return true;
            }
            return false;
        }

        if (key == Key.Escape && target != KeyboardTarget.Text)
        {
            Select(SkinLayer.SkinInfo);
            return true;
        }
        if (target != KeyboardTarget.Canvas) return false;
        int step = shift ? 10 : 1;
        switch (key)
        {
            case Key.Left: CommitKeyEdit(doc => Nudged(doc, -step, 0)); return true;
            case Key.Right: CommitKeyEdit(doc => Nudged(doc, step, 0)); return true;
            case Key.Up: CommitKeyEdit(doc => Nudged(doc, 0, -step)); return true;
            case Key.Down: CommitKeyEdit(doc => Nudged(doc, 0, step)); return true;
            case Key.Delete:
                CommitKeyEdit(doc => _selection switch
                {
                    SkinLayer.VolumeMuted or SkinLayer.NoticeArt => SkinEdits.ClearArt(doc, _selection, _probe),
                    SkinLayer.AirPlayPower => SkinEdits.SetHit(doc, SkinLayer.AirPlayPower, null),
                    _ => doc,
                });
                return true;
        }
        return false;
    }

    private SkinDocument Nudged(SkinDocument doc, int dx, int dy) =>
        SkinEdits.Drag(doc, _selection, SkinHandle.Move, dx, dy, _probe);

    /// <summary>A key's edit of the canvas, one undo step per press. A drag still running ends
    /// first, as for Undo: it stays one step, and its next move does not overwrite the edit.</summary>
    private void CommitKeyEdit(Func<SkinDocument, SkinDocument> edit)
    {
        Overlay.FinishDrag();
        Commit(edit(_history.Current));
    }
}
