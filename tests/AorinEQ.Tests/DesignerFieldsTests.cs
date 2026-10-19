using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using AorinEQ.Core;
using AorinEQ.UI.DesignerFields;
using Xunit.Abstractions;
using TextBox = System.Windows.Controls.TextBox;

namespace AorinEQ.Tests;

/// <summary>The field controls the Properties panel is built from, each hosted in a real window
/// and driven through the same routed events a keyboard raises.</summary>
[Collection("Wpf")]
public class DesignerFieldsTests : IDisposable
{
    private readonly StaWpf _sta;
    private readonly ITestOutputHelper _out;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aorineq-fields-" + Guid.NewGuid().ToString("N"));

    public DesignerFieldsTests(StaWpf sta, ITestOutputHelper output)
    {
        _sta = sta;
        _out = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private T Hosted<TField, T>(Func<TField> create, Func<TField, T> body) where TField : FrameworkElement =>
        _sta.Run(() =>
        {
            var field = create();
            var host = new Window { Content = field, Width = 420, Height = 220, ShowActivated = false };
            host.Show();
            try { return body(field); }
            finally { host.Close(); }
        });

    private static void Press(UIElement element, Key key) =>
        element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(element)!, 0, key)
        { RoutedEvent = UIElement.PreviewKeyDownEvent });

    private static void Type(TextBox box, string text)
    {
        box.Text = text;
        Press(box, Key.Enter);
    }

    private static string N(double value) => value.ToString(CultureInfo.CurrentCulture);

    [Fact]
    public void Number_parsing_accepts_finite_numbers_clamps_and_rounds()
    {
        Assert.True(NumberField.TryParse(N(7.25), 0, 10, 2, out var a));
        Assert.True(NumberField.TryParse(N(50), 0, 10, 0, out var clamped));
        Assert.True(NumberField.TryParse(N(1.236), 0, 10, 2, out var rounded));
        Assert.False(NumberField.TryParse("abc", 0, 10, 0, out _));
        Assert.False(NumberField.TryParse("NaN", 0, 10, 0, out _));
        Assert.False(NumberField.TryParse("Infinity", 0, 10, 0, out _));
        _out.WriteLine($"7.25 -> {a}; 50 -> {clamped}; 1.236 -> {rounded}");
        Assert.Equal((7.25, 10.0, 1.24), (a, clamped, rounded));
    }

    [Fact]
    public void Arrow_steps_are_one_and_ten_and_stay_in_range()
    {
        Check("Stepped(5, up)", 6, NumberField.Stepped(5, up: true, large: false, 1, 0, 10, 0));
        Check("Stepped(5, up, large)", 10, NumberField.Stepped(5, up: true, large: true, 1, 0, 10, 0));
        Check("Stepped(5, down, large)", 0, NumberField.Stepped(5, up: false, large: true, 1, 0, 10, 0));
        Check("Stepped(0.6, up, step 0.05)", 0.65, NumberField.Stepped(0.6, up: true, large: false, 0.05, 0, 1, 2));
    }

    [Fact]
    public void Scrubbing_the_label_moves_one_step_per_two_pixels()
    {
        Check("Scrubbed(10, +6 px)", 13, NumberField.Scrubbed(10, 6, 1, 0, 100, 0));
        Check("Scrubbed(10, -40 px)", 0, NumberField.Scrubbed(10, -40, 1, 0, 100, 0));
        Check("Scrubbed(1, +20 px, step 0.05)", 1.5, NumberField.Scrubbed(1, 20, 0.05, 0.25, 4, 2));
        // Half a step rounds away from zero both ways (banker's rounding would give 12 and 8).
        Check("Scrubbed(10, +5 px)", 13, NumberField.Scrubbed(10, 5, 1, 0, 100, 0));
        Check("Scrubbed(10, -5 px)", 7, NumberField.Scrubbed(10, -5, 1, 0, 100, 0));
        Check("Scrubbed(10, +1 px)", 11, NumberField.Scrubbed(10, 1, 1, 0, 100, 0));
    }

    private static void MouseUp(UIElement element) =>
        element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        { RoutedEvent = UIElement.MouseLeftButtonUpEvent });

    [Fact]
    public void Arrow_steps_on_the_focused_label_end_when_focus_leaves_it()
    {
        var r = Hosted(() => new NumberField("Size", 0, 100), field =>
        {
            int ended = 0;
            field.EditEnded += () => ended++;
            field.ShowValue(10);
            // A focus change with nothing edited is not an edit ending.
            bool focused = field.Label.Focus();
            Keyboard.ClearFocus();
            int endedIdle = ended;

            ended = 0;
            field.Label.Focus();
            field.HandleKey(Key.Up, ModifierKeys.None);
            field.HandleKey(Key.Up, ModifierKeys.None);
            int endedWhileFocused = ended;
            Keyboard.ClearFocus();
            bool movedAway = !field.Label.IsKeyboardFocused;
            int endedAfterLeaving = ended;
            field.Label.Focus();
            Keyboard.ClearFocus();                   // in and out again, nothing changed
            return (focused, movedAway, endedIdle, endedWhileFocused, endedAfterLeaving, EndedTotal: ended, field.Value,
                TabStop: KeyboardNavigation.GetIsTabStop(field.Label), field.Label.Focusable);
        });
        _out.WriteLine("expected focused True, moved away True, idle ends 0, ends while focused 0, after leaving 1, total 1, " +
                       "value 12, tab stop False, focusable True");
        _out.WriteLine($"actual {r}");
        Assert.True(r.focused && r.movedAway, "keyboard focus did not move in the host window: the test proves nothing");
        Assert.Equal(0, r.endedIdle);
        Assert.Equal(0, r.endedWhileFocused);
        Assert.Equal(1, r.endedAfterLeaving);
        Assert.Equal(1, r.EndedTotal);
        Assert.Equal(12, r.Value);
        Assert.False(r.TabStop, "every number label became a Tab stop");
        Assert.True(r.Focusable, "a label that cannot take focus cannot hear Esc during a scrub");
    }

    [Fact]
    public void A_scrub_ends_the_edit_exactly_once_however_it_ends()
    {
        var r = Hosted(() => new NumberField("Size", 0, 100), field =>
        {
            var changes = new List<double>();
            int ended = 0;
            field.ValueChanged += changes.Add;
            field.EditEnded += () => ended++;
            field.ShowValue(10);
            var log = new List<string>();

            // Released over the label: the mouse-up releases capture, and losing it ends the edit.
            bool began = field.BeginScrub(100);
            field.ScrubTo(106);
            MouseUp(field.Label);
            log.Add($"mouse-up: began {began}, value {field.Value}, ended {ended}, captured {field.Label.IsMouseCaptured}");
            var released = (began, field.Value, ended);

            // Capture taken away mid-drag (another window, Alt+Tab): the edit still ends, and the
            // mouse-up that follows does not end it a second time.
            ended = 0;
            began = field.BeginScrub(100);
            field.ScrubTo(110);                          // 13 + 5 steps
            Mouse.Capture(null);
            MouseUp(field.Label);
            field.ScrubTo(140);                          // a stray move after the end changes nothing
            log.Add($"capture lost: began {began}, value {field.Value}, ended {ended}");
            var lost = (began, field.Value, ended);

            // Esc mid-drag: back to the value before the drag, reported, then ended once.
            ended = 0;
            changes.Clear();
            began = field.BeginScrub(100);
            field.ScrubTo(120);
            Press(field.Label, Key.Escape);
            MouseUp(field.Label);
            log.Add($"esc: began {began}, value {field.Value}, ended {ended}, changes [{string.Join(", ", changes)}], " +
                    $"captured {field.Label.IsMouseCaptured}");
            var escaped = (began, field.Value, ended, Changes: changes.ToArray());

            // Esc with no scrub running is not the field's key.
            bool idleEsc = field.HandleKey(Key.Escape, ModifierKeys.None);
            return (log, released, lost, escaped, idleEsc);
        });
        foreach (var line in r.log) _out.WriteLine(line);
        _out.WriteLine("expected mouse-up (True, 13, 1); capture lost (True, 18, 1); esc (True, 18, 1, [28, 18]); idle esc False");
        Assert.Equal((true, 13.0, 1), r.released);
        Assert.Equal((true, 18.0, 1), r.lost);
        Assert.Equal((true, 18.0, 1), (r.escaped.began, r.escaped.Value, r.escaped.ended));
        Assert.Equal(new[] { 28.0, 18.0 }, r.escaped.Changes);
        Assert.False(r.idleEsc);
    }

    private void Check(string what, double expected, double actual)
    {
        _out.WriteLine($"{what}: expected {expected}, actual {actual}");
        Assert.Equal(expected, actual);
    }

    private static bool Marked(TextBox box) =>
        box.ReadLocalValue(System.Windows.Controls.Control.BorderBrushProperty) != DependencyProperty.UnsetValue;

    [Fact]
    public void A_number_field_commits_on_enter_steps_on_arrows_and_reverts_junk()
    {
        var r = Hosted(() => new NumberField("Size", 0, 100, 1), field =>
        {
            var changes = new List<double>();
            int ended = 0;
            field.ValueChanged += changes.Add;
            field.EditEnded += () => ended++;
            field.ShowValue(4);                       // programmatic: no event
            Type(field.Box, N(7.5));
            // The modifiers are passed in, never read from the real keyboard, so a held Shift
            // on the test machine cannot turn the plain step into a large one.
            bool upHandled = field.HandleKey(Key.Up, ModifierKeys.None);
            bool shiftUpHandled = field.HandleKey(Key.Up, ModifierKeys.Shift);
            bool otherHandled = field.HandleKey(Key.A, ModifierKeys.None);
            Type(field.Box, "abc");
            var junk = (field.IsInvalid, field.Box.Text, field.Value, Red: Marked(field.Box));
            field.ShowValue(3);
            return (Changes: changes.ToArray(), ended, junk.IsInvalid, junk.Text, junk.Value, field.ToolTip,
                junk.Red, Cleared: !Marked(field.Box) && !field.IsInvalid, Handled: (upHandled, shiftUpHandled, otherHandled));
        });
        _out.WriteLine($"expected changes 7.5, 8.5, 18.5; ended 2; handled (True, True, False); junk reverted to 18.5 in red, cleared by ShowValue");
        _out.WriteLine($"actual {string.Join(", ", r.Changes)}; ended {r.ended}; handled {r.Handled}; invalid {r.IsInvalid}; red {r.Red}; " +
                       $"cleared {r.Cleared}; text '{r.Text}'; tooltip '{r.ToolTip}'");
        Assert.Equal(new[] { 7.5, 8.5, 18.5 }, r.Changes);
        Assert.Equal((true, true, false), r.Handled);
        Assert.Equal(2, r.ended);
        Assert.True(r.IsInvalid);
        Assert.True(r.Red, "junk did not turn the box red");
        Assert.True(r.Cleared, "showing a good value left the box red");
        Assert.Equal(NumberField.Format(18.5, 1), r.Text);
        Assert.Equal(18.5, r.Value);
        Assert.Equal(Loc.T("designer.field.range", "0", "100"), r.ToolTip);
    }

    [Fact]
    public void A_text_field_commits_and_a_rejected_value_stays_for_fixing()
    {
        var r = Hosted(() => new TextField("Source URL", validate: v => v.StartsWith("https://", StringComparison.Ordinal)), field =>
        {
            var changes = new List<string>();
            int rejected = 0;
            field.ValueChanged += changes.Add;
            field.Rejected += () => rejected++;
            Type(field.Box, "https://example.com");
            Type(field.Box, "ftp://nope");
            return (Changes: changes.ToArray(), rejected, field.IsInvalid, field.Value, field.Box.Text, Red: Marked(field.Box));
        });
        _out.WriteLine($"expected changes [https://example.com], 1 rejection, red, value https://example.com, box ftp://nope");
        _out.WriteLine($"actual   changes [{string.Join(", ", r.Changes)}], {r.rejected} rejection(s), red {r.Red}, " +
                       $"value {r.Value}, box {r.Text}");
        Assert.True(r.Red, "a refused value did not turn the box red");
        Assert.Equal(new[] { "https://example.com" }, r.Changes);
        Assert.Equal(1, r.rejected);
        Assert.True(r.IsInvalid);
        Assert.Equal("https://example.com", r.Value);
        Assert.Equal("ftp://nope", r.Text);
    }

    [Fact]
    public void A_toggle_reports_the_user_but_not_the_program()
    {
        var events = Hosted(() => new ToggleField("Bold"), field =>
        {
            var seen = new List<bool>();
            field.Toggled += seen.Add;
            field.ShowValue(true);
            field.Switch.IsChecked = false;
            return seen.ToArray();
        });
        _out.WriteLine($"expected [False] (the user's toggle only), actual [{string.Join(", ", events)}]");
        Assert.Equal(new[] { false }, events);
    }

    [Fact]
    public void A_choice_field_selects_by_value()
    {
        var r = Hosted(() => new ChoiceField("Align", [("Left", "left"), ("Center", "center"), ("Right", "right")]), field =>
        {
            var seen = new List<string>();
            field.ValueChanged += seen.Add;
            field.ShowValue("center");
            var shown = field.Value;
            field.Combo.SelectedIndex = 2;
            return (shown, Seen: seen.ToArray());
        });
        _out.WriteLine($"expected shown 'center' and events [right]; actual shown '{r.shown}', events [{string.Join(", ", r.Seen)}]");
        Assert.Equal("center", r.shown);
        Assert.Equal(new[] { "right" }, r.Seen);
    }

    [Fact]
    public void A_rect_field_reports_the_whole_rectangle()
    {
        var r = Hosted(() => new RectField("X", "Y", "W", "H", 300, 30), field =>
        {
            var seen = new List<SkinHitRect>();
            field.ValueChanged += seen.Add;
            field.ShowValue(new SkinHitRect(270, 0, 30, 30));
            Type(field.W.Box, "999");
            return seen.ToArray();
        });
        _out.WriteLine($"expected [{new SkinHitRect(270, 0, 300, 30)}], actual [{string.Join<SkinHitRect>(", ", r)}]");
        Assert.Equal(new[] { new SkinHitRect(270, 0, 300, 30) }, r);
    }

    [Fact]
    public void A_colour_field_formats_hex_reverts_junk_and_asks_for_the_picker()
    {
        var r = Hosted(() => new ColorField("Color"), field =>
        {
            var seen = new List<string>();
            ColorField? asked = null;
            field.ValueChanged += seen.Add;
            field.PickRequested += f => asked = f;
            field.ShowValue("#FF336699");
            Type(field.Hex, "#0f0");
            Type(field.Hex, "not a colour");
            var junk = (field.Hex.Text, Red: Marked(field.Hex));
            field.ApplyPicked("#FF00FF00");          // same colour: no event
            field.ApplyPicked("#80FF0000");
            field.Swatch.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            return (Seen: seen.ToArray(), field.Hex.Text, Asked: ReferenceEquals(asked, field), Junk: junk, Cleared: !Marked(field.Hex));
        });
        _out.WriteLine($"expected events [#FF00FF00, #80FF0000], junk reverted to #FF00FF00 in red, cleared by a pick, picker asked");
        _out.WriteLine($"actual   events [{string.Join(", ", r.Seen)}], junk -> '{r.Junk.Text}' red {r.Junk.Red}, cleared {r.Cleared}, " +
                       $"text {r.Text}, asked {r.Asked}");
        Assert.Equal(("#FF00FF00", true), (r.Junk.Text, r.Junk.Red));
        Assert.True(r.Cleared, "a picked colour left the hex box red");
        Assert.Equal(new[] { "#FF00FF00", "#80FF0000" }, r.Seen);
        Assert.Equal("#80FF0000", r.Text);
        Assert.True(r.Asked);
    }

    [Fact]
    public void A_font_field_ignores_a_change_of_case_only()
    {
        var r = Hosted(() => new FontField("Font"), field =>
        {
            var seen = new List<string>();
            field.ValueChanged += seen.Add;
            field.ShowValue("Arial");
            field.Combo.Text = "aRIAL";
            Press(field.Combo, Key.Enter);
            var afterCase = (Seen: seen.ToArray(), field.Value);
            field.Combo.Text = "Consolas";
            Press(field.Combo, Key.Enter);
            return (afterCase, Seen: seen.ToArray(), field.Value);
        });
        _out.WriteLine($"expected no event for 'aRIAL' (value stays 'Arial'), then [Consolas]; " +
                       $"actual [{string.Join(", ", r.afterCase.Seen)}] value '{r.afterCase.Value}', then [{string.Join(", ", r.Seen)}] value '{r.Value}'");
        Assert.Empty(r.afterCase.Seen);
        Assert.Equal("Arial", r.afterCase.Value);
        Assert.Equal(new[] { "Consolas" }, r.Seen);
        Assert.Equal("Consolas", r.Value);
    }

    [Fact]
    public void A_font_field_keeps_an_uninstalled_font_verbatim()
    {
        var r = Hosted(() => new FontField("Font"), field =>
        {
            var seen = new List<string>();
            field.ValueChanged += seen.Add;
            field.ShowValue("Not An Installed Font 123");
            var shown = (field.Combo.Text, field.Value);
            field.Combo.Text = "  Arial ";
            Press(field.Combo, Key.Enter);
            return (shown, Seen: seen.ToArray(), Families: FontField.Families.Count);
        });
        _out.WriteLine($"{r.shown}; {string.Join(", ", r.Seen)}; {r.Families} families");
        Assert.Equal(("Not An Installed Font 123", "Not An Installed Font 123"), r.shown);
        Assert.Equal(new[] { "Arial" }, r.Seen);
        Assert.True(r.Families > 0);
    }

    [Fact]
    public void An_art_field_shows_a_small_thumbnail_and_its_frame_size_and_survives_bad_art()
    {
        var good = Path.Combine(_dir, "good.png");
        RealPngs.WriteSolid(good, 300, 400, System.Drawing.Color.Teal);
        var bad = Path.Combine(_dir, "bad.png");
        TestPngs.WriteHeaderOnly(bad, 300, 40);

        var r = Hosted(() => new ArtField(optional: false), field =>
        {
            field.ShowArt(good, "good.png", (300, 400));
            var goodThumb = (field.Thumbnail.Source as BitmapSource)?.PixelHeight;
            var size = field.FrameSize.Text;
            field.ShowArt(bad, "bad.png", null);
            var badThumb = field.Thumbnail.Source;
            field.ShowArt(null, "—", null);
            return (goodThumb, size, BadThumb: badThumb is null, RemoveVisible: field.Remove.Visibility, field.FileName.Text);
        });
        _out.WriteLine($"{r}");
        Assert.Equal(ArtField.ThumbnailHeight, r.goodThumb);
        Assert.Equal(Loc.T("designer.art.frame-size", 300, 400), r.size);
        Assert.True(r.BadThumb);
        Assert.Equal(Visibility.Collapsed, r.RemoveVisible);
        Assert.Equal("—", r.Text);
    }

    [Fact]
    public void An_art_fields_buttons_raise_their_requests()
    {
        var r = Hosted(() => new ArtField(optional: true), field =>
        {
            var seen = new List<string>();
            field.BrowseRequested += () => seen.Add("browse");
            field.SheetRequested += () => seen.Add("sheet");
            field.RemoveRequested += () => seen.Add("remove");
            field.ShowArt(Path.Combine(_dir, "anything.png"), "anything.png", null);
            foreach (var button in new[] { field.Browse, field.Sheet, field.Remove })
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            return (Seen: seen.ToArray(), field.Remove.Visibility, field.Remove.IsEnabled);
        });
        _out.WriteLine($"expected [browse, sheet, remove] and Remove visible+enabled; actual [{string.Join(", ", r.Seen)}], " +
                       $"{r.Visibility}, enabled {r.IsEnabled}");
        Assert.Equal(new[] { "browse", "sheet", "remove" }, r.Seen);
        Assert.Equal((Visibility.Visible, true), (r.Visibility, r.IsEnabled));
    }
}
