using System.Windows;
using System.Windows.Input;
using AorinEQ.Core;
using AorinEQ.UI;
using Xunit.Abstractions;
using Button = System.Windows.Controls.Button;
using Point = System.Windows.Point;

namespace AorinEQ.Tests;

/// <summary>The picker on a real anchor in a real window: what it shows for a colour, what each
/// part of it produces, and what it remembers.</summary>
[Collection("Wpf")]
public class ColorPickerPopupTests
{
    private readonly StaWpf _sta;
    private readonly ITestOutputHelper _out;

    public ColorPickerPopupTests(StaWpf sta, ITestOutputHelper output)
    {
        _sta = sta;
        _out = output;
    }

    private T WithPicker<T>(Func<ColorPickerPopup, Button, T> body) =>
        _sta.Run(() =>
        {
            var anchor = new Button { Width = 40, Height = 24 };
            var host = new Window { Content = anchor, Width = 200, Height = 100, ShowActivated = false };
            host.Show();
            StaWpf.Pump(); // the anchor must be loaded before a Popup will open on it
            var picker = new ColorPickerPopup();
            try { return body(picker, anchor); }
            finally { picker.IsOpen = false; StaWpf.Pump(); host.Close(); }
        });

    private static void PressEnter(UIElement element) =>
        element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(element)!, 0, Key.Enter)
        { RoutedEvent = UIElement.PreviewKeyDownEvent });

    [Fact]
    public void The_square_maps_saturation_right_and_value_up()
    {
        var cases = new (Point At, (double S, double V) Expected)[]
        {
            (new Point(180, 0), (1.0, 1.0)), (new Point(-20, 400), (0.0, 0.0)), (new Point(90, 135), (0.5, 0.25)),
            // A NaN position must not reach SkinColor.FromHsv, where it would become an arbitrary byte.
            (new Point(double.NaN, double.NaN), (0.0, 0.0)),
        };
        foreach (var (at, expected) in cases)
        {
            var actual = ColorPickerPopup.SquareToSv(at, 180);
            _out.WriteLine($"SquareToSv({at}): expected {expected}, actual {actual}");
            Assert.Equal(expected, actual);
        }
        var back = ColorPickerPopup.SvToSquare(0.5, 0.25, 180);
        _out.WriteLine($"SvToSquare(0.5, 0.25): expected 90,135, actual {back}");
        Assert.Equal(new Point(90, 135), back);
    }

    [Fact]
    public void Opening_on_a_colour_shows_its_hue_alpha_and_hex()
    {
        var r = WithPicker((picker, anchor) =>
        {
            picker.Open(anchor, "#80FF0000", ["#FF336699", "junk"]);
            StaWpf.Pump();
            return (picker.IsOpen, Hue: picker.HueSlider.Value, Alpha: picker.AlphaSlider.Value, Hex: picker.HexBox.Text,
                Recents: picker.Recents.ToArray(), Swatches: picker.RecentPanel.Children.Count);
        });
        _out.WriteLine($"expected open, hue 0, alpha 128, #80FF0000, recents [#FF336699], 1 swatch; actual {r} " +
                       $"recents [{string.Join(", ", r.Recents)}]");
        Assert.True(r.IsOpen);
        Assert.Equal((0.0, 128.0, "#80FF0000"), (r.Hue, r.Alpha, r.Hex));
        Assert.Equal(new[] { "#FF336699" }, r.Recents);
        Assert.Equal(1, r.Swatches);
    }

    [Fact]
    public void The_square_and_the_hex_box_produce_colours()
    {
        var r = WithPicker((picker, anchor) =>
        {
            var seen = new List<string>();
            picker.ColorChanged += seen.Add;
            picker.Open(anchor, "#80FF0000", []);
            picker.SetFromSquare(new Point(ColorPickerPopup.SquareSize, 0));
            var topRight = picker.Color;
            picker.SetFromSquare(new Point(0, ColorPickerPopup.SquareSize));
            var bottomLeft = picker.Color;
            picker.HexBox.Text = "#0F0";
            PressEnter(picker.HexBox);
            var typed = picker.Color;
            picker.HexBox.Text = "zz";
            PressEnter(picker.HexBox);
            return (topRight, bottomLeft, typed, AfterJunk: picker.HexBox.Text, Seen: seen.ToArray());
        });
        _out.WriteLine($"{r.topRight} {r.bottomLeft} {r.typed} junk->'{r.AfterJunk}' events [{string.Join(", ", r.Seen)}]");
        Assert.Equal("#80FF0000", r.topRight);
        Assert.Equal("#80000000", r.bottomLeft);
        Assert.Equal("#FF00FF00", r.typed);
        Assert.Equal("#FF00FF00", r.AfterJunk);
        Assert.Equal(new[] { "#80FF0000", "#80000000", "#FF00FF00" }, r.Seen);
    }

    [Fact]
    public void Closing_after_a_change_remembers_the_colour_and_closing_without_one_does_not()
    {
        var r = WithPicker((picker, anchor) =>
        {
            var pushed = new List<string[]>();
            var closes = 0;
            picker.RecentsChanged += c => pushed.Add(c.ToArray());
            picker.Closed += (_, _) => closes++;
            // Pumped after every open and close: a Popup creates its window, and raises Closed, a
            // dispatcher turn later - as it does between the click that opens it and the one that
            // closes it. Without the pump the first close would be cancelled by the second open
            // and "closing without a change" would never have been tested.
            picker.Open(anchor, "#FF336699", ["#FF336699"]);
            StaWpf.Pump();
            picker.IsOpen = false;                          // nothing changed
            StaWpf.Pump();
            var closesAfterFirst = closes;
            picker.Open(anchor, "#FF336699", ["#FF336699"]);
            StaWpf.Pump();
            picker.SetColor("#FF00FF00");
            picker.IsOpen = false;
            StaWpf.Pump();
            return (Pushed: pushed, closesAfterFirst, Closes: closes);
        });
        _out.WriteLine($"expected 1 close then 2, one push [#FF00FF00, #FF336699]; actual closes {r.closesAfterFirst} then {r.Closes}, " +
                       $"pushes: {string.Join(" | ", r.Pushed.Select(p => string.Join(", ", p)))}");
        Assert.Equal((1, 2), (r.closesAfterFirst, r.Closes));
        Assert.Single(r.Pushed);
        Assert.Equal(new[] { "#FF00FF00", "#FF336699" }, r.Pushed[0]);
    }

    [Fact]
    public void Opening_for_another_field_while_open_remembers_the_colour_left_behind()
    {
        var r = WithPicker((picker, anchor) =>
        {
            var pushed = new List<string[]>();
            picker.RecentsChanged += c => pushed.Add(c.ToArray());
            picker.Open(anchor, "#FF336699", []);
            StaWpf.Pump();
            picker.SetColor("#FF00FF00");
            // A second field asks with the recents the host has stored - read before the colour
            // left behind was pushed - and no close in between.
            picker.Open(anchor, "#FFFF0000", ["#FF336699"]);
            StaWpf.Pump();
            var afterReopen = pushed.Count;
            picker.IsOpen = false;                              // nothing changed in the second opening
            StaWpf.Pump();
            return (Pushed: pushed, afterReopen, picker.IsOpen, Shown: picker.Recents.ToArray());
        });
        _out.WriteLine($"expected one push [#FF00FF00, #FF336699] on reopen and none on close; actual {r.afterReopen} on reopen, " +
                       $"{r.Pushed.Count} total: {string.Join(" | ", r.Pushed.Select(p => string.Join(", ", p)))}; recents [{string.Join(", ", r.Shown)}]");
        Assert.Equal(1, r.afterReopen);
        Assert.Single(r.Pushed);
        Assert.Equal(new[] { "#FF00FF00", "#FF336699" }, r.Pushed[0]);
        Assert.Equal(new[] { "#FF00FF00", "#FF336699" }, r.Shown);
        Assert.False(r.IsOpen);
    }

    /// <summary>Everything the picker tells its host, in order, as "color #..", "closed" and
    /// "recents [..]".</summary>
    private static List<string> Record(ColorPickerPopup picker)
    {
        var log = new List<string>();
        picker.ColorChanged += c => log.Add("color " + c);
        picker.Closed += (_, _) => log.Add("closed");
        picker.RecentsChanged += c => log.Add($"recents [{string.Join(", ", c)}]");
        return log;
    }

    [Fact]
    public void A_typed_hex_not_yet_entered_is_committed_before_the_host_hears_the_picker_closed()
    {
        var log = WithPicker((picker, anchor) =>
        {
            var log = Record(picker);
            picker.Open(anchor, "#FF336699", ["#FF336699"]);
            StaWpf.Pump();
            picker.HexBox.Text = "#0F0";                    // typed, no Enter, focus never left
            picker.IsOpen = false;
            StaWpf.Pump();
            return log.ToArray();             // before teardown closes the picker again
        });
        _out.WriteLine($"expected [color #FF00FF00, closed, recents [#FF00FF00, #FF336699]]; actual [{string.Join(", ", log)}]");
        Assert.Equal(new[] { "color #FF00FF00", "closed", "recents [#FF00FF00, #FF336699]" }, log);
    }

    [Fact]
    public void A_typed_hex_not_yet_entered_is_committed_before_the_picker_reopens()
    {
        var log = WithPicker((picker, anchor) =>
        {
            var log = Record(picker);
            picker.Open(anchor, "#FF336699", ["#FF336699"]);
            StaWpf.Pump();
            picker.HexBox.Text = "#0F0";
            picker.Open(anchor, "#FFFF0000", ["#FF336699"]); // another field asks
            StaWpf.Pump();
            return log.ToArray();             // before teardown closes the picker again
        });
        _out.WriteLine($"expected [color #FF00FF00, recents [#FF00FF00, #FF336699]]; actual [{string.Join(", ", log)}]");
        Assert.Equal(new[] { "color #FF00FF00", "recents [#FF00FF00, #FF336699]" }, log);
    }

    [Fact]
    public void A_host_that_closes_and_opens_in_one_turn_still_remembers_the_colour()
    {
        var log = WithPicker((picker, anchor) =>
        {
            var log = Record(picker);
            picker.Open(anchor, "#FF336699", []);
            StaWpf.Pump();
            picker.SetColor("#FF00FF00");
            picker.IsOpen = false;                          // the HUD closes its menu, then opens the picker
            picker.Open(anchor, "#FFFF0000", ["#FF336699"]);
            StaWpf.Pump();
            return log.ToArray();             // before teardown closes the picker again
        });
        _out.WriteLine($"expected [color #FF00FF00, recents [#FF00FF00, #FF336699]]; actual [{string.Join(", ", log)}]");
        Assert.Equal(new[] { "color #FF00FF00", "recents [#FF00FF00, #FF336699]" }, log);
    }

    [Fact]
    public void The_first_opening_remembers_nothing()
    {
        var log = WithPicker((picker, anchor) =>
        {
            var log = Record(picker);
            picker.Open(anchor, "#FF336699", ["#FF808080"]);
            StaWpf.Pump();
            return (Log: log.ToArray(), Shown: picker.Recents.ToArray());
        });
        _out.WriteLine($"expected no events and recents [#FF808080]; actual [{string.Join(", ", log.Log)}], recents [{string.Join(", ", log.Shown)}]");
        Assert.Empty(log.Log);
        Assert.Equal(new[] { "#FF808080" }, log.Shown);
    }

    [Fact]
    public void A_grey_keeps_the_hue_the_user_had_and_the_eyedropper_can_be_hidden()
    {
        var r = WithPicker((picker, anchor) =>
        {
            picker.Open(anchor, "#FF0000FF", []);
            picker.SetColor("#FF808080");
            var hue = picker.HueSlider.Value;
            picker.SetFromSquare(new Point(ColorPickerPopup.SquareSize, 0)); // back out of the grey
            var leftGrey = picker.Color;
            picker.EyedropperAvailable = false;
            return (hue, leftGrey, picker.EyedropperButton.Visibility);
        });
        _out.WriteLine($"expected hue 240, #FF0000FF after leaving the grey, the eyedropper Collapsed; " +
                       $"actual hue {r.hue}, {r.leftGrey}, eyedropper {r.Visibility}");
        Assert.Equal(240.0, r.hue);
        Assert.Equal("#FF0000FF", r.leftGrey);
        Assert.Equal(Visibility.Collapsed, r.Visibility);
    }
}
