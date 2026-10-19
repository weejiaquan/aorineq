using System.IO;
using System.Windows;
using System.Windows.Controls;
using AorinEQ.Core;
using AorinEQ.UI;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>A bar colour picked from a real widget's menu, in the real picker, lands in the real
/// layout file's store - and the recents the HUD shares with the designer move with it.</summary>
[Collection("Wpf")]
public class HudColourPickerTests : IDisposable
{
    private readonly StaWpf _sta;
    private readonly ITestOutputHelper _out;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aorineq-hudpicker-" + Guid.NewGuid().ToString("N"));

    public HudColourPickerTests(StaWpf sta, ITestOutputHelper output)
    {
        _sta = sta;
        _out = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private T WithHud<T>(Func<HudManager, HudWidgetWindow, HudStore, List<string[]>, T> body) =>
        _sta.Run(() =>
        {
            var store = new HudStore(Path.Combine(_dir, "hud.json"));
            // Hidden, so the HUD builds no window and opens no audio capture of its own.
            store.Update(l => l.Add(HudWidget.Create(HudWidgetTypes.Spectrum) with { Visible = false }));
            var widget = store.Layout.Widgets.Single();
            using var pipeline = new SharedAudioPipeline();
            var hud = new HudManager(store, pipeline, () => new HudManager.HudRuntimeState(50, false, null, "", []));
            var pushed = new List<string[]>();
            hud.RecentColors = () => ["#FF336699"];
            hud.RecentColorsChanged += c => pushed.Add(c.ToArray());
            var window = new HudWidgetWindow(widget, new HudSpectrumView());
            window.Show();
            try { return body(hud, window, store, pushed); }
            finally
            {
                window.Close();
                hud.Dispose();
            }
        });

    private static void ClickLowColour(HudManager hud, HudWidgetWindow window)
    {
        var menu = HudWidgetMenu.Build(hud, window);
        var item = menu.Items.OfType<MenuItem>().Single(i => (string)i.Header == Loc.T("hud.menu.bar-colour-low"));
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    [Fact]
    public void A_bar_colour_is_picked_on_the_widget_and_lands_in_the_layout()
    {
        var r = WithHud((hud, window, store, pushed) =>
        {
            var before = store.Layout.Widgets.Single().ColorStart;
            ClickLowColour(hud, window);
            var picker = hud.ColourPicker!;
            var opened = (picker.IsOpen, OnWidget: ReferenceEquals(picker.PlacementTarget, window),
                picker.EyedropperAvailable, Recents: picker.Recents.ToArray(), picker.HexBox.Text);
            picker.SetColor("#FF00FF00");
            picker.IsOpen = false;
            StaWpf.Pump();
            return (before, opened, After: store.Layout.Widgets.Single().ColorStart, Pushed: pushed.ToArray());
        });
        _out.WriteLine($"{r.before} -> {r.After}; opened {r.opened}; pushed {string.Join(" | ", r.Pushed.Select(p => string.Join(", ", p)))}");
        Assert.True(r.opened.IsOpen);
        Assert.True(r.opened.OnWidget);
        Assert.False(r.opened.EyedropperAvailable);
        Assert.Equal(new[] { "#FF336699" }, r.opened.Recents);
        Assert.Equal(SkinColor.Format(r.before), r.opened.Text);
        Assert.Equal("#FF00FF00", r.After);
        Assert.Equal(new[] { "#FF00FF00", "#FF336699" }, Assert.Single(r.Pushed));
    }

    private static void ClickColour(HudManager hud, HudWidgetWindow window, string header)
    {
        var menu = HudWidgetMenu.Build(hud, window);
        menu.Items.OfType<MenuItem>().Single(i => (string)i.Header == Loc.T(header))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    [Fact]
    public void Reopening_in_the_same_turn_still_applies_the_first_colour_and_the_second()
    {
        var r = WithHud((hud, window, store, pushed) =>
        {
            var before = store.Layout.Widgets.Single();
            ClickColour(hud, window, "hud.menu.bar-colour-low");
            hud.ColourPicker!.SetColor("#FF00FF00");
            // No Pump: the first opening's Closed has not been raised, and never may be.
            ClickColour(hud, window, "hud.menu.bar-colour-high");
            var mid = store.Layout.Widgets.Single();
            hud.ColourPicker!.SetColor("#FFFF0000");
            hud.ColourPicker!.IsOpen = false;
            StaWpf.Pump();
            return (before, mid, After: store.Layout.Widgets.Single(), Pushed: pushed.Count);
        });
        _out.WriteLine($"low {r.before.ColorStart} -> {r.mid.ColorStart} -> {r.After.ColorStart}; high {r.before.ColorEnd} -> {r.mid.ColorEnd} -> {r.After.ColorEnd}; pushes {r.Pushed}");
        Assert.Equal("#FF00FF00", r.mid.ColorStart);
        Assert.Equal(r.before.ColorEnd, r.mid.ColorEnd);
        Assert.Equal("#FF00FF00", r.After.ColorStart);
        Assert.Equal("#FFFF0000", r.After.ColorEnd);
    }

    [Fact]
    public void Disposing_with_the_picker_open_applies_the_pick_and_pushes_the_recents()
    {
        var r = _sta.Run(() =>
        {
            var path = Path.Combine(_dir, "dispose.json");
            var store = new HudStore(path);
            store.Update(l => l.Add(HudWidget.Create(HudWidgetTypes.Spectrum) with { Visible = false }));
            var widget = store.Layout.Widgets.Single();
            using var pipeline = new SharedAudioPipeline();
            var hud = new HudManager(store, pipeline, () => new HudManager.HudRuntimeState(50, false, null, "", []));
            var pushed = new List<string[]>();
            hud.RecentColors = () => [];
            hud.RecentColorsChanged += c => pushed.Add(c.ToArray());
            var window = new HudWidgetWindow(widget, new HudSpectrumView());
            window.Show();
            ClickLowColour(hud, window);
            hud.ColourPicker!.SetColor("#FF00FF00");
            hud.Dispose(); // no Pump: Closed has not been raised and the store is gone after this
            window.Close();
            var onDisk = new HudStore(path).Layout.Widgets.Single().ColorStart;
            return (onDisk, Pushed: pushed.ToArray(), Picker: hud.ColourPicker);
        });
        _out.WriteLine($"on disk {r.onDisk}; pushed {string.Join(" | ", r.Pushed.Select(p => string.Join(", ", p)))}; picker released {r.Picker is null}");
        Assert.Equal("#FF00FF00", r.onDisk);
        Assert.Equal(new[] { "#FF00FF00" }, Assert.Single(r.Pushed));
        Assert.Null(r.Picker);
    }

    [Fact]
    public void Leaving_edit_mode_with_the_picker_open_applies_the_pick_at_once()
    {
        var r = WithHud((hud, window, store, pushed) =>
        {
            hud.SetMode(HudModes.Edit);
            ClickLowColour(hud, window);
            hud.ColourPicker!.SetColor("#FF00FF00");
            hud.SetMode(HudModes.Live); // no Pump
            return (After: store.Layout.Widgets.Single().ColorStart, Open: hud.ColourPicker.IsOpen, Pushed: pushed.Count);
        });
        _out.WriteLine($"after {r.After}; open {r.Open}; pushed {r.Pushed}");
        Assert.Equal("#FF00FF00", r.After);
        Assert.False(r.Open);
        Assert.Equal(1, r.Pushed);
    }

    [Fact]
    public void Closing_the_picker_without_a_change_changes_nothing()
    {
        var r = WithHud((hud, window, store, pushed) =>
        {
            var before = store.Layout;
            ClickLowColour(hud, window);
            hud.ColourPicker!.IsOpen = false;
            StaWpf.Pump();
            return (Same: ReferenceEquals(before, store.Layout), Pushed: pushed.Count);
        });
        _out.WriteLine($"expected the same layout and no recents pushed; actual same={r.Same}, pushed {r.Pushed}");
        Assert.True(r.Same);
        Assert.Equal(0, r.Pushed);
    }
}
