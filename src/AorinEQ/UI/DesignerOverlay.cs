using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AorinEQ.Core;
// WPF and WinForms are both referenced by this project, so the names below are
// ambiguous without an explicit alias. Every one of these is the WPF type.
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursor = System.Windows.Input.Cursor;
using Cursors = System.Windows.Input.Cursors;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace AorinEQ.UI;

/// <summary>One artboard as the overlay sees it: where it sits in overlay coordinates, the scale it
/// is drawn at, its size in skin pixels and its fill range.</summary>
internal sealed record SceneBoard(SkinLayer Layer, Point Origin, double Scale, int Width, int Height, int? FillStart, int? FillEnd)
{
    public Rect Bounds => new(Origin, new Size(Width * Scale, Height * Scale));

    public Point ToOverlay(double x, double y) => new(Origin.X + x * Scale, Origin.Y + y * Scale);

    public Rect ToOverlay(Rect skin) => new(ToOverlay(skin.X, skin.Y), new Size(skin.Width * Scale, skin.Height * Scale));
}

/// <summary>A part of the skin the canvas can grab, in its artboard's skin pixels.</summary>
internal sealed record SceneElement(SkinLayer Layer, SkinLayer Board, Rect Bounds, IReadOnlyList<SkinHandle> Handles);

internal sealed record DesignerScene(IReadOnlyList<SceneBoard> Boards, IReadOnlyList<SceneElement> Elements)
{
    public static readonly DesignerScene Empty = new([], []);

    public SceneBoard? BoardFor(SkinLayer layer) =>
        Boards.FirstOrDefault(b => b.Layer == SkinLayers.BoardOf(layer));

    /// <summary>Everything the scene covers in overlay coordinates: the artboards and every
    /// element - including a text stopped at an artboard's right or bottom edge, which lies
    /// outside it. Empty for an empty scene.</summary>
    public Rect Extent
    {
        get
        {
            var extent = Rect.Empty;
            foreach (var board in Boards) extent.Union(board.Bounds);
            foreach (var element in Elements)
                if (BoardFor(element.Board) is { } board) extent.Union(board.ToOverlay(element.Bounds));
            return extent;
        }
    }
}

/// <summary>Everything the canvas draws ON the skin rather than as part of it - the selection
/// outline and its handles, the fill-range lines, the dashed click areas and the magenta snap
/// guides - and the mouse handling that turns a drag into skin pixels. It lies over the artboards
/// inside the zoom transform, so its coordinates are the artboards' own and only the pen widths
/// and handle sizes have to be divided by the zoom. The renderers underneath are never touched.
///
/// A drag reports its delta from mouse-down, snapped and in skin pixels; what the delta does to the
/// skin is <see cref="SkinEdits.Drag"/>'s business.</summary>
internal sealed class DesignerOverlay : FrameworkElement
{
    public const double HandleScreenSize = 8;

    private static readonly Color Accent = Color.FromRgb(0x00, 0x78, 0xD7);
    private static readonly Color GuideColor = Color.FromRgb(0xFF, 0x00, 0xFF);

    private sealed record DragState(SceneBoard Board, SceneElement? Element, SkinHandle Handle, Point Start);

    private DesignerScene _scene = DesignerScene.Empty;
    private SkinLayer _selected = SkinLayer.SkinInfo;
    private double _zoom = 1;
    private DragState? _drag;
    private IReadOnlyList<SnapTarget> _guides = [];

    public DesignerOverlay()
    {
        Focusable = true;
        FocusVisualStyle = null;
    }

    public DesignerScene Scene
    {
        get => _scene;
        set { _scene = value; InvalidateVisual(); }
    }

    public SkinLayer Selected
    {
        get => _selected;
        set { _selected = value; InvalidateVisual(); }
    }

    public double Zoom
    {
        get => _zoom;
        set { _zoom = CanvasZoom.Clamp(value); InvalidateVisual(); }
    }

    /// <summary>The targets the drag in progress has snapped to, drawn as the magenta guides.</summary>
    internal IReadOnlyList<SnapTarget> Guides => _guides;

    /// <summary>The layer under a click: an element, an artboard, or skin info for empty canvas.</summary>
    public event Action<SkinLayer>? LayerClicked;

    public event Action? DragStarted;

    /// <summary>Layer, handle and the snapped skin-pixel delta from mouse-down.</summary>
    public event Action<SkinLayer, SkinHandle, int, int>? Dragged;

    public event Action? DragCompleted;

    /// <summary>The artboard under the pointer and the skin pixel it is over; null when off them.</summary>
    public event Action<SkinLayer?, int, int>? PointerMoved;

    /// <summary>What a point (overlay coordinates) is over: the selected element's handles first,
    /// then a fill-range line, then elements topmost first, then an artboard.</summary>
    internal (SceneBoard? Board, SceneElement? Element, SkinHandle? Handle) HitTest(Point point)
    {
        double grab = HandleScreenSize / _zoom;
        foreach (var element in _scene.Elements.Where(e => e.Layer == _selected))
        {
            if (_scene.BoardFor(element.Board) is not { } board) continue;
            var bounds = board.ToOverlay(element.Bounds);
            foreach (var handle in element.Handles.Where(h => h != SkinHandle.Move))
                if (HandleRect(bounds, handle, grab).Contains(point)) return (board, element, handle);
        }
        foreach (var board in _scene.Boards)
        {
            if (!board.Bounds.Contains(point)) continue;
            if (board.FillStart is int start && Math.Abs(point.X - board.ToOverlay(start, 0).X) <= grab / 2)
                return (board, null, SkinHandle.FillStart);
            if (board.FillEnd is int end && Math.Abs(point.X - board.ToOverlay(end, 0).X) <= grab / 2)
                return (board, null, SkinHandle.FillEnd);
        }
        foreach (var element in _scene.Elements.Reverse())
            if (_scene.BoardFor(element.Board) is { } board && board.ToOverlay(element.Bounds).Contains(point))
                return (board, element, SkinHandle.Move);
        foreach (var board in _scene.Boards)
            if (board.Bounds.Contains(point)) return (board, null, null);
        return (null, null, null);
    }

    /// <summary>A drag of <paramref name="handle"/> by <paramref name="overlayDelta"/> (overlay
    /// pixels) as whole skin pixels, snapped unless <paramref name="snap"/> is false (Alt). Fill
    /// lines and the text box's right edge only move sideways.</summary>
    internal (int Dx, int Dy, IReadOnlyList<SnapTarget> Guides) ComputeDrag(SceneBoard board, SceneElement? element,
        SkinHandle handle, Vector overlayDelta, bool snap)
    {
        double dx = overlayDelta.X / board.Scale, dy = overlayDelta.Y / board.Scale;
        bool sideways = handle is SkinHandle.FillStart or SkinHandle.FillEnd or SkinHandle.Right;
        if (sideways) dy = 0;
        IReadOnlyList<SnapTarget> guides = [];
        if (snap)
        {
            var result = SnapMath.Snap(MovingRect(board, element, handle, dx, dy), Targets(board, element, handle),
                SnapMath.ThresholdFor(_zoom, board.Scale));
            dx += result.OffsetX;
            if (!sideways) dy += result.OffsetY;
            guides = sideways ? result.Guides.Where(g => g.Axis == SnapAxis.X).ToList() : result.Guides;
        }
        return ((int)Math.Round(dx, MidpointRounding.AwayFromZero), (int)Math.Round(dy, MidpointRounding.AwayFromZero), guides);
    }

    private static SnapRect MovingRect(SceneBoard board, SceneElement? element, SkinHandle handle, double dx, double dy)
    {
        if (handle == SkinHandle.FillStart) return new SnapRect(board.FillStart!.Value + dx, 0, 0, board.Height);
        if (handle == SkinHandle.FillEnd) return new SnapRect(board.FillEnd!.Value + dx, 0, 0, board.Height);
        var r = element!.Bounds;
        return handle switch
        {
            SkinHandle.Move => new SnapRect(r.X + dx, r.Y + dy, r.Width, r.Height),
            SkinHandle.Right => new SnapRect(r.Right + dx, r.Y, 0, r.Height),
            SkinHandle.TopLeft => new SnapRect(r.X + dx, r.Y + dy, 0, 0),
            SkinHandle.TopRight => new SnapRect(r.Right + dx, r.Y + dy, 0, 0),
            SkinHandle.BottomLeft => new SnapRect(r.X + dx, r.Bottom + dy, 0, 0),
            _ => new SnapRect(r.Right + dx, r.Bottom + dy, 0, 0),
        };
    }

    /// <summary>The artboard's edges and centre, its fill lines (not the one being dragged) and
    /// every other element on the same artboard.</summary>
    private IReadOnlyList<SnapTarget> Targets(SceneBoard board, SceneElement? self, SkinHandle handle)
    {
        var targets = new List<SnapTarget>(SnapMath.ArtboardTargets(board.Width, board.Height));
        if (board.FillStart is int start && handle != SkinHandle.FillStart)
            targets.Add(new SnapTarget(SnapAxis.X, start, SnapSource.Artboard));
        if (board.FillEnd is int end && handle != SkinHandle.FillEnd)
            targets.Add(new SnapTarget(SnapAxis.X, end, SnapSource.Artboard));
        foreach (var element in _scene.Elements)
            if (element.Board == board.Layer && !ReferenceEquals(element, self))
                targets.AddRange(SnapMath.ElementTargets(new SnapRect(element.Bounds.X, element.Bounds.Y,
                    element.Bounds.Width, element.Bounds.Height)));
        return targets;
    }

    private static Rect HandleRect(Rect r, SkinHandle handle, double size)
    {
        var centre = handle switch
        {
            SkinHandle.TopLeft => r.TopLeft,
            SkinHandle.TopRight => r.TopRight,
            SkinHandle.BottomLeft => r.BottomLeft,
            SkinHandle.BottomRight => r.BottomRight,
            SkinHandle.Right => new Point(r.Right, r.Top + r.Height / 2),
            _ => new Point(r.Left + r.Width / 2, r.Top + r.Height / 2),
        };
        return new Rect(centre.X - size / 2, centre.Y - size / 2, size, size);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus(); // the canvas takes the keyboard (arrows nudge) and the field being typed in commits
        var point = e.GetPosition(this);
        var (board, element, handle) = HitTest(point);
        if (board is null)
        {
            LayerClicked?.Invoke(SkinLayer.SkinInfo);
            return;
        }
        bool fillHandle = handle is SkinHandle.FillStart or SkinHandle.FillEnd;
        LayerClicked?.Invoke(element?.Layer ?? board.Layer);
        if (handle is not { } held || (element is null && !fillHandle) || !CaptureMouse()) return;
        _drag = new DragState(board, element, held, point);
        DragStarted?.Invoke();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        var over = _scene.Boards.FirstOrDefault(b => b.Bounds.Contains(point));
        if (over is null) PointerMoved?.Invoke(null, 0, 0);
        else PointerMoved?.Invoke(over.Layer, (int)Math.Floor((point.X - over.Origin.X) / over.Scale),
            (int)Math.Floor((point.Y - over.Origin.Y) / over.Scale));

        if (_drag is not { } drag || e.LeftButton != MouseButtonState.Pressed)
        {
            var hit = HitTest(point);
            Cursor = CursorFor(hit.Element is null && hit.Handle is not (SkinHandle.FillStart or SkinHandle.FillEnd) ? null : hit.Handle);
            return;
        }
        var (dx, dy, guides) = ComputeDrag(drag.Board, drag.Element, drag.Handle, point - drag.Start,
            snap: !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
        _guides = guides;
        InvalidateVisual();
        Dragged?.Invoke(drag.Element?.Layer ?? drag.Board.Layer, drag.Handle, dx, dy);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag is not null) ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_drag is null) return;
        _drag = null;
        _guides = [];
        InvalidateVisual();
        DragCompleted?.Invoke();
    }

    private static Cursor? CursorFor(SkinHandle? handle) => handle switch
    {
        SkinHandle.Move => Cursors.SizeAll,
        SkinHandle.FillStart or SkinHandle.FillEnd or SkinHandle.Right => Cursors.SizeWE,
        SkinHandle.TopLeft or SkinHandle.BottomRight => Cursors.SizeNWSE,
        SkinHandle.TopRight or SkinHandle.BottomLeft => Cursors.SizeNESW,
        _ => null,
    };

    protected override void OnRender(DrawingContext dc)
    {
        double px = 1 / _zoom;
        // Transparent, not null: the whole overlay takes the mouse, so the renderers under it never
        // do - and so does whatever of the scene lies past it (text stopped at an artboard's far
        // edge, the handles on it), or that part could be seen but not grabbed.
        var hitArea = new Rect(RenderSize);
        if (_scene.Extent is { IsEmpty: false } extent)
            hitArea.Union(Rect.Inflate(extent, HandleScreenSize * px / 2, HandleScreenSize * px / 2));
        dc.DrawRectangle(Brushes.Transparent, null, hitArea);
        var line = MakePen(Accent, px);
        var dashed = MakePen(Accent, px, dashed: true);

        foreach (var board in _scene.Boards)
            foreach (var x in new[] { board.FillStart, board.FillEnd }.OfType<int>())
                dc.DrawLine(line, board.ToOverlay(x, 0), board.ToOverlay(x, board.Height));

        foreach (var element in _scene.Elements.Where(e => e.Handles.Contains(SkinHandle.TopLeft)))
            if (_scene.BoardFor(element.Board) is { } board)
                dc.DrawRectangle(null, dashed, board.ToOverlay(element.Bounds));

        var selected = _scene.Elements.FirstOrDefault(e => e.Layer == _selected);
        if (selected is not null && _scene.BoardFor(selected.Board) is { } selectedBoard)
        {
            var bounds = selectedBoard.ToOverlay(selected.Bounds);
            dc.DrawRectangle(null, MakePen(Accent, 2 * px), bounds);
            foreach (var handle in selected.Handles.Where(h => h != SkinHandle.Move))
                dc.DrawRectangle(Brushes.White, line, HandleRect(bounds, handle, HandleScreenSize * px));
        }
        else if (_selected != SkinLayer.SkinInfo && _scene.BoardFor(_selected) is { } board)
        {
            dc.DrawRectangle(null, MakePen(Accent, 2 * px), board.Bounds);
        }

        if (_drag is { } drag)
        {
            var guide = MakePen(GuideColor, px);
            foreach (var target in Guides)
                if (target.Axis == SnapAxis.X)
                    dc.DrawLine(guide, drag.Board.ToOverlay(target.Position, 0), drag.Board.ToOverlay(target.Position, drag.Board.Height));
                else
                    dc.DrawLine(guide, drag.Board.ToOverlay(0, target.Position), drag.Board.ToOverlay(drag.Board.Width, target.Position));
        }
    }

    private static Pen MakePen(Color color, double thickness, bool dashed = false)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness);
        if (dashed) pen.DashStyle = DashStyles.Dash;
        pen.Freeze();
        return pen;
    }
}
