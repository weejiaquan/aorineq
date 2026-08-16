using AorinEQ.Core;

namespace AorinEQ.Tests;

/// <summary>Where a remembered widget actually lands. The rule the spec is built on: monitor
/// identity is a DEVICE PATH, never an index, so widgets do not jump when a display is added,
/// removed, or the user docks — and when the remembered monitor really is gone, the widget is
/// placed on the primary and FLAGGED, so the user can see it moved rather than losing it.</summary>
public class HudPlacementTests
{
    private static readonly HudMonitor Left = new(
        @"\\?\DISPLAY#DEL4321#5&1&UID1", "DELL U2415",
        new HudRect(-1920, 0, 1920, 1080), new HudRect(-1920, 0, 1920, 1040), IsPrimary: false);

    private static readonly HudMonitor Primary = new(
        @"\\?\DISPLAY#GSM5B09#5&2&UID2", "LG 27GL850",
        new HudRect(0, 0, 2560, 1440), new HudRect(0, 0, 2560, 1392), IsPrimary: true);

    private static readonly HudMonitor[] Both = [Left, Primary];

    [Fact]
    public void A_widget_on_a_present_monitor_keeps_its_exact_box()
    {
        var w = new HudWidget
        {
            Id = "a", Type = HudWidgetTypes.Levels, MonitorId = Left.DeviceId,
            X = 40, Y = 60, Width = 200, Height = 120,
        };

        var placed = HudPlacement.Resolve(w, Both);

        Assert.False(placed.MovedToFallback);
        Assert.Equal(Left.DeviceId, placed.Monitor.DeviceId);
        // X/Y are stored relative to the monitor's work area, so the same numbers mean the same
        // place after the desktop is rearranged.
        Assert.Equal(Left.WorkArea.X + 40, placed.Bounds.X);
        Assert.Equal(Left.WorkArea.Y + 60, placed.Bounds.Y);
        Assert.Equal(200, placed.Bounds.Width);
        Assert.Equal(120, placed.Bounds.Height);
    }

    [Fact]
    public void A_widget_whose_monitor_is_gone_moves_to_the_primary_and_says_so()
    {
        var w = new HudWidget
        {
            Id = "a", Type = HudWidgetTypes.Levels, MonitorId = @"\\?\DISPLAY#GONE#1&1&UID9",
            X = 10, Y = 10, Width = 200, Height = 120,
        };

        var placed = HudPlacement.Resolve(w, Both);

        Assert.True(placed.MovedToFallback);
        Assert.True(placed.Monitor.IsPrimary);
        Assert.True(Primary.WorkArea.Contains(placed.Bounds));
    }

    [Fact]
    public void An_empty_monitor_id_means_wherever_is_primary_and_is_not_a_move()
    {
        // A freshly created widget has no monitor yet. Landing on the primary is where it was
        // always going, so flagging it as "moved" would cry wolf on every new widget.
        var w = HudWidget.Create(HudWidgetTypes.Spectrum) with { MonitorId = "", X = 5, Y = 5 };

        var placed = HudPlacement.Resolve(w, Both);

        Assert.False(placed.MovedToFallback);
        Assert.True(placed.Monitor.IsPrimary);
    }

    [Fact]
    public void The_same_monitor_reached_by_a_new_index_is_still_the_same_monitor()
    {
        // The docking case: the display that used to be listed second is now listed first, and
        // its coordinates changed. Identity is the device path, so the widget follows the SCREEN,
        // not the slot it used to occupy.
        var movedLeft = Left with { Bounds = new HudRect(2560, 0, 1920, 1080), WorkArea = new HudRect(2560, 0, 1920, 1040) };
        var w = new HudWidget
        {
            Id = "a", Type = HudWidgetTypes.Levels, MonitorId = Left.DeviceId,
            X = 40, Y = 60, Width = 200, Height = 120,
        };

        var placed = HudPlacement.Resolve(w, [Primary, movedLeft]);

        Assert.False(placed.MovedToFallback);
        Assert.Equal(2560 + 40, placed.Bounds.X);
    }

    [Fact]
    public void A_widget_dragged_off_the_edge_is_pulled_back_so_it_can_always_be_grabbed_again()
    {
        var w = new HudWidget
        {
            Id = "a", Type = HudWidgetTypes.Levels, MonitorId = Primary.DeviceId,
            X = 99999, Y = -99999, Width = 200, Height = 120,
        };

        var placed = HudPlacement.Resolve(w, Both);

        Assert.True(Primary.WorkArea.Contains(placed.Bounds));
    }

    [Fact]
    public void A_widget_larger_than_the_screen_is_clamped_to_the_work_area()
    {
        var tiny = new HudMonitor("only", "Tiny", new HudRect(0, 0, 800, 600),
            new HudRect(0, 0, 800, 560), IsPrimary: true);
        var w = new HudWidget
        {
            Id = "a", Type = HudWidgetTypes.Spectrum, MonitorId = "only",
            X = 0, Y = 0, Width = 4000, Height = 4000,
        };

        var placed = HudPlacement.Resolve(w, [tiny]);

        Assert.Equal(800, placed.Bounds.Width);
        Assert.Equal(560, placed.Bounds.Height);
        Assert.True(tiny.WorkArea.Contains(placed.Bounds));
    }

    [Fact]
    public void With_no_monitors_at_all_resolve_reports_no_placement_instead_of_inventing_one()
    {
        var w = HudWidget.Create(HudWidgetTypes.Levels);
        Assert.Null(HudPlacement.TryResolve(w, []));
    }

    [Fact]
    public void Without_a_primary_flag_the_first_monitor_stands_in()
    {
        // Defensive: EnumDisplayMonitors has always reported a primary, but a widget must not
        // become unreachable if one day it does not.
        var odd = new HudMonitor("a", "A", new HudRect(0, 0, 1000, 800), new HudRect(0, 0, 1000, 760), IsPrimary: false);
        var w = HudWidget.Create(HudWidgetTypes.Levels) with { MonitorId = "missing" };

        var placed = HudPlacement.Resolve(w, [odd]);

        Assert.Equal("a", placed.Monitor.DeviceId);
        Assert.True(placed.MovedToFallback);
    }

    // ---- capture: turning a live window box back into a stored one ----

    [Fact]
    public void Capturing_a_dragged_box_stores_it_against_the_monitor_it_landed_on()
    {
        var w = HudWidget.Create(HudWidgetTypes.Levels);

        // Dropped near the top-left of the LEFT monitor.
        var stored = HudPlacement.Capture(w, new HudRect(-1900, 20, 200, 120), Both);

        Assert.Equal(Left.DeviceId, stored.MonitorId);
        Assert.Equal(20, stored.X);
        Assert.Equal(20, stored.Y);
        Assert.Equal(200, stored.Width);
        Assert.Equal(120, stored.Height);
    }

    [Fact]
    public void A_box_straddling_two_monitors_is_stored_against_the_one_holding_most_of_it()
    {
        var w = HudWidget.Create(HudWidgetTypes.Levels);

        // 160 px on the left monitor, 40 px on the primary.
        var stored = HudPlacement.Capture(w, new HudRect(-160, 100, 200, 120), Both);

        Assert.Equal(Left.DeviceId, stored.MonitorId);
    }

    [Fact]
    public void Capture_round_trips_through_Resolve_unchanged()
    {
        var w = HudWidget.Create(HudWidgetTypes.Spectrum);
        var box = new HudRect(300, 400, 320, 120);

        var stored = HudPlacement.Capture(w, box, Both);
        var placed = HudPlacement.Resolve(stored, Both);

        Assert.False(placed.MovedToFallback);
        Assert.Equal(box, placed.Bounds);
    }

    // ---- edge and widget snapping, the thing that makes dragging usable ----

    [Fact]
    public void A_drag_close_to_a_screen_edge_snaps_flush_to_it()
    {
        var box = new HudRect(Primary.WorkArea.X + 6, Primary.WorkArea.Y + 4, 200, 120);

        var snapped = HudSnap.Apply(box, Primary.WorkArea, others: [], HudSnap.DefaultThreshold);

        Assert.Equal(Primary.WorkArea.X, snapped.X);
        Assert.Equal(Primary.WorkArea.Y, snapped.Y);
    }

    [Fact]
    public void The_right_and_bottom_edges_snap_too()
    {
        var wa = Primary.WorkArea;
        var box = new HudRect(wa.Right - 200 - 5, wa.Bottom - 120 - 3, 200, 120);

        var snapped = HudSnap.Apply(box, wa, others: [], HudSnap.DefaultThreshold);

        Assert.Equal(wa.Right, snapped.Right);
        Assert.Equal(wa.Bottom, snapped.Bottom);
    }

    [Fact]
    public void A_drag_far_from_every_edge_is_left_exactly_where_the_user_put_it()
    {
        var box = new HudRect(600, 500, 200, 120);

        Assert.Equal(box, HudSnap.Apply(box, Primary.WorkArea, others: [], HudSnap.DefaultThreshold));
    }

    [Fact]
    public void Widgets_snap_to_each_other_edge_to_edge()
    {
        var neighbour = new HudRect(600, 500, 200, 120);
        // Dropped 5 px to the right of the neighbour's right edge, 4 px below its top.
        var box = new HudRect(805, 504, 150, 90);

        var snapped = HudSnap.Apply(box, Primary.WorkArea, [neighbour], HudSnap.DefaultThreshold);

        Assert.Equal(neighbour.Right, snapped.X);   // butted up against it
        Assert.Equal(neighbour.Y, snapped.Y);       // tops aligned
    }

    [Fact]
    public void Snapping_never_changes_the_size_of_the_box()
    {
        var box = new HudRect(Primary.WorkArea.X + 3, Primary.WorkArea.Y + 3, 233, 117);

        var snapped = HudSnap.Apply(box, Primary.WorkArea, [new HudRect(600, 500, 200, 120)],
            HudSnap.DefaultThreshold);

        Assert.Equal(233, snapped.Width);
        Assert.Equal(117, snapped.Height);
    }

    // ---- HudRect itself ----

    [Fact]
    public void Rect_geometry_is_what_it_says()
    {
        var r = new HudRect(10, 20, 100, 50);
        Assert.Equal(110, r.Right);
        Assert.Equal(70, r.Bottom);
        Assert.Equal(60, r.CenterX);
        Assert.Equal(45, r.CenterY);
        Assert.True(r.Contains(new HudRect(10, 20, 100, 50)));
        Assert.False(r.Contains(new HudRect(10, 20, 101, 50)));
        Assert.Equal(100 * 50, r.IntersectionArea(new HudRect(0, 0, 200, 200)));
        Assert.Equal(0, r.IntersectionArea(new HudRect(200, 200, 10, 10)));
        Assert.Equal(10 * 50, r.IntersectionArea(new HudRect(100, 0, 200, 200)));
    }

    /// <summary>The point overload is what decides whether a wheel notch belongs to a HUD volume
    /// widget. HUD widget windows are click-through in live mode, so WPF never sees the wheel over
    /// one — the low-level hook hit-tests the widget's box itself, in the same physical pixels
    /// <see cref="HudWidget"/> already stores.</summary>
    [Fact]
    public void Rect_contains_a_point_including_its_top_left_but_not_its_far_edges()
    {
        var r = new HudRect(10, 20, 100, 50); // covers x 10..109, y 20..69

        Assert.True(r.Contains(10, 20));   // top-left corner is inside
        Assert.True(r.Contains(109, 69));  // last pixel inside
        Assert.True(r.Contains(60, 45));   // middle

        // Right/Bottom are one past the last pixel — a point there belongs to the next window,
        // or two adjacent widgets would both claim the same notch.
        Assert.False(r.Contains(110, 45));
        Assert.False(r.Contains(60, 70));
        Assert.False(r.Contains(9, 45));
        Assert.False(r.Contains(60, 19));
    }

    /// <summary>A widget with no window yet reports a zero box (GetWindowRect failed), and a
    /// zero box must claim nothing — least of all the top-left pixel of the screen.</summary>
    [Fact]
    public void An_empty_rect_contains_no_point()
    {
        var r = new HudRect(0, 0, 0, 0);
        Assert.False(r.Contains(0, 0));
    }
}
