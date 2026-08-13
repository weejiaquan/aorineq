using System.Globalization;
using System.Windows.Controls;
using AorinEQ.Core;

namespace AorinEQ.UI;

/// <summary>The per-widget settings surface: a context menu raised by a right-click in edit mode.
///
/// A menu rather than a settings window, because these are one-tap choices on an object the user
/// is already pointing at — opening a dialog would move their attention off the widget they are
/// arranging, and every change here is meant to be seen immediately on the widget itself. Each
/// item writes straight through <see cref="HudManager.Update"/>, so the layout file and the live
/// widget never disagree.</summary>
internal static class HudWidgetMenu
{
    public static ContextMenu Build(HudManager hud, HudWidget widget)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Header(HudWidgetTypes.DisplayName(widget.Type)));
        menu.Items.Add(new Separator());

        switch (widget.Type)
        {
            case HudWidgetTypes.Spectrum: AddSpectrum(menu, hud, widget); break;
            case HudWidgetTypes.Levels: AddLevels(menu); break;
            case HudWidgetTypes.EqCurve: AddEqCurve(menu, hud, widget); break;
            case HudWidgetTypes.Volume: AddVolume(menu, hud, widget); break;
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Choices("Background", widget.Opacity,
            [("Transparent", HudWidget.MinOpacity), ("Light", 0.25), ("Medium", 0.55),
             ("Heavy", 0.8), ("Solid", 1.0)],
            v => hud.Update(widget with { Opacity = v })));
        menu.Items.Add(Item("Hide this widget", () => hud.SetVisible(widget.Id, false)));
        menu.Items.Add(Item("Remove this widget", () => hud.Remove(widget.Id)));
        return menu;
    }

    private static void AddSpectrum(ContextMenu menu, HudManager hud, HudWidget w)
    {
        menu.Items.Add(Choices("Bars", w.BandCount,
            [("16", 16), ("24", 24), ("32", 32), ("48", 48), ("64", 64), ("96", 96), ("128", 128)],
            v => hud.Update(w with { BandCount = v })));
        menu.Items.Add(Choices("Direction", w.Orientation,
            [("Left to right", HudOrientations.LeftToRight),
             ("Right to left", HudOrientations.RightToLeft),
             ("Vertical", HudOrientations.Vertical),
             ("Mirrored", HudOrientations.Mirrored)],
            v => hud.Update(w with { Orientation = v })));
        menu.Items.Add(Choices("Frequency range", (w.MinHz, w.MaxHz),
            [("20 Hz – 20 kHz (full)", (20.0, 20000.0)),
             ("20 Hz – 10 kHz", (20.0, 10000.0)),
             ("40 Hz – 16 kHz", (40.0, 16000.0)),
             ("20 Hz – 500 Hz (bass)", (20.0, 500.0))],
            v => hud.Update(w with { MinHz = v.Item1, MaxHz = v.Item2 })));
        menu.Items.Add(Choices("Falloff", w.Smoothing,
            [("None", 0.0), ("Fast", 0.35), ("Medium", 0.6), ("Slow", 0.85)],
            v => hud.Update(w with { Smoothing = v })));
        menu.Items.Add(Check("Peak hold", w.PeakHold, v => hud.Update(w with { PeakHold = v })));
        menu.Items.Add(Choices("Peak decay", w.PeakDecayDbPerSecond,
            [("Slow (12 dB/s)", 12.0), ("Medium (24 dB/s)", 24.0), ("Fast (48 dB/s)", 48.0)],
            v => hud.Update(w with { PeakDecayDbPerSecond = v })));
        menu.Items.Add(Choices("Bar gap", w.BarGap,
            [("None", 0), ("1 px", 1), ("2 px", 2), ("4 px", 4), ("8 px", 8)],
            v => hud.Update(w with { BarGap = v })));
        menu.Items.Add(Item("Bar colour (low)…",
            () => PickColour(w.ColorStart, c => hud.Update(w with { ColorStart = c }))));
        menu.Items.Add(Item("Bar colour (high)…",
            () => PickColour(w.ColorEnd, c => hud.Update(w with { ColorEnd = c }))));
    }

    private static void AddLevels(ContextMenu menu)
    {
        // The clip indicator is reset by clicking it — it is right there on the widget, and a menu
        // item for it would be a second way to do the same thing in a worse place.
        menu.Items.Add(Header("Click the clip indicator to reset it."));
    }

    private static void AddEqCurve(ContextMenu menu, HudManager hud, HudWidget w)
    {
        menu.Items.Add(Check("Band nodes", w.ShowNodes, v => hud.Update(w with { ShowNodes = v })));
        menu.Items.Add(Check("dB grid", w.ShowGrid, v => hud.Update(w with { ShowGrid = v })));
    }

    private static void AddVolume(ContextMenu menu, HudManager hud, HudWidget w)
    {
        menu.Items.Add(Choices("Skin zoom", w.Scale,
            [("50%", 0.5), ("75%", 0.75), ("100%", 1.0), ("150%", 1.5), ("200%", 2.0)],
            v => hud.Update(w with { Scale = v })));
        menu.Items.Add(Check("Show device name", w.ShowDeviceName,
            v => hud.Update(w with { ShowDeviceName = v })));
        menu.Items.Add(Header("Uses your OSD skin. Pick one in Settings → Skins."));
    }

    // ---- item builders ----

    private static MenuItem Header(string text) => new() { Header = text, IsEnabled = false };

    private static MenuItem Item(string text, Action run)
    {
        var item = new MenuItem { Header = text };
        item.Click += (_, _) => run();
        return item;
    }

    private static MenuItem Check(string text, bool value, Action<bool> set)
    {
        var item = new MenuItem { Header = text, IsCheckable = true, IsChecked = value };
        item.Click += (_, _) => set(!value);
        return item;
    }

    /// <summary>A submenu of mutually exclusive choices with the current one ticked. Discrete
    /// choices rather than sliders: this is a context menu over a live widget, and a slider in one
    /// would be a drag target competing with the drag that moves the widget.</summary>
    private static MenuItem Choices<T>(string text, T current,
        IReadOnlyList<(string Label, T Value)> options, Action<T> set)
    {
        var parent = new MenuItem { Header = text };
        foreach (var (label, value) in options)
        {
            var item = new MenuItem
            {
                Header = label,
                IsCheckable = true,
                IsChecked = EqualityComparer<T>.Default.Equals(value, current),
            };
            var chosen = value;
            item.Click += (_, _) => set(chosen);
            parent.Items.Add(item);
        }
        return parent;
    }

    /// <summary>The native colour picker, as the skin designer already uses — one colour dialog in
    /// the app, not two.</summary>
    private static void PickColour(string current, Action<string> set)
    {
        var start = HudSpectrumView.ParseColor(current, System.Windows.Media.Colors.DeepSkyBlue);
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(start.A, start.R, start.G, start.B),
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        var c = dialog.Color;
        // Alpha is carried over from the previous value: the native dialog has no alpha channel,
        // and dropping it would silently make a deliberately translucent bar opaque.
        set(string.Create(CultureInfo.InvariantCulture,
            $"#{start.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}"));
    }
}
