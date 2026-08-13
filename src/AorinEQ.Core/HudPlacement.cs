namespace AorinEQ.Core;

/// <summary>A rectangle in PHYSICAL PIXELS. Its own type rather than System.Windows.Rect so the
/// placement maths lives in Core beside the record it operates on, and can be tested without a
/// WPF dispatcher.
///
/// Pixels rather than DIPs because the process is per-monitor DPI aware: a DIP means a different
/// distance on each screen, so a widget dragged across a DPI boundary would change size and drift
/// away from the pointer. Pixels are the one coordinate space both screens agree on. WPF still
/// rescales the widget's CONTENT for the new DPI by itself, which is what keeps it sharp.</summary>
public readonly record struct HudRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;

    public bool Contains(HudRect other) =>
        other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;

    /// <summary>Overlap area with <paramref name="other"/>, 0 when they do not meet. Used to
    /// decide which screen a box dropped across a boundary actually belongs to.</summary>
    public double IntersectionArea(HudRect other)
    {
        double w = Math.Min(Right, other.Right) - Math.Max(X, other.X);
        double h = Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y);
        return w <= 0 || h <= 0 ? 0 : w * h;
    }
}

/// <summary>One screen, identified by its DEVICE PATH.</summary>
/// <param name="DeviceId">The stable identity — a display device interface path where Windows
/// gives one, falling back to the adapter\monitor name. Never an index.</param>
/// <param name="Name">What to call it in the UI.</param>
/// <param name="Bounds">Full screen rectangle in DIPs, in virtual-desktop coordinates.</param>
/// <param name="WorkArea">The part not covered by the taskbar. Widgets are placed against this.</param>
public readonly record struct HudMonitor(
    string DeviceId, string Name, HudRect Bounds, HudRect WorkArea, bool IsPrimary);

/// <summary>Where a widget actually goes: the screen it landed on, its box in virtual-desktop
/// coordinates, and whether it had to be moved because the screen it remembered is gone.</summary>
public readonly record struct HudPlaced(HudMonitor Monitor, HudRect Bounds, bool MovedToFallback);

/// <summary>Turns a persisted widget record into a real on-screen box, and back.
///
/// The rule the whole thing exists for: a widget must not jump when a display is added or removed
/// or when the user docks. Identity is a device path, position is relative to that screen's work
/// area, and when the remembered screen really is absent the widget lands on the primary AND SAYS
/// SO — visibly moved beats silently lost.</summary>
public static class HudPlacement
{
    /// <summary>The placement, or null when the machine reports no screens at all (a locked or
    /// headless session). Null rather than an invented rectangle: there is nowhere to put it.</summary>
    public static HudPlaced? TryResolve(HudWidget widget, IReadOnlyList<HudMonitor> monitors)
    {
        if (monitors.Count == 0) return null;

        int index = string.IsNullOrEmpty(widget.MonitorId)
            ? -1
            : IndexOf(monitors, m => m.DeviceId == widget.MonitorId);

        // An empty MonitorId is a widget that has never been placed. Landing on the primary is
        // where it was always going, so flagging it as "moved" would cry wolf on every new widget.
        bool moved = index < 0 && !string.IsNullOrEmpty(widget.MonitorId);
        if (index < 0)
        {
            // Defensive fallback to the first entry: EnumDisplayMonitors has always reported a
            // primary, but a widget must not become unreachable on the day it does not.
            int primary = IndexOf(monitors, m => m.IsPrimary);
            index = primary >= 0 ? primary : 0;
        }

        var target = monitors[index];
        var wa = target.WorkArea;
        double w = Math.Min(widget.Width, wa.Width);
        double h = Math.Min(widget.Height, wa.Height);
        double x = Math.Clamp(wa.X + widget.X, wa.X, Math.Max(wa.X, wa.Right - w));
        double y = Math.Clamp(wa.Y + widget.Y, wa.Y, Math.Max(wa.Y, wa.Bottom - h));

        return new HudPlaced(target, new HudRect(x, y, w, h), moved);
    }

    /// <summary>Index of the first match, or -1. A local helper because HudMonitor is a struct:
    /// FirstOrDefault would hand back a default-valued monitor that is indistinguishable from a
    /// real one with an empty id.</summary>
    private static int IndexOf(IReadOnlyList<HudMonitor> monitors, Func<HudMonitor, bool> match)
    {
        for (int i = 0; i < monitors.Count; i++)
            if (match(monitors[i])) return i;
        return -1;
    }

    /// <summary>As <see cref="TryResolve"/>, for callers that already know a screen exists.</summary>
    /// <exception cref="InvalidOperationException">No monitors were supplied.</exception>
    public static HudPlaced Resolve(HudWidget widget, IReadOnlyList<HudMonitor> monitors) =>
        TryResolve(widget, monitors)
        ?? throw new InvalidOperationException("No display monitors to place a widget on.");

    /// <summary>The inverse: an on-screen box (after a drag or a resize) recorded back onto the
    /// widget, against whichever screen holds most of it.</summary>
    public static HudWidget Capture(HudWidget widget, HudRect bounds, IReadOnlyList<HudMonitor> monitors)
    {
        if (monitors.Count == 0) return widget;

        var host = monitors
            .OrderByDescending(m => bounds.IntersectionArea(m.WorkArea))
            .ThenByDescending(m => m.IsPrimary)
            .First();
        // Entirely off every work area (dragged onto a taskbar, say): fall back to the screen
        // whose CENTRE is nearest, rather than recording it against an arbitrary first entry.
        if (bounds.IntersectionArea(host.WorkArea) <= 0)
            host = monitors
                .OrderBy(m => Math.Abs(m.WorkArea.CenterX - bounds.CenterX)
                    + Math.Abs(m.WorkArea.CenterY - bounds.CenterY))
                .First();

        return widget with
        {
            MonitorId = host.DeviceId,
            X = (int)Math.Round(bounds.X - host.WorkArea.X),
            Y = (int)Math.Round(bounds.Y - host.WorkArea.Y),
            Width = (int)Math.Round(bounds.Width),
            Height = (int)Math.Round(bounds.Height),
        };
    }
}

/// <summary>Edge and widget snapping for edit-mode drags. Without it, lining two widgets up is a
/// pixel-hunting exercise; with it, a drag that gets close finishes the job.</summary>
public static class HudSnap
{
    /// <summary>How near an edge has to be before it pulls, in DEVICE-INDEPENDENT PIXELS —
    /// this is a distance the user perceives, so it must not shrink on a high-DPI display. Boxes
    /// here are in physical pixels, so the caller scales this by the target monitor's DPI.</summary>
    public const double DefaultThreshold = 12;

    /// <summary>Moves <paramref name="box"/> to the nearest screen edge or neighbouring widget
    /// edge within <paramref name="threshold"/>. The SIZE is never changed — snapping must move a
    /// widget, never quietly resize it.</summary>
    public static HudRect Apply(HudRect box, HudRect workArea, IReadOnlyList<HudRect> others,
        double threshold)
    {
        double x = box.X, y = box.Y;

        // Candidate positions for this box's LEFT edge, and for its TOP edge, each paired with
        // the guide it would land against.
        var xTargets = new List<double> { workArea.X, workArea.Right - box.Width };
        var yTargets = new List<double> { workArea.Y, workArea.Bottom - box.Height };
        foreach (var o in others)
        {
            xTargets.Add(o.X);                 // left edges aligned
            xTargets.Add(o.Right);             // butted against its right edge
            xTargets.Add(o.X - box.Width);     // butted against its left edge
            xTargets.Add(o.Right - box.Width); // right edges aligned
            yTargets.Add(o.Y);
            yTargets.Add(o.Bottom);
            yTargets.Add(o.Y - box.Height);
            yTargets.Add(o.Bottom - box.Height);
        }

        x = Nearest(x, xTargets, threshold);
        y = Nearest(y, yTargets, threshold);
        return box with { X = x, Y = y };
    }

    private static double Nearest(double value, IReadOnlyList<double> targets, double threshold)
    {
        double best = value, bestDelta = threshold;
        foreach (var t in targets)
        {
            double delta = Math.Abs(t - value);
            if (delta < bestDelta)
            {
                bestDelta = delta;
                best = t;
            }
        }
        return best;
    }
}
