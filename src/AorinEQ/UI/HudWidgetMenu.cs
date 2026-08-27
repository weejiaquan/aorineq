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
        menu.Items.Add(Choices(Loc.T("hud.menu.background"), widget.Opacity,
            [(Loc.T("hud.menu.transparent"), HudWidget.MinOpacity), (Loc.T("hud.menu.light"), 0.25), (Loc.T("hud.menu.medium"), 0.55),
             (Loc.T("hud.menu.heavy"), 0.8), (Loc.T("hud.menu.solid"), 1.0)],
            v => hud.Update(widget with { Opacity = v })));
        menu.Items.Add(Item(Loc.T("hud.menu.hide-this-widget"), () => hud.SetVisible(widget.Id, false)));
        menu.Items.Add(Item(Loc.T("hud.menu.remove-this-widget"), () => hud.Remove(widget.Id)));
        return menu;
    }

    private static void AddSpectrum(ContextMenu menu, HudManager hud, HudWidget w)
    {
        menu.Items.Add(Choices(Loc.T("hud.menu.bars"), w.BandCount,
            [("16", 16), ("24", 24), ("32", 32), ("48", 48), ("64", 64), ("96", 96), ("128", 128)],
            v => hud.Update(w with { BandCount = v })));
        menu.Items.Add(Choices(Loc.T("hud.menu.direction"), w.Orientation,
            [(Loc.T("hud.menu.left-to-right"), HudOrientations.LeftToRight),
             (Loc.T("hud.menu.right-to-left"), HudOrientations.RightToLeft),
             (Loc.T("hud.menu.vertical"), HudOrientations.Vertical),
             (Loc.T("hud.menu.mirrored"), HudOrientations.Mirrored)],
            v => hud.Update(w with { Orientation = v })));
        menu.Items.Add(Choices(Loc.T("hud.menu.frequency-range"), (w.MinHz, w.MaxHz),
            [(Loc.T("hud.menu.20-hz-20-khz-full"), (20.0, 20000.0)),
             (Loc.T("hud.menu.20-hz-10-khz"), (20.0, 10000.0)),
             (Loc.T("hud.menu.40-hz-16-khz"), (40.0, 16000.0)),
             (Loc.T("hud.menu.20-hz-500-hz-bass"), (20.0, 500.0))],
            v => hud.Update(w with { MinHz = v.Item1, MaxHz = v.Item2 })));
        menu.Items.Add(Choices(Loc.T("hud.menu.falloff"), w.Smoothing,
            [(Loc.T("hud.menu.none"), 0.0), (Loc.T("hud.menu.fast"), 0.35), (Loc.T("hud.menu.medium"), 0.6), (Loc.T("hud.menu.slow"), 0.85)],
            v => hud.Update(w with { Smoothing = v })));
        menu.Items.Add(Check(Loc.T("hud.menu.peak-hold"), w.PeakHold, v => hud.Update(w with { PeakHold = v })));
        menu.Items.Add(Choices(Loc.T("hud.menu.peak-decay"), w.PeakDecayDbPerSecond,
            [(Loc.T("hud.menu.slow-12-db-s"), 12.0), (Loc.T("hud.menu.medium-24-db-s"), 24.0), (Loc.T("hud.menu.fast-48-db-s"), 48.0)],
            v => hud.Update(w with { PeakDecayDbPerSecond = v })));
        menu.Items.Add(Choices(Loc.T("hud.menu.bar-gap"), w.BarGap,
            [(Loc.T("hud.menu.none"), 0), (Loc.T("hud.menu.1-px"), 1), (Loc.T("hud.menu.2-px"), 2), (Loc.T("hud.menu.4-px"), 4), (Loc.T("hud.menu.8-px"), 8)],
            v => hud.Update(w with { BarGap = v })));
        menu.Items.Add(Item(Loc.T("hud.menu.bar-colour-low"),
            () => PickColour(w.ColorStart, c => hud.Update(w with { ColorStart = c }))));
        menu.Items.Add(Item(Loc.T("hud.menu.bar-colour-high"),
            () => PickColour(w.ColorEnd, c => hud.Update(w with { ColorEnd = c }))));
    }

    private static void AddLevels(ContextMenu menu)
    {
        // The clip indicator is reset by clicking it — it is right there on the widget, and a menu
        // item for it would be a second way to do the same thing in a worse place.
        menu.Items.Add(Header(Loc.T("hud.menu.click-the-clip-indicator-to")));
    }

    private static void AddEqCurve(ContextMenu menu, HudManager hud, HudWidget w)
    {
        menu.Items.Add(Check(Loc.T("hud.menu.band-nodes"), w.ShowNodes, v => hud.Update(w with { ShowNodes = v })));
        menu.Items.Add(Check(Loc.T("hud.menu.db-grid"), w.ShowGrid, v => hud.Update(w with { ShowGrid = v })));
    }

    private static void AddVolume(ContextMenu menu, HudManager hud, HudWidget w)
    {
        menu.Items.Add(Choices(Loc.T("hud.menu.skin-zoom"), w.Scale,
            [("50%", 0.5), ("75%", 0.75), ("100%", 1.0), ("150%", 1.5), ("200%", 2.0)],
            v => hud.Update(w with { Scale = v })));
        menu.Items.Add(Check(Loc.T("hud.menu.show-device-name"), w.ShowDeviceName,
            v => hud.Update(w with { ShowDeviceName = v })));
        menu.Items.Add(Header(Loc.T("hud.menu.uses-your-osd-skin-pick")));
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
