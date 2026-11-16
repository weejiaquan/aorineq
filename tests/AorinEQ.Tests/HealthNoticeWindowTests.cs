using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AorinEQ.UI;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;

namespace AorinEQ.Tests;

/// <summary>The Equalizer APO health notice, on the real <see cref="HealthNoticeWindow"/>: it says
/// what it was given, stays until the user answers it, and sits in the corner of the screen in
/// use without taking the focus. WHEN something is said is EapoHealthTests' business.
///
/// The window really is shown, for as long as each test takes.</summary>
[Collection("Wpf")]
public class HealthNoticeWindowTests
{
    private readonly StaWpf _sta;
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public HealthNoticeWindowTests(StaWpf sta, Xunit.Abstractions.ITestOutputHelper output)
    {
        _sta = sta;
        _out = output;
    }

    private T With<T>(string text, Action? action, Func<HealthNoticeWindow, T> body) =>
        _sta.Run(() =>
        {
            // Resolved from App.xaml in the app; the fixture's Application is not App.
            Application.Current.Resources["AppFontFamily"] = new System.Windows.Media.FontFamily("Segoe UI");
            var notice = new HealthNoticeWindow(text, action);
            try
            {
                notice.Present();
                return body(notice);
            }
            finally { notice.Close(); }
        });

    private static Button Part(HealthNoticeWindow notice, string name) => (Button)notice.FindName(name);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    [Fact]
    public void A_warning_says_its_text_and_offers_the_way_to_fix_it()
    {
        const string text = "Equalizer APO isn't switched on for your current playback device.";
        var (shown, said, action, dismiss, activated, topmost) = With(text, () => { }, n =>
            (n.IsVisible, ((TextBlock)n.FindName("MessageText")).Text, Part(n, "ActionButton").Visibility,
                Part(n, "DismissButton").Visibility, n.ShowActivated, n.Topmost));

        _out.WriteLine($"shown={shown} text='{said}' action={action} dismiss={dismiss} activated={activated} topmost={topmost}");
        Assert.True(shown);
        Assert.Equal(text, said);
        Assert.Equal(Visibility.Visible, action);
        Assert.Equal(Visibility.Visible, dismiss);
        // It appears at startup and mid-typing: it must be seen, and must not take the keyboard.
        Assert.False(activated);
        Assert.True(topmost);
    }

    [Fact]
    public void A_message_with_nothing_to_do_about_it_offers_only_dismiss()
    {
        var action = With("Equalizer APO is working again on your playback device.", null,
            n => Part(n, "ActionButton").Visibility);
        Assert.Equal(Visibility.Collapsed, action);
    }

    [Fact]
    public void It_sits_in_the_bottom_right_corner_of_the_screen_in_use()
    {
        var (window, work, scale) = With("Equalizer APO stopped reading AorinEQ's settings file.", () => { }, n =>
        {
            var target = OsdScreen.Active();
            double current = System.Windows.Media.VisualTreeHelper.GetDpi(n).DpiScaleX;
            // The window's own DIPs, expressed in the target screen's.
            double k = current / target.Scale;
            return (new Rect(n.Left * k, n.Top * k, n.ActualWidth, n.ActualHeight), target.WorkArea, target.Scale);
        });

        _out.WriteLine($"notice {window} in work area {work} at scale {scale}");
        Assert.True(window.Height > 0, "The notice has no height: it was never laid out.");
        // The window style's minimum size is a dialog's (460 x 320); a one-line notice that tall
        // is a slab in the corner of the screen.
        Assert.Equal(380, window.Width);
        Assert.True(window.Height < 200, $"The notice is {window.Height} tall for one line of text.");
        Assert.Equal(work.Right - HealthNoticeWindow.ScreenMargin, window.Right, 1.0);
        Assert.Equal(work.Bottom - HealthNoticeWindow.ScreenMargin, window.Bottom, 1.0);
    }

    [Fact]
    public void Dismiss_closes_it_without_running_the_action()
    {
        int ran = 0;
        var (before, after) = With("warning", () => ran++, n =>
        {
            bool before = n.IsVisible;
            Click(Part(n, "DismissButton"));
            return (before, n.IsVisible);
        });

        _out.WriteLine($"visible before={before} after={after}, action ran {ran} times");
        Assert.True(before);
        Assert.False(after);
        Assert.Equal(0, ran);
    }

    [Fact]
    public void The_action_runs_once_and_takes_the_notice_away()
    {
        int ran = 0;
        var after = With("warning", () => ran++, n =>
        {
            Click(Part(n, "ActionButton"));
            return n.IsVisible;
        });

        _out.WriteLine($"visible after={after}, action ran {ran} times");
        Assert.False(after);
        Assert.Equal(1, ran);
    }
}
