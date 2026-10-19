using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AorinEQ.Core;
using Point = System.Windows.Point;

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
        // Frozen while a drag runs: the canvas is centred, so growing it would move the overlay
        // under a still pointer and run the drag ahead of it. The drag's end applies it.
        if (_dragStart is null) ApplyOverhang();
    }

    /// <summary>What of the overlay's hit area lies past the artboards (text stopped at a far edge
    /// and its handles) gets canvas to scroll to, in whole pixels.</summary>
    private void ApplyOverhang()
    {
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
}
