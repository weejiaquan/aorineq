using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using AorinEQ.Core;
using Wpf.Ui.Controls;
using Orientation = System.Windows.Controls.Orientation;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = Wpf.Ui.Controls.TextBlock;
using ToolTip = System.Windows.Controls.ToolTip;
using ToolTipService = System.Windows.Controls.ToolTipService;
using ToggleButton = System.Windows.Controls.Primitives.ToggleButton;
using Application = System.Windows.Application;

namespace AorinEQ.UI;

/// <summary>Turns <see cref="Help.Topic"/> into something visible, once, when a window loads.
///
/// Three renderings, because the windows are genuinely different shapes and one affordance would
/// wreck two of them. A settings card has room to grow, so its help opens in place. A toolbar
/// button in the EQ editor does not, so its help is a tooltip. A WinForms menu item has neither,
/// so its help is ToolTipText set from code.
///
/// The pass runs on Loaded rather than in a constructor: a CardControl's header is not reachable
/// until the visual tree exists.</summary>
public static class HelpDecorator
{
    /// <summary>Decorates every helped control under <paramref name="root"/>, and returns the map
    /// from topic key to the element carrying it - which is what search scrolls to.</summary>
    public static IReadOnlyDictionary<string, FrameworkElement> Decorate(DependencyObject root)
    {
        var map = new Dictionary<string, FrameworkElement>(StringComparer.Ordinal);

        foreach (var element in Descendants(root).OfType<FrameworkElement>())
        {
            var key = Help.GetTopic(element);
            if (key is null) continue;

            var topic = HelpCatalogue.Find(key)
                ?? throw new InvalidOperationException(
                    $"help:Help.Topic=\"{key}\" on a {element.GetType().Name} names a topic that " +
                    "is not in HelpCatalogue. HelpXamlCoverageTests should have caught this before " +
                    "the app ran.");

            if (element is CardControl card) DecorateCard(card, topic);
            else DecorateWithToolTip(element, topic);

            map[key] = element;
        }

        return map;
    }

    /// <summary>Expands a card's help, if it has any. Used by search after it scrolls to a row.</summary>
    public static void Expand(FrameworkElement element)
    {
        if (element is CardControl { Header: StackPanel header }
            && header.Children.OfType<StackPanel>().FirstOrDefault(p => p.Tag as string == HelpPanelTag)
                is { } panel)
        {
            panel.Visibility = Visibility.Visible;
            foreach (var toggle in Descendants(element).OfType<ToggleButton>())
                if (toggle.Tag as string == HelpToggleTag)
                    toggle.IsChecked = true;
        }
    }

    private const string HelpPanelTag = "aorineq-help-body";
    private const string HelpToggleTag = "aorineq-help-toggle";

    /// <summary>A settings card: a (?) beside the title, and the paragraph appended to the SAME
    /// header panel so the card grows downward when it opens.
    ///
    /// Appending to the header rather than inserting a sibling into the page is what keeps this
    /// from touching the page's layout at all - and the card's content slot, where every radio,
    /// combo and toggle lives, is never gone near.</summary>
    private static void DecorateCard(CardControl card, HelpTopic topic)
    {
        if (card.Header is not StackPanel header || header.Children.Count == 0
            || header.Children[0] is not TextBlock title)
        {
            throw new InvalidOperationException(
                $"The card for topic '{topic.Key}' does not have the header shape this pass " +
                "expects (a StackPanel whose first child is a ui:TextBlock). Either fix the card, " +
                "or give the control a tooltip topic instead of a card topic.");
        }

        header.Children.RemoveAt(0);

        var toggle = new ToggleButton
        {
            Content = "?",
            Tag = HelpToggleTag,
            Width = 18,
            Height = 18,
            Padding = new Thickness(0),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 10,
            // The secondary TEXT brush, never Appearance="Primary": that paints from the Windows
            // accent, and this machine's accent is black - the same trap ModeRadio and AnchorCell
            // in the XAML already carry a comment about.
            Foreground = Brush("TextFillColorSecondaryBrush"),
        };
        AutomationProperties.SetName(toggle, Loc.T("help.affordance.name", topic.Title));

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(title);
        titleRow.Children.Add(toggle);
        header.Children.Insert(0, titleRow);

        var panel = new StackPanel
        {
            Tag = HelpPanelTag,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 8, 0, 2),
        };

        panel.Children.Add(new TextBlock
        {
            Text = topic.Body,
            TextWrapping = TextWrapping.Wrap,
            // Stated explicitly, like every other text style in this window. A ui:TextBlock that is
            // not inside a themed container falls back to BLACK and measures 1.41:1 on this
            // window's Mica - a defect this project shipped once and had to pixel-sample to find.
            Foreground = Brush("TextFillColorSecondaryBrush"),
        });

        if (topic.DocsAnchor is { } anchor)
        {
            panel.Children.Add(new HyperlinkButton
            {
                Content = Loc.T("help.learn-more"),
                NavigateUri = DocsUrl + anchor,
                Margin = new Thickness(0, 4, 0, 0),
                Padding = new Thickness(0),
                Foreground = Brush("TextFillColorPrimaryBrush"),
            });
        }

        header.Children.Add(panel);

        toggle.Checked += (_, _) => panel.Visibility = Visibility.Visible;
        toggle.Unchecked += (_, _) => panel.Visibility = Visibility.Collapsed;
    }

    /// <summary>Anything that is not a card: the title in bold over the body, as a tooltip.</summary>
    private static void DecorateWithToolTip(FrameworkElement element, HelpTopic topic)
    {
        var stack = new StackPanel { MaxWidth = 320 };

        stack.Children.Add(new TextBlock
        {
            Text = topic.Title,
            FontTypography = FontTypography.BodyStrong,
            TextWrapping = TextWrapping.Wrap,
        });
        stack.Children.Add(new TextBlock
        {
            Text = topic.Body,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        });

        element.ToolTip = new ToolTip { Content = stack };
        ToolTipService.SetInitialShowDelay(element, 400);
        ToolTipService.SetShowDuration(element, 30000);

        // A tooltip is invisible to someone driving the app from the keyboard, so the same words go
        // where a screen reader will actually find them.
        AutomationProperties.SetHelpText(element, $"{topic.Title}. {topic.Body}");
    }

    /// <summary>The WinForms menus have no attached properties, so their help is applied in code -
    /// same catalogue, same words, same translation.</summary>
    public static void Apply(System.Windows.Forms.ToolStripMenuItem item, string key)
    {
        var topic = HelpCatalogue.Find(key)
            ?? throw new InvalidOperationException($"No help topic '{key}' for menu item '{item.Text}'.");

        item.ToolTipText = topic.Body;
    }

    private const string DocsUrl = "https://github.com/weejiaquan/aorineq/blob/master/docs/reference.md";

    private static System.Windows.Media.Brush Brush(string key) =>
        (System.Windows.Media.Brush)Application.Current.Resources[key];

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
