using System.Windows;

namespace AorinEQ.UI;

/// <summary>`help:Help.Topic="settings.air-play-dither"` on any control.
///
/// One property, three renderings - see <see cref="HelpDecorator"/>. Attaching it is the only
/// thing a window author does; which affordance appears is decided by what it is attached to, so
/// nobody has to remember that a card gets an expander and a toolbar button gets a tooltip.</summary>
public static class Help
{
    public static readonly DependencyProperty TopicProperty =
        DependencyProperty.RegisterAttached(
            "Topic", typeof(string), typeof(Help), new PropertyMetadata(null));

    public static void SetTopic(DependencyObject element, string value) =>
        element.SetValue(TopicProperty, value);

    public static string? GetTopic(DependencyObject element) =>
        (string?)element.GetValue(TopicProperty);
}
