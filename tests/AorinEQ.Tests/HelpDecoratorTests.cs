using System.Windows;
using System.Windows.Controls.Primitives;
using AorinEQ.UI;
using Wpf.Ui.Controls;
using Popup = System.Windows.Controls.Primitives.Popup;
using ScrollViewer = System.Windows.Controls.ScrollViewer;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = Wpf.Ui.Controls.TextBlock;
using Window = System.Windows.Window;

namespace AorinEQ.Tests;

/// <summary>What the (?) on a settings card is allowed to do to the page around it.
///
/// These are the only tests in this suite that build WPF controls, and they exist because the
/// defects they pin are invisible to every other kind of test. The catalogue tests prove the WORDS
/// are right; HelpXamlCoverageTests proves every card HAS help. Neither can see that opening that
/// help shoved the rest of the page downward, which is what it did.</summary>
[Collection("Wpf")]
public class HelpDecoratorTests
{
    private readonly StaWpf _sta;
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public HelpDecoratorTests(StaWpf sta, Xunit.Abstractions.ITestOutputHelper output)
    {
        _sta = sta;
        _out = output;
    }

    private sealed record Card(FrameworkElement Root, CardControl Control, ToggleButton Toggle,
        ScrollViewer? Scroller, Popup Popup, Window Host);

    /// <summary>Builds one decorated card in a hosted window, runs <paramref name="body"/> against
    /// it on the STA thread, and closes the window afterwards whatever happens.</summary>
    private T With<T>(Func<Card, T> body, bool inScrollViewer = false) =>
        _sta.Run(() =>
        {
            var card = BuildDecoratedCard(inScrollViewer);
            try { return body(card); }
            finally { card.Host.Close(); }
        });

    /// <summary>A real settings card of the shape the XAML builds, decorated and laid out.</summary>
    private static Card BuildDecoratedCard(bool inScrollViewer = false, string topic = "settings.step")
    {
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "Volume step" });
        header.Children.Add(new TextBlock { Text = "How far each keypress moves the volume." });

        var card = new CardControl
        {
            Header = header,
            Content = new Wpf.Ui.Controls.Button { Content = "5" },
        };
        Help.SetTopic(card, topic);

        var cards = new StackPanel { Width = 600 };
        cards.Children.Add(card);

        // Tall filler, so the ScrollViewer has somewhere to scroll TO. Without it
        // ScrollToVerticalOffset is a no-op and the scroll test would pass on nothing happening.
        if (inScrollViewer)
            cards.Children.Add(new System.Windows.Controls.Border { Height = 2000 });

        ScrollViewer? scroller = null;
        FrameworkElement root = cards;
        if (inScrollViewer)
        {
            scroller = new ScrollViewer { Content = cards, Height = 300, Width = 600 };
            root = scroller;
        }

        // Hosted in a REAL window, positioned off-screen. A Popup cannot open unless its placement
        // target is connected to a PresentationSource: measure and arrange alone leave the tree in
        // memory with no HWND, and IsOpen quietly stays false however the toggle is driven. That
        // is not a detail - a test that skips the window passes on help that never opens, because
        // a popup that stays shut moves nothing either, and that is exactly the false pass this
        // suite gave until the window was added. Off-screen and unactivated keeps it out of the
        // way of whoever is running the suite.
        var host = new Window
        {
            Content = root,
            Width = 640,
            Height = 480,
            Left = -32000,
            Top = -32000,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            ShowActivated = false,
        };
        host.Show();
        host.UpdateLayout();
        Pump();

        Layout(root);
        HelpDecorator.Decorate(root);
        Layout(root);
        Pump();

        var toggle = Descendants(card).OfType<ToggleButton>().FirstOrDefault()
            ?? throw new InvalidOperationException(
                "The decoration pass put no ToggleButton on the card, so there is no help "
                + "affordance to test.");

        var popup = ((StackPanel)card.Header).Children.OfType<Popup>().FirstOrDefault()
            ?? throw new InvalidOperationException(
                "The decoration pass built no Popup, so the help body has nowhere to go that is "
                + "not in the layout.");

        return new Card(root, card, toggle, scroller, popup, host);
    }

    /// <summary>Runs the dispatcher queue to empty.
    ///
    /// The whole of a test body runs inside a single HookThread.Invoke, which occupies the message
    /// loop for its duration - so without this nothing queued ever runs: Loaded never fires, the
    /// controls stay unloaded, and WPF refuses to open a Popup whose placement target is not
    /// loaded. IsOpen then reads back false however it is set, including when assigned directly,
    /// which looks exactly like a broken feature and is not.</summary>
    private static void Pump()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(600, double.PositiveInfinity));
        element.Arrange(new Rect(element.DesiredSize));
        element.UpdateLayout();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    /// <summary>THE defect this change exists to fix.
    ///
    /// The help body used to be appended to the card's own header panel, so revealing it grew the
    /// card and pushed every card below it down the page - while the pointer was still resting on
    /// the row the reader was aiming at. The search dropdown in the same window already avoids
    /// this by floating its results in a Popup; help had to do the same.
    ///
    /// Measured on the ROOT rather than the card, because that is what the page is: if the root
    /// grows, everything after this card has moved.</summary>
    [Fact]
    public void Opening_a_cards_help_does_not_change_the_height_of_the_page()
    {
        var (before, after, opened) = With(card =>
        {
            var closed = card.Root.DesiredSize.Height;

            card.Toggle.IsChecked = true;
            Pump();
            Layout(card.Root);

            return (closed, card.Root.DesiredSize.Height, card.Popup.IsOpen);
        });

        _out.WriteLine($"page height with help closed: {before}");
        _out.WriteLine($"page height with help open:   {after}");
        _out.WriteLine($"difference:                   {after - before}");
        _out.WriteLine($"popup actually open:          {opened}");

        // Asserted together, and this one first. Without it the height check passes just as
        // happily on help that never opens - a test that measures nothing and reports success.
        Assert.True(opened, "Checking the toggle did not open the popup, so the height proves nothing.");
        Assert.Equal(before, after);
    }

    /// <summary>Scrolling the page closes the help.
    ///
    /// A Popup is positioned in SCREEN space and does not follow its placement target, so a page
    /// scrolled while help is open would leave the paragraph hanging over whatever slid under it,
    /// still pointing at a card that has moved. The inline panel this replaced could not come
    /// adrift like that, so the popup has to be told to let go.</summary>
    [Fact]
    public void Scrolling_the_page_closes_an_open_help_popup()
    {
        var (openedBefore, openAfter) = With(card =>
        {
            card.Toggle.IsChecked = true;
            Pump();
            var opened = card.Popup.IsOpen;

            card.Scroller!.ScrollToVerticalOffset(60);
            card.Scroller.UpdateLayout();
            Pump();

            return (opened, card.Popup.IsOpen);
        }, inScrollViewer: true);

        _out.WriteLine($"open before scrolling: {openedBefore}");
        _out.WriteLine($"open after scrolling:  {openAfter}");

        Assert.True(openedBefore, "The help did not open, so this proves nothing about scrolling.");
        Assert.False(openAfter);
    }

    /// <summary>Dismissing the popup puts the button back with it, so the (?) is never left lit
    /// over nothing. StaysOpen=False closes on an outside click without touching the button.</summary>
    [Fact]
    public void Closing_the_popup_unchecks_the_button()
    {
        var (openedFirst, stillChecked) = With(card =>
        {
            card.Toggle.IsChecked = true;
            Pump();
            var opened = card.Popup.IsOpen;

            card.Popup.IsOpen = false;
            Pump();
            return (opened, card.Toggle.IsChecked == true);
        });

        _out.WriteLine($"opened first:        {openedFirst}");
        _out.WriteLine($"still checked after: {stillChecked}");

        Assert.True(openedFirst, "The help did not open, so closing it proves nothing.");
        Assert.False(stillChecked);
    }

    /// <summary>At rest the affordance is invisible, so a page of 35 cards is not a page of 35
    /// question marks. It becomes visible when the pointer is over the card it belongs to.</summary>
    [Fact]
    public void The_help_button_is_invisible_until_its_card_is_pointed_at()
    {
        var opacity = With(card => card.Toggle.Opacity);

        _out.WriteLine($"resting opacity: {opacity}");
        Assert.Equal(0d, opacity);
    }

    /// <summary>Invisible, but still occupying its width.
    ///
    /// The obvious way to hide the button is Visibility.Collapsed, and it is wrong: the title
    /// would shift sideways the instant the pointer arrived, which is the same defect as the one
    /// above wearing a different hat. Reserving the slot costs 18px of empty space and buys a
    /// title that never moves.</summary>
    [Fact]
    public void The_hidden_help_button_still_reserves_its_place_so_the_title_cannot_shift()
    {
        var width = With(card => card.Toggle.DesiredSize.Width);

        _out.WriteLine($"reserved width while invisible: {width}");
        Assert.True(width > 0,
            "The help button takes up no width while hidden, so revealing it will move the title.");
    }
}
