using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AorinEQ.Core;

// WPF and WinForms are both referenced by this project, so the names below are
// ambiguous without an explicit alias. Every one of these is the WPF type.
using Color = System.Windows.Media.Color;
using Cursor = System.Windows.Input.Cursor;
using Cursors = System.Windows.Input.Cursors;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace AorinEQ.UI;

/// <summary>One HUD widget: a layered, borderless, always-on-top tool window that holds one
/// widget view and nothing else.
///
/// ONE WINDOW PER WIDGET, not a single fullscreen overlay. A fullscreen always-on-top surface
/// fights exclusive-fullscreen games, forces every widget into one z-order, and makes per-widget
/// click-through impossible — the last of which is the whole point of live mode.
///
/// GEOMETRY IS PHYSICAL PIXELS, driven by SetWindowPos and read back by GetWindowRect, never by
/// Window.Left/Top/Width/Height. The process is PerMonitorV2, so WPF's own units are the DIPs of
/// whichever monitor the window is on — and a widget dragged from a 100% screen to a 150% one
/// would then change size and drift away from the pointer halfway across. Pixels are the one
/// coordinate space that means the same thing on both screens; WPF still rescales the CONTENT for
/// the new DPI on its own, which is what keeps a dragged widget sharp instead of blurred.
///
/// EDIT MODE gives the window a visible frame, a grab area over its whole surface, eight-way
/// resize edges and a context menu. LIVE MODE takes all of that away at the WINDOW HANDLE level
/// (WS_EX_TRANSPARENT), so the widget is invisible to input rather than merely ignoring it.</summary>
internal sealed class HudWidgetWindow : Window
{
    /// <summary>How wide the resize band along each edge is, in DIPs. Wide enough to hit without
    /// aiming, narrow enough not to eat the middle of a small widget.</summary>
    public const double ResizeBand = 8;

    private readonly Border _chrome;
    private readonly Border _editFrame;
    private readonly TextBlock _editLabel;
    private readonly IHudWidgetView _view;

    private bool _editMode;
    private DragKind _drag = DragKind.None;
    private POINT _dragOrigin;
    private HudRect _dragStartBounds;

    /// <summary>The widget record this window is showing. Replaced whenever the layout changes.</summary>
    public HudWidget Widget { get; private set; }

    /// <summary>The view inside — the thing that actually draws.</summary>
    public IHudWidgetView View => _view;

    /// <summary>Raised when the user finishes moving or resizing, with the window's new box in
    /// physical pixels. The HUD turns that back into a stored record.</summary>
    public event Action<HudWidgetWindow, HudRect>? BoxChanged;

    /// <summary>Raised DURING a move, so the HUD can apply snapping live rather than only on drop.</summary>
    public event Func<HudWidgetWindow, HudRect, HudRect>? BoxDragging;

    /// <summary>Raised when the user asks for this widget's settings (right-click in edit mode).</summary>
    public event Action<HudWidgetWindow>? SettingsRequested;

    /// <summary>Raised when the user removes this widget (Delete key, or the context menu).</summary>
    public event Action<HudWidgetWindow>? RemoveRequested;

    /// <summary>Raised when the widget is pressed in edit mode — the HUD brings it to the front.</summary>
    public event Action<HudWidgetWindow>? Pressed;

    private enum DragKind { None, Move, ResizeLeft, ResizeRight, ResizeTop, ResizeBottom,
        ResizeTopLeft, ResizeTopRight, ResizeBottomLeft, ResizeBottomRight }

    public HudWidgetWindow(HudWidget widget, IHudWidgetView view)
    {
        Widget = widget;
        _view = view;

        WindowStyle = WindowStyle.None;
        // NoResize, because resizing is driven by hand below: Windows would otherwise draw a
        // sizing frame on a layered window and fight the widget's own rounded chrome.
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        SizeToContent = SizeToContent.Manual;
        // Never in alt-tab and never focused: WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, applied to the
        // handle in SourceInitialized below.

        _chrome = new Border
        {
            CornerRadius = new CornerRadius(6),
            Child = (UIElement)view,
            SnapsToDevicePixels = true,
        };
        _editFrame = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false, // the frame is decoration; the WINDOW takes the drag
        };
        _editLabel = new TextBlock
        {
            Margin = new Thickness(4),
            Padding = new Thickness(5, 2, 5, 2),
            FontSize = 11,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = System.Windows.VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };

        var root = new Grid();
        root.Children.Add(_chrome);
        root.Children.Add(_editFrame);
        root.Children.Add(_editLabel);
        Content = root;

        SourceInitialized += (_, _) => HudWindowStyle.MakeToolWindow(this, clickThrough: !_editMode);
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        MouseRightButtonUp += OnRightClick;
        LostMouseCapture += (_, _) => _drag = DragKind.None;
        KeyDown += OnKeyDown;
        ApplyWidget(widget);
    }

    /// <summary>Pushes a (possibly changed) record into the window: style knobs to the view, and
    /// the background the widget's own drawing sits on. Position and size are applied by the HUD,
    /// which is the only thing that knows about monitors.</summary>
    public void ApplyWidget(HudWidget widget)
    {
        Widget = widget;
        _chrome.Background = new SolidColorBrush(Color.FromArgb(
            (byte)Math.Round(Math.Clamp(widget.Opacity, 0, 1) * 255), 0x10, 0x10, 0x14));
        _editLabel.Text = HudWidgetTypes.DisplayName(widget.Type);
        _view.Apply(widget);
    }

    /// <summary>Switches between edit and live. The click-through flag is set on the HANDLE, so
    /// this is a real change to what Windows does with a click and not merely a WPF one — which is
    /// why the release verifies it by hit-testing the desktop through a widget.</summary>
    public void SetEditMode(bool edit)
    {
        _editMode = edit;
        HudWindowStyle.SetClickThrough(this, clickThrough: !edit);
        _editFrame.Visibility = edit ? Visibility.Visible : Visibility.Collapsed;
        // The affordance exists so a widget whose data is SILENT is still visible to grab. Without
        // it, a spectrum over silence in edit mode is an invisible window nobody can find.
        _editLabel.Visibility = edit ? Visibility.Visible : Visibility.Collapsed;
        // Leaving edit mode mid-drag must COMMIT the drag, not abandon it: the window is already
        // where the user dragged it, and merely clearing the flag would leave hud.json describing
        // the old box — so the widget would jump back at the next Apply or restart.
        if (!edit) EndDrag();
        Cursor = null;
    }

    /// <summary>Applies the palette so the edit affordance is legible in both themes.</summary>
    public void ApplyPalette(EqPalette palette)
    {
        _editFrame.BorderBrush = Freeze(palette.NodeSelected);
        _editLabel.Foreground = Freeze(palette.Text);
        _editLabel.Background = Freeze(palette.PanelBackground);
        _view.ApplyPalette(palette);
    }

    private static SolidColorBrush Freeze(System.Drawing.Color c)
    {
        var b = new SolidColorBrush(ToMediaColor(c));
        b.Freeze();
        return b;
    }

    internal static Color ToMediaColor(System.Drawing.Color c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    // ---- native geometry ----

    /// <summary>The window's box in PHYSICAL PIXELS, straight from Windows.</summary>
    public HudRect Box
    {
        get
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r))
                return new HudRect(0, 0, 0, 0);
            return new HudRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }
    }

    /// <summary>Moves and sizes the window in PHYSICAL PIXELS, without activating it — a widget
    /// that stole focus every time the layout was applied would be unusable.
    ///
    /// Deliberately does NOT restack: passing HWND_TOPMOST here would lift this window to the top
    /// of the topmost band as a side effect of being MOVED, so the last widget to be placed would
    /// always win regardless of the order the user chose. Stacking is <see cref="BringToTop"/>'s
    /// job and the HUD applies it in Z order.</summary>
    public void SetBox(HudRect box)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, IntPtr.Zero,
            (int)Math.Round(box.X), (int)Math.Round(box.Y),
            (int)Math.Round(box.Width), (int)Math.Round(box.Height),
            SWP_NOACTIVATE | SWP_NOZORDER);
    }

    /// <summary>Lifts this window to the top of the always-on-top band. Called for each widget in
    /// ascending Z, so the highest Z is the last one lifted and therefore the one in front.
    /// Without this, "bring to front" changed a number in a file and nothing on screen, and a
    /// widget could sit permanently buried under another one with no way to reach it.</summary>
    public void BringToTop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE);
    }

    // ---- edit-mode input ----

    private DragKind HitKind(System.Windows.Point p)
    {
        bool left = p.X <= ResizeBand, right = p.X >= ActualWidth - ResizeBand;
        bool top = p.Y <= ResizeBand, bottom = p.Y >= ActualHeight - ResizeBand;
        return (left, right, top, bottom) switch
        {
            (true, _, true, _) => DragKind.ResizeTopLeft,
            (_, true, true, _) => DragKind.ResizeTopRight,
            (true, _, _, true) => DragKind.ResizeBottomLeft,
            (_, true, _, true) => DragKind.ResizeBottomRight,
            (true, _, _, _) => DragKind.ResizeLeft,
            (_, true, _, _) => DragKind.ResizeRight,
            (_, _, true, _) => DragKind.ResizeTop,
            (_, _, _, true) => DragKind.ResizeBottom,
            _ => DragKind.Move,
        };
    }

    private static Cursor CursorFor(DragKind kind) => kind switch
    {
        DragKind.ResizeLeft or DragKind.ResizeRight => Cursors.SizeWE,
        DragKind.ResizeTop or DragKind.ResizeBottom => Cursors.SizeNS,
        DragKind.ResizeTopLeft or DragKind.ResizeBottomRight => Cursors.SizeNWSE,
        DragKind.ResizeTopRight or DragKind.ResizeBottomLeft => Cursors.SizeNESW,
        _ => Cursors.SizeAll,
    };

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_editMode) return;
        Pressed?.Invoke(this);
        _drag = HitKind(e.GetPosition(this));
        // The cursor is read in SCREEN pixels for the whole drag, so the maths never depends on
        // the window's own (moving) origin or on the DPI of the screen it happens to be over.
        if (!GetCursorPos(out _dragOrigin)) { _drag = DragKind.None; return; }
        _dragStartBounds = Box;
        if (!CaptureMouse()) _drag = DragKind.None;
        e.Handled = true;
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_editMode) return;
        if (_drag == DragKind.None)
        {
            Cursor = CursorFor(HitKind(e.GetPosition(this)));
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag();
            return;
        }
        if (!GetCursorPos(out var now)) return;

        var box = Apply(_dragStartBounds, _drag, now.X - _dragOrigin.X, now.Y - _dragOrigin.Y);
        // Snapping applies to a MOVE only. Snapping a resize would silently change the size the
        // user is in the middle of choosing.
        if (_drag == DragKind.Move && BoxDragging is not null)
            box = BoxDragging(this, box);
        SetBox(box);
    }

    private static HudRect Apply(HudRect start, DragKind kind, double dx, double dy)
    {
        double x = start.X, y = start.Y, w = start.Width, h = start.Height;
        switch (kind)
        {
            case DragKind.Move: x += dx; y += dy; break;
            case DragKind.ResizeLeft: x += dx; w -= dx; break;
            case DragKind.ResizeRight: w += dx; break;
            case DragKind.ResizeTop: y += dy; h -= dy; break;
            case DragKind.ResizeBottom: h += dy; break;
            case DragKind.ResizeTopLeft: x += dx; w -= dx; y += dy; h -= dy; break;
            case DragKind.ResizeTopRight: w += dx; y += dy; h -= dy; break;
            case DragKind.ResizeBottomLeft: x += dx; w -= dx; h += dy; break;
            case DragKind.ResizeBottomRight: w += dx; h += dy; break;
        }
        // A resize that would invert the box stops at the minimum instead, with the moving edge
        // held — dragging past the far edge must not flip the widget inside out.
        if (w < HudWidget.MinSize)
        {
            if (kind is DragKind.ResizeLeft or DragKind.ResizeTopLeft or DragKind.ResizeBottomLeft)
                x = start.X + start.Width - HudWidget.MinSize;
            w = HudWidget.MinSize;
        }
        if (h < HudWidget.MinSize)
        {
            if (kind is DragKind.ResizeTop or DragKind.ResizeTopLeft or DragKind.ResizeTopRight)
                y = start.Y + start.Height - HudWidget.MinSize;
            h = HudWidget.MinSize;
        }
        return new HudRect(x, y, w, h);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e) => EndDrag();

    private void EndDrag()
    {
        if (_drag == DragKind.None) return;
        _drag = DragKind.None;
        if (IsMouseCaptured) ReleaseMouseCapture();
        BoxChanged?.Invoke(this, Box);
    }

    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (!_editMode) return;
        SettingsRequested?.Invoke(this);
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!_editMode) return;
        if (e.Key == Key.Delete)
        {
            RemoveRequested?.Invoke(this);
            e.Handled = true;
        }
    }

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }
}
