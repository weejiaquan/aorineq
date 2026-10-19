using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AorinEQ.Core;
using AorinEQ.UI;
using Xunit.Abstractions;
using Cursors = System.Windows.Input.Cursors;
using Point = System.Windows.Point;

namespace AorinEQ.Tests;

/// <summary>The canvas overlay on a hand-built scene: what a point hits, what a drag means in skin
/// pixels at any zoom and skin scale, how it snaps, and that it draws the selection where the
/// element is.</summary>
[Collection("Wpf")]
public class DesignerOverlayTests
{
    private readonly StaWpf _sta;
    private readonly ITestOutputHelper _out;

    public DesignerOverlayTests(StaWpf sta, ITestOutputHelper output)
    {
        _sta = sta;
        _out = output;
    }

    private static readonly SkinHandle[] AreaHandles =
        [SkinHandle.Move, SkinHandle.TopLeft, SkinHandle.TopRight, SkinHandle.BottomLeft, SkinHandle.BottomRight];

    private static DesignerScene Scene(double scale) => new(
        [
            new SceneBoard(SkinLayer.VolumeBar, new Point(10, 20), scale, 300, 40, 60, 280),
            new SceneBoard(SkinLayer.AirPlay, new Point(10, 20 + 40 * scale), scale, 300, 30, 0, 300),
        ],
        [
            new SceneElement(SkinLayer.PercentText, SkinLayer.VolumeBar, new Rect(100, 10, 30, 18), [SkinHandle.Move]),
            new SceneElement(SkinLayer.AirPlayPower, SkinLayer.AirPlay, new Rect(270, 0, 30, 30), AreaHandles),
        ]);

    [Fact]
    public void A_point_hits_handles_then_fill_lines_then_elements_then_the_board()
    {
        var r = _sta.Run(() =>
        {
            var overlay = new DesignerOverlay { Scene = Scene(1), Selected = SkinLayer.AirPlayPower, Zoom = 1 };
            var corner = overlay.HitTest(new Point(10 + 300, 60 + 30));          // the power area's bottom-right
            var fill = overlay.HitTest(new Point(10 + 60 + 2, 30));               // 2 px from the 0 % line
            var text = overlay.HitTest(new Point(10 + 110, 20 + 15));
            var board = overlay.HitTest(new Point(10 + 200, 20 + 30));
            var outside = overlay.HitTest(new Point(1, 1));
            return (corner, fill, text, board, outside);
        });
        _out.WriteLine($"{r}");
        Assert.Equal((SkinLayer.AirPlayPower, SkinHandle.BottomRight), (r.corner.Element!.Layer, r.corner.Handle!.Value));
        Assert.Equal((SkinLayer.VolumeBar, SkinHandle.FillStart), (r.fill.Board!.Layer, r.fill.Handle!.Value));
        Assert.Equal((SkinLayer.PercentText, SkinHandle.Move), (r.text.Element!.Layer, r.text.Handle!.Value));
        Assert.Equal(SkinLayer.VolumeBar, r.board.Board!.Layer);
        Assert.Null(r.board.Element);
        Assert.Null(r.board.Handle);
        Assert.Null(r.outside.Board);
    }

    /// <summary>Review focus: a drag at 800 % and at 25 %, on a skin drawn at 2x.</summary>
    [Fact]
    public void A_drag_maps_screen_pixels_to_skin_pixels_at_any_zoom()
    {
        var r = _sta.Run(() =>
        {
            var overlay = new DesignerOverlay { Scene = Scene(2) };
            var board = overlay.Scene.Boards[0];
            var text = overlay.Scene.Elements[0];
            overlay.Zoom = 8;
            var plain = overlay.ComputeDrag(board, text, SkinHandle.Move, new Vector(20, 10), snap: false);
            var nearEdgeZoomedIn = overlay.ComputeDrag(board, text, SkinHandle.Move, new Vector(-97 * 2, 0), snap: true);
            overlay.Zoom = 0.25;
            var nearEdgeZoomedOut = overlay.ComputeDrag(board, text, SkinHandle.Move, new Vector(-97 * 2, 0), snap: true);
            var farOff = overlay.ComputeDrag(board, text, SkinHandle.Move, new Vector(100000, -100000), snap: false);
            return (plain, nearEdgeZoomedIn, nearEdgeZoomedOut, farOff);
        });
        _out.WriteLine($"{r}");
        Assert.Equal((10, 5), (r.plain.Dx, r.plain.Dy));
        Assert.Equal(-97, r.nearEdgeZoomedIn.Dx);   // 3 skin px = 48 screen px at 800 % x2: no snap
        Assert.Equal(-100, r.nearEdgeZoomedOut.Dx); // 3 skin px = 1.5 screen px at 25 % x2: snaps to the edge
        Assert.Contains(new SnapTarget(SnapAxis.X, 0, SnapSource.Artboard), r.nearEdgeZoomedOut.Guides);
        Assert.Equal((50000, -50000), (r.farOff.Dx, r.farOff.Dy)); // clamping is SkinEdits.Drag's job
    }

    [Fact]
    public void Snapping_lines_an_area_up_with_a_fill_line_and_alt_turns_it_off()
    {
        var r = _sta.Run(() =>
        {
            var overlay = new DesignerOverlay { Scene = Scene(1), Zoom = 1 };
            var strip = overlay.Scene.Boards[1];
            var power = overlay.Scene.Elements[1];
            var snapped = overlay.ComputeDrag(strip, power, SkinHandle.Move, new Vector(-118, 0), snap: true);   // left 152 -> centre 150
            var free = overlay.ComputeDrag(strip, power, SkinHandle.Move, new Vector(-118, 0), snap: false);
            var fill = overlay.ComputeDrag(strip, null, SkinHandle.FillEnd, new Vector(-3, 40), snap: true);
            return (snapped, free, fill);
        });
        _out.WriteLine($"{r}");
        Assert.Equal(-120, r.snapped.Dx);
        Assert.Contains(new SnapTarget(SnapAxis.X, 150, SnapSource.Artboard), r.snapped.Guides);
        Assert.Equal(-118, r.free.Dx);
        Assert.Empty(r.free.Guides);
        Assert.Equal((0, 0), (r.fill.Dx, r.fill.Dy));  // the 100 % line snaps back onto the strip's edge, and never moves vertically
    }

    [Fact]
    public void The_selection_is_drawn_on_the_element()
    {
        var pixel = _sta.Run(() =>
        {
            var overlay = new DesignerOverlay { Scene = Scene(1), Selected = SkinLayer.PercentText, Zoom = 1 };
            overlay.Measure(new Size(400, 200));
            overlay.Arrange(new Rect(0, 0, 400, 200));
            var bitmap = new RenderTargetBitmap(400, 200, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(overlay);
            var bgra = new byte[4];
            bitmap.CopyPixels(new Int32Rect(110, 40, 1, 1), bgra, 4, 0);   // the text's left edge, mid-height
            return bgra;
        });
        _out.WriteLine($"BGRA {string.Join(",", pixel)}");
        Assert.Equal(new byte[] { 0xD7, 0x78, 0x00, 0xFF }, pixel);
    }

    /// <summary>A text stopped at its artboard's far edge lies past the overlay's own bounds: the
    /// scene's extent includes it, and the overlay takes the mouse over it - but not beyond. Only
    /// the elements get room for their handles: the artboards themselves add none, so a scene with
    /// nothing past its boards needs no more than the overlay's own size.</summary>
    [Fact]
    public void An_element_past_the_overlay_still_takes_the_mouse()
    {
        var r = _sta.Run(() =>
        {
            var scene = new DesignerScene(
                [new SceneBoard(SkinLayer.VolumeBar, new Point(0, 0), 1, 100, 40, null, null)],
                [new SceneElement(SkinLayer.PercentText, SkinLayer.VolumeBar, new Rect(100, 40, 30, 18), [SkinHandle.Move])]);
            var overlay = new DesignerOverlay { Scene = scene, Zoom = 1 };
            overlay.Measure(new Size(100, 40));
            overlay.Arrange(new Rect(0, 0, 100, 40));
            var onText = VisualTreeHelper.HitTest(overlay, new Point(115, 49))?.VisualHit;
            var beyond = VisualTreeHelper.HitTest(overlay, new Point(160, 49))?.VisualHit;
            var hitArea = overlay.HitArea;
            var picked = overlay.HitTest(new Point(115, 49)).Element?.Layer;
            overlay.Scene = new DesignerScene(scene.Boards,
                [new SceneElement(SkinLayer.PercentText, SkinLayer.VolumeBar, new Rect(10, 10, 30, 18), [SkinHandle.Move])]);
            return (Extent: scene.Extent(0), Handled: scene.Extent(4), EmptyExtent: DesignerScene.Empty.Extent(4), overlay.RenderSize, hitArea,
                InsideHitArea: overlay.HitArea, OnText: ReferenceEquals(onText, overlay), Beyond: beyond is null,
                Picked: picked);
        });
        _out.WriteLine("expected extent 0,0,130,58, with 4 px handle room 0,0,134,62, hit area 0,0,134,62 and 0,0,100,40 with the text inside, " +
                       $"mouse taken on the text and not beyond, PercentText picked; actual {r}");
        Assert.Equal(new Rect(0, 0, 130, 58), r.Extent);
        Assert.Equal(new Rect(0, 0, 134, 62), r.Handled);
        Assert.Equal(new Rect(0, 0, 134, 62), r.hitArea);         // half a handle round the text, none round the board
        Assert.Equal(new Rect(0, 0, 100, 40), r.InsideHitArea);   // nothing past the board: no margin at all
        Assert.True(r.EmptyExtent.IsEmpty);
        Assert.True(r.OnText);
        Assert.True(r.Beyond);
        Assert.Equal(SkinLayer.PercentText, r.Picked);
    }

    /// <summary>The zoom the overlay converts by is the canvas's clamped zoom, never a raw value.</summary>
    [Fact]
    public void The_zoom_is_clamped_to_the_canvas_range()
    {
        var r = _sta.Run(() =>
        {
            var overlay = new DesignerOverlay();
            double Set(double zoom) { overlay.Zoom = zoom; return overlay.Zoom; }
            return (Huge: Set(100), Tiny: Set(0.01), Zero: Set(0), NaN: Set(double.NaN), Inside: Set(3));
        });
        _out.WriteLine($"expected (8, 0.25, 0.25, 1, 3); actual {r}");
        Assert.Equal((8.0, 0.25, 0.25, 1.0, 3.0), r);
    }

    /// <summary>The canvas outlines a text with the size the renderer lays it out at: the width
    /// <see cref="PercentTextRenderer.Update(System.Windows.Shapes.Path,SkinText,string,double,double)"/>
    /// reports at scale 1, at the style's own weight and letter spacing.</summary>
    [Fact]
    public void Text_is_measured_as_the_renderer_draws_it()
    {
        var r = _sta.Run(() =>
        {
            var style = new SkinText(true, 0, 0) { FontSize = 20 };
            var spaced = style with { LetterSpacing = 6 };
            double dip = VisualTreeHelper.GetDpi(new System.Windows.Controls.Border()).PixelsPerDip;
            var measured = PercentTextRenderer.Measure(style, "88", dip);
            var heavy = PercentTextRenderer.Measure(style with { Weight = 900 }, "88", dip);
            var measuredSpaced = PercentTextRenderer.Measure(spaced, "88", dip);
            double drawn = PercentTextRenderer.Update(new System.Windows.Shapes.Path(), style, "88", 1.0, dip);
            double drawnAt2 = PercentTextRenderer.Update(new System.Windows.Shapes.Path(), style, "88", 2.0, dip);
            double drawnSpacedAt2 = PercentTextRenderer.Update(new System.Windows.Shapes.Path(), spaced, "88", 2.0, dip);
            return (measured, heavy, measuredSpaced, drawn, drawnAt2, drawnSpacedAt2);
        });
        _out.WriteLine($"measured {r.measured}, at weight 900 {r.heavy}, spaced 6 {r.measuredSpaced}; " +
                       $"drawn at 1 {r.drawn}, at 2 {r.drawnAt2}, spaced at 2 {r.drawnSpacedAt2}");
        Assert.Equal(r.drawn, r.measured.Width, 6);
        Assert.Equal(r.drawnAt2, 2 * r.measured.Width, 1);   // measured at scale 1, whatever the skin's scale
        Assert.True(r.measured.Height > 20 && r.measured.Height < 40);
        Assert.True(r.heavy.Width - r.measured.Width > 0.01, "the style's weight did not reach the measurement");
        Assert.Equal(r.drawnSpacedAt2, 2 * r.measuredSpaced.Width, 1); // the spacing scales with the skin too
        Assert.Equal(r.measured.Width + 6, r.measuredSpaced.Width, 1);  // two digits, one gap
    }

    /// <summary>Every step of a drag rebuilds the scene, so the dragged layer's last position comes
    /// back as a new element. It is the element itself, not a target: a slow drag must not stick
    /// to where it just was.</summary>
    [Fact]
    public void A_drag_never_snaps_to_its_own_last_position()
    {
        var r = _sta.Run(() =>
        {
            var atStart = new SceneElement(SkinLayer.PercentText, SkinLayer.VolumeBar, new Rect(100, 10, 30, 18), [SkinHandle.Move]);
            var overlay = new DesignerOverlay
            {
                Scene = new DesignerScene(
                    [new SceneBoard(SkinLayer.VolumeBar, new Point(0, 0), 1, 300, 40, null, null)],
                    [atStart with { Bounds = new Rect(101, 10, 30, 18) }]), // after the drag's first 1 px step
                Zoom = 1,
            };
            return overlay.ComputeDrag(overlay.Scene.Boards[0], atStart, SkinHandle.Move, new Vector(2, 0), snap: true);
        });
        _out.WriteLine($"expected Dx 2 with no guide on the element's own last position; actual {r.Dx} guides [{string.Join(", ", r.Guides)}]");
        Assert.Equal(2, r.Dx);
        Assert.DoesNotContain(r.Guides, g => g.Source == SnapSource.Element);
    }

    /// <summary>Text stopped at the bar's bottom edge overflows onto the strip below. The strip's own
    /// element under the point wins the click; where nothing of the strip's is, the text does.</summary>
    [Fact]
    public void An_element_on_its_own_board_wins_over_one_overflowing_onto_it()
    {
        var r = _sta.Run(() =>
        {
            var overlay = new DesignerOverlay
            {
                Scene = new DesignerScene(
                    [
                        new SceneBoard(SkinLayer.VolumeBar, new Point(0, 0), 1, 100, 40, null, null),
                        new SceneBoard(SkinLayer.AirPlay, new Point(0, 40), 1, 100, 30, null, null),
                    ],
                    [
                        new SceneElement(SkinLayer.AirPlayPower, SkinLayer.AirPlay, new Rect(0, 0, 50, 30), [SkinHandle.Move]),
                        new SceneElement(SkinLayer.PercentText, SkinLayer.VolumeBar, new Rect(20, 30, 60, 18), [SkinHandle.Move]),
                    ]),
                Zoom = 1,
            };
            return (OnPower: overlay.HitTest(new Point(30, 45)).Element?.Layer,
                OnBarItself: overlay.HitTest(new Point(30, 35)).Element?.Layer,
                OnBareStrip: overlay.HitTest(new Point(70, 45)).Element?.Layer);
        });
        _out.WriteLine($"expected (AirPlayPower, PercentText, PercentText); actual {r}");
        Assert.Equal((SkinLayer.AirPlayPower, SkinLayer.PercentText, SkinLayer.PercentText), (r.OnPower!.Value, r.OnBarItself!.Value, r.OnBareStrip!.Value));
    }

    /// <summary>While the eyedropper is on, the press on the canvas does nothing at all - no
    /// focus, no layer clicked, no drag, no capture, even on the selected element - and the release
    /// is the sample (a pick on the press would unpin the picker, whose popup then closes on the
    /// release). Off again, the same press selects as usual. Real routed events, at wherever the
    /// real pointer is: the point does not matter here.</summary>
    [Fact]
    public void The_eyedropper_samples_on_the_release_and_the_press_does_nothing()
    {
        var r = _sta.Run(() =>
        {
            var overlay = new DesignerOverlay { Scene = Scene(1), Selected = SkinLayer.PercentText, Zoom = 1 };
            var window = new Window { Content = overlay, Width = 400, Height = 200, ShowActivated = false };
            window.Show();
            try
            {
                int samples = 0, clicks = 0, drags = 0;
                overlay.SamplePicked += _ => samples++;
                overlay.LayerClicked += _ => clicks++;
                overlay.DragStarted += () => drags++;
                overlay.EyedropperActive = true;
                var cursor = overlay.Cursor;
                var press = Raise(overlay, UIElement.MouseLeftButtonDownEvent);
                var down = (samples, clicks, drags, press.Handled, overlay.IsMouseCaptured, overlay.IsFocused);
                var release = Raise(overlay, UIElement.MouseLeftButtonUpEvent);
                var up = (samples, clicks, drags, release.Handled);
                overlay.EyedropperActive = false;
                Raise(overlay, UIElement.MouseLeftButtonDownEvent);
                Raise(overlay, UIElement.MouseLeftButtonUpEvent);
                return (Cursor: cursor == Cursors.Cross, down, up, Off: (samples, clicks), CursorOff: overlay.Cursor);
            }
            finally { window.Close(); }
        });
        _out.WriteLine("expected: cross cursor; down (0 samples, 0 clicks, 0 drags, handled, not captured, not focused); " +
                       $"up (1 sample, 0 clicks, 0 drags, handled); off (still 1 sample, 1 click), no cursor; actual {r}");
        Assert.True(r.Cursor);
        Assert.Equal((0, 0, 0, true, false, false), r.down);
        Assert.Equal((1, 0, 0, true), r.up);
        Assert.Equal((1, 1), r.Off);
        Assert.Null(r.CursorOff);
    }

    private static MouseButtonEventArgs Raise(UIElement element, RoutedEvent routedEvent)
    {
        var e = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = routedEvent };
        element.RaiseEvent(e);
        return e;
    }

    /// <summary>The magnifier draws the pixels it is given ten times over, beside the pointer, at
    /// a fixed screen size whatever the zoom - and is gone, bitmap and all, once the eyedropper ends.</summary>
    [Fact]
    public void The_magnifier_follows_the_pointer_and_goes_with_the_eyedropper()
    {
        var r = _sta.Run(() =>
        {
            var green = BitmapSource.Create(3, 3, 96, 96, PixelFormats.Bgra32, null,
                Enumerable.Repeat(new byte[] { 0, 0xFF, 0, 0xFF }, 9).SelectMany(b => b).ToArray(), 12);
            Point? asked = null;
            var overlay = new DesignerOverlay
            {
                Scene = Scene(1), Zoom = 2, EyedropperActive = true,
                Magnifier = p => { asked = p; return green; },
            };
            overlay.Measure(new Size(400, 200));
            overlay.Arrange(new Rect(0, 0, 400, 200));
            overlay.MagnifyAt(new Point(50, 50));
            // At 200 % the box starts 16/2 = 8 px past the pointer and is 10*3/2 = 15 px wide.
            var inside = Pixel(overlay, 60, 60);
            var past = Pixel(overlay, 75, 75);
            overlay.EyedropperActive = false;
            return (asked, inside, past, After: Pixel(overlay, 60, 60));
        });
        _out.WriteLine($"expected: asked at 50,50; BGRA 0,255,0,255 inside the box, not past it nor after; " +
                       $"actual asked {r.asked}, inside {string.Join(",", r.inside)}, past {string.Join(",", r.past)}, after {string.Join(",", r.After)}");
        Assert.Equal(new Point(50, 50), r.asked);
        Assert.Equal(new byte[] { 0, 0xFF, 0, 0xFF }, r.inside);
        Assert.NotEqual(new byte[] { 0, 0xFF, 0, 0xFF }, r.past);
        Assert.NotEqual(new byte[] { 0, 0xFF, 0, 0xFF }, r.After);
    }

    /// <summary>One pixel of the element as drawn now: a change since its last layout is drawn first.</summary>
    private static byte[] Pixel(FrameworkElement element, int x, int y)
    {
        element.Arrange(new Rect(element.RenderSize));
        var bitmap = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var bgra = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), bgra, 4, 0);
        return bgra;
    }
}
