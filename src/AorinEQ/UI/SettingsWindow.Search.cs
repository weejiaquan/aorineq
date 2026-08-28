using System.Windows;
using System.Windows.Controls;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using Key = System.Windows.Input.Key;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AorinEQ.Core;

namespace AorinEQ.UI;

/// <summary>Searching the help.
///
/// It searches TOPICS, not section names. That is the whole point: someone who has just bought a
/// HomePod types "homepod", and no visible label anywhere in this app contains that word - it is in
/// the body of the AirPlay receiver topic. A search over the sidebar would find nothing and teach
/// the user the feature does not exist.
///
/// Built from a TextBox and a Popup rather than the library's AutoSuggestBox: that control exists
/// in WPF-UI 4.3 and compiles, but has no template under this theme, so it renders NOTHING. The
/// failure is silent - the box is simply absent, and only a UI Automation dump showed it.</summary>
public partial class SettingsWindow
{
    /// <summary>One row in the results list. ToString is what the ListBox renders, so the surface
    /// prefix is part of it: "AirPlay › Receiver" says where you are about to be taken, which
    /// matters now that results span pages.</summary>
    private sealed record HelpSearchResult(HelpTopic Topic, string Label)
    {
        public override string ToString() => Label;
    }

    private void OnHelpSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        var results = HelpCatalogue.Search(HelpSearchBox.Text)
            .Select(topic => new HelpSearchResult(
                topic,
                Loc.T("settings.search.result", HelpSurfaces.DisplayName(topic.Surface), topic.Title)))
            .ToList();

        HelpSearchResults.ItemsSource = results;
        HelpSearchPopup.IsOpen = results.Count > 0;
    }

    /// <summary>Down-arrow moves into the list, Enter takes the best match, Escape gives up.
    ///
    /// Without these the box is mouse-only: you can see the answer and cannot reach it from the
    /// keyboard, which is the state most home-grown search boxes ship in.</summary>
    private void OnHelpSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                CloseSearch();
                e.Handled = true;
                break;

            case Key.Down when HelpSearchResults.Items.Count > 0:
                HelpSearchResults.SelectedIndex = 0;
                ((ListBoxItem?)HelpSearchResults.ItemContainerGenerator
                    .ContainerFromIndex(0))?.Focus();
                e.Handled = true;
                break;

            case Key.Enter when HelpSearchResults.Items.Count > 0:
                GoTo(HelpSearchResults.Items[0] as HelpSearchResult);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Enter commits, Escape backs out.
    ///
    /// Escape has to be handled HERE as well as on the box: once Down moves focus into the list,
    /// the box's handler stops seeing keys, and without this there is no way back out of the
    /// results except the mouse - on a control the user reached with the keyboard.</summary>
    private void OnHelpResultsKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                GoTo(HelpSearchResults.SelectedItem as HelpSearchResult);
                e.Handled = true;
                break;

            case Key.Escape:
                CloseSearch();
                // Focus goes back where it came from, not nowhere: the popup is closing and the
                // element that had focus is inside it.
                HelpSearchBox.Focus();
                e.Handled = true;
                break;
        }
    }

    /// <summary>A click on a result commits it.
    ///
    /// Deliberately NOT SelectionChanged. Moving the highlight with the arrow keys also changes the
    /// selection, so committing there meant pressing Down navigated away instantly and the list
    /// could never be browsed from the keyboard - the very thing the Down handler exists to allow.</summary>
    private void OnHelpResultClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;

        var item = ItemsControl.ContainerFromElement(HelpSearchResults, source) as ListBoxItem;
        if (item?.DataContext is HelpSearchResult result) GoTo(result);
    }

    private void GoTo(HelpSearchResult? result)
    {
        if (result is null) return;

        var topic = result.Topic;
        CloseSearch();

        if (SettingsSections.IsSection(topic.Surface))
        {
            Navigate(topic.Surface);

            // Layout has to happen before a card has a position to scroll to - and this section may
            // be being decorated for the first time by this very navigation, at Loaded priority, so
            // this queues behind that at Background.
            Dispatcher.BeginInvoke(() => RevealHelp(topic.Key), DispatcherPriority.Background);
        }
        else
        {
            // A topic that lives in another window. Opening that window's help panel is honest;
            // hunting down an individual control inside a window that may not even be open is the
            // kind of thing that works on the machine it was written on and nowhere else.
            HelpPanel.Show(this, topic.Surface, topic.Key);
        }
    }

    private void CloseSearch()
    {
        HelpSearchPopup.IsOpen = false;
        HelpSearchResults.ItemsSource = null;
        HelpSearchBox.Text = "";
    }

    /// <summary>Scrolls a helped card into view, opens its help, and pulses it once.</summary>
    private void RevealHelp(string key)
    {
        if (!_helpTargets.TryGetValue(key, out var element)) return;

        element.BringIntoView();

        // Opened AFTER the scroll, not with it. BringIntoView is honoured during the next layout
        // pass, and the help is now a Popup placed in screen coordinates: opening it first would
        // place the paragraph against the row's OLD position, and then the scroll arriving behind
        // it would trip CloseOnScroll and shut the help the user just asked for. Loaded priority
        // is the same queue the window already uses to wait for a layout pass in DecorateHelpFor
        // and FocusPrimaryControl.
        Dispatcher.BeginInvoke(() =>
        {
            HelpDecorator.Expand(element);
            Pulse(element);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>One soft pulse of the card.
    ///
    /// Without it the user is dropped onto a page of near-identical rows with no idea which one
    /// they asked for; a scroll position is not an answer. Once, not a loop - this is a pointer,
    /// not an alarm.</summary>
    private static void Pulse(FrameworkElement element)
    {
        // Opacity, not the background: the theme brushes are frozen, and animating a frozen brush
        // throws. The animation is released on completion so the element goes back to being driven
        // by its own style rather than by a leftover animation clock holding it at 1.0.
        var pulse = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(800),
            KeyFrames =
            {
                new LinearDoubleKeyFrame(0.40, KeyTime.FromPercent(0.15)),
                new LinearDoubleKeyFrame(1.00, KeyTime.FromPercent(0.45)),
                new LinearDoubleKeyFrame(0.55, KeyTime.FromPercent(0.70)),
                new LinearDoubleKeyFrame(1.00, KeyTime.FromPercent(1.00)),
            },
        };

        pulse.Completed += (_, _) =>
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1.0;
        };

        element.BeginAnimation(UIElement.OpacityProperty, pulse);
    }
}
