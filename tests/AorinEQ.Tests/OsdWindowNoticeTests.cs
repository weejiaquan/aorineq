using System.Windows;
using AorinEQ.Core;
using AorinEQ.UI;
using Application = System.Windows.Application;

namespace AorinEQ.Tests;

/// <summary>The device notice is drawn by the real <see cref="OsdWindow"/>, so these build one and
/// measure it. What a name is cut to is DeviceNoticeTests' business; what is pinned here is the
/// part only a laid-out window can show - that the notice sits where the volume bar sits, that a
/// wide name stays on its plate, and that the bar comes back afterwards.
///
/// The window really is shown, for as long as each test takes, wherever the OSD would appear.</summary>
[Collection("Wpf")]
public class OsdWindowNoticeTests
{
    private readonly StaWpf _sta;
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public OsdWindowNoticeTests(StaWpf sta, Xunit.Abstractions.ITestOutputHelper output)
    {
        _sta = sta;
        _out = output;
    }

    /// <summary>Runs <paramref name="body"/> against a configured OSD on the STA thread. The
    /// window is hidden rather than closed afterwards: OsdWindow cancels Close by design.</summary>
    private T With<T>(Func<OsdWindow, T> body) =>
        _sta.Run(() =>
        {
            // OsdWindow's XAML resolves this from App.xaml; the fixture's Application is not App.
            Application.Current.Resources["AppFontFamily"] = new System.Windows.Media.FontFamily("Segoe UI");
            var osd = new OsdWindow();
            osd.ApplyConfig(Settings.Default);
            try { return body(osd); }
            finally { osd.Hide(); }
        });

    private static FrameworkElement Part(OsdWindow osd, string name) =>
        (FrameworkElement)osd.FindName(name);

    private static void ShowVolume(OsdWindow osd)
    {
        osd.SetAirPlay(AirPlayBarState.Hidden, skin: null, scale: 1.0);
        osd.ShowVolume(50, muted: false, interactive: false);
        osd.UpdateLayout();
    }

    [Fact]
    public void The_notice_appears_where_the_volume_bar_does()
    {
        var (bar, notice, visible) = With(osd =>
        {
            ShowVolume(osd);
            var barRect = new Rect(osd.Left, osd.Top, osd.Width, osd.Height);

            osd.ShowNotice("Speakers");
            osd.UpdateLayout();
            return (barRect, new Rect(osd.Left, osd.Top, osd.Width, osd.Height), osd.IsVisible);
        });

        _out.WriteLine($"volume bar {bar}, notice {notice}");
        Assert.True(visible, "ShowNotice did not show the window.");
        Assert.Equal(bar, notice);
    }

    [Fact]
    public void The_notice_replaces_the_volume_bar_rather_than_joining_it()
    {
        var (styles, strip, plate, text) = With(osd =>
        {
            ShowVolume(osd);
            osd.ShowNotice("Headphones (USB DAC)");
            osd.UpdateLayout();
            return (Part(osd, "StyleHost").Visibility, Part(osd, "AirPlayBar").Visibility,
                Part(osd, "NoticeRoot").Visibility, ((System.Windows.Controls.TextBlock)Part(osd, "NoticeText")).Text);
        });

        Assert.Equal(Visibility.Collapsed, styles);
        Assert.Equal(Visibility.Collapsed, strip);
        Assert.Equal(Visibility.Visible, plate);
        Assert.Equal("Headphones (USB DAC)", text);
    }

    [Fact]
    public void The_next_volume_change_puts_the_bar_back()
    {
        var (styles, plate) = With(osd =>
        {
            osd.ShowNotice("Speakers");
            ShowVolume(osd);
            return (Part(osd, "StyleHost").Visibility, Part(osd, "NoticeRoot").Visibility);
        });

        Assert.Equal(Visibility.Visible, styles);
        Assert.Equal(Visibility.Collapsed, plate);
    }

    [Fact]
    public void A_name_too_wide_for_the_plate_stays_inside_it()
    {
        // As long as DeviceNotice lets a name be, in full-width characters: few enough to be left
        // whole, and still half as wide again as the plate is allowed to get.
        var name = new string('音', DeviceNotice.MaxNameLength);

        var (shortWidth, wideWidth, textRight, plateWidth, workArea, window) = With(osd =>
        {
            osd.ShowNotice("Speakers");
            osd.UpdateLayout();
            double narrow = osd.Width;

            osd.ShowNotice(name);
            osd.UpdateLayout();
            var plate = Part(osd, "NoticeRoot");
            var text = Part(osd, "NoticeText");
            double right = text.TranslatePoint(new System.Windows.Point(text.ActualWidth, 0), plate).X;
            return (narrow, osd.Width, right, plate.ActualWidth, SystemParameters.WorkArea,
                new Rect(osd.Left, osd.Top, osd.Width, osd.Height));
        });

        _out.WriteLine($"short name: {shortWidth} wide; {name.Length} full-width characters: {wideWidth} wide, " +
                       $"text ends at {textRight:0.#} of a {plateWidth:0.#} plate; window {window} in {workArea}");
        Assert.Equal(300, shortWidth); // a short name gets the volume bar's own width, not a sliver
        Assert.True(wideWidth > shortWidth, "A long name did not widen the plate at all.");
        Assert.True(wideWidth <= 480, $"The plate grew to {wideWidth}, past its 480 cap.");
        Assert.True(textRight <= plateWidth - 16 + 0.5,
            $"The name runs to {textRight:0.#}, past the plate's padding at {plateWidth - 16:0.#}.");
        Assert.True(workArea.Contains(window), $"The notice {window} is not inside the work area {workArea}.");
    }
}

/// <summary>One STA thread and one <see cref="Application"/> for every test class that builds WPF
/// controls. The Application can only be constructed once per process and belongs to the thread
/// that made it, so a second class with a StaWpf of its own would be handed controls styled from
/// another thread's resources.</summary>
[CollectionDefinition("Wpf")]
public class WpfCollection : ICollectionFixture<StaWpf>;
