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
using Popup = System.Windows.Controls.Primitives.Popup;
using PlacementMode = System.Windows.Controls.Primitives.PlacementMode;
using Border = System.Windows.Controls.Border;
using ContentPresenter = System.Windows.Controls.ContentPresenter;
using Binding = System.Windows.Data.Binding;
using RelativeSource = System.Windows.Data.RelativeSource;
using ControlTemplate = System.Windows.Controls.ControlTemplate;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

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

    /// <summary>Opens a card's help. Used by search after it scrolls to a row.
    ///
    /// Checking the toggle is the whole of it - the popup's IsOpen is bound to it - so search and
    /// a click go through exactly the same path rather than two that can drift apart.</summary>
    public static void Expand(FrameworkElement element)
    {
        foreach (var toggle in Descendants(element).OfType<ToggleButton>())
            if (toggle.Tag as string == HelpToggleTag)
                toggle.IsChecked = true;
    }

    private const string HelpToggleTag = "aorineq-help-toggle";

    /// <summary>A settings card: a (?) beside the title, and the body in a POPUP anchored to it.
    ///
    /// The body used to be appended to this same header panel, which grew the card and shoved
    /// every card below it down the page - while the pointer was still resting on the row the
    /// reader was aiming at. The search dropdown a few controls up the same window already
    /// carries a comment about why it is a Popup and not a panel; this is the same reason. A
    /// Popup is hosted in its own window and takes part in no layout pass here, so opening help
    /// cannot move anything. HelpDecoratorTests measures the page either side of a click and
    /// fails on a single pixel of movement.
    ///
    /// The (?) itself is invisible at rest and appears when the pointer is over the card, because
    /// thirty-five permanently visible question marks read as clutter rather than as help. It
    /// keeps its width while hidden - see BuildToggleStyle.</summary>
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
            Style = BuildToggleStyle(),
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Bound, not assigned: both halves of "Help for <title>" are localised, so a language
        // switch has to move both or the sentence ends up half in each language.
        LocBinding.SetFormatted(toggle, AutomationProperties.NameProperty,
            "help.affordance.name", topic.TitleKey);

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(title);
        titleRow.Children.Add(toggle);
        header.Children.Insert(0, titleRow);

        var body = new StackPanel { Margin = new Thickness(12, 10, 12, 12), MaxWidth = 360 };

        var bodyText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            // Stated explicitly, like every other text style in this window. A ui:TextBlock that is
            // not inside a themed container falls back to BLACK and measures 1.41:1 on this
            // window's Mica - a defect this project shipped once and had to pixel-sample to find.
            Foreground = Brush("TextFillColorSecondaryBrush"),
        };
        LocBinding.Set(bodyText, TextBlock.TextProperty, $"help.{topic.Key}.body");
        body.Children.Add(bodyText);

        if (topic.DocsAnchor is { } anchor)
        {
            var learnMore = new HyperlinkButton
            {
                NavigateUri = DocsUrl + anchor,
                Margin = new Thickness(0, 4, 0, 0),
                Padding = new Thickness(0),
                Foreground = Brush("TextFillColorPrimaryBrush"),
            };
            LocBinding.Set(learnMore, System.Windows.Controls.ContentControl.ContentProperty, "help.learn-more");
            body.Children.Add(learnMore);
        }

        var popup = new Popup
        {
            PlacementTarget = toggle,
            Placement = PlacementMode.Bottom,
            // Closes on a click anywhere else, exactly like the search results.
            StaysOpen = false,
            AllowsTransparency = true,
            // Shifted back off the 18px button, so the paragraph hangs under the title it belongs
            // to rather than starting at the question mark.
            HorizontalOffset = -24,
            Child = new Border
            {
                Background = Brush("ApplicationBackgroundBrush"),
                BorderBrush = Brush("ControlStrokeColorDefaultBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Child = body,
            },
        };

        // Two explicit handlers rather than a TwoWay binding from IsChecked to IsOpen. Both work;
        // this reads without the reader having to know how WPF converts bool? to bool, and it puts
        // the open and the close next to each other.
        toggle.Checked += (_, _) => popup.IsOpen = true;
        toggle.Unchecked += (_, _) => popup.IsOpen = false;

        // StaysOpen=False dismisses the popup on an outside click without touching the button, so
        // the button would be left lit with nothing under it. This puts the two back in step.
        popup.Closed += (_, _) => toggle.IsChecked = false;

        // A closed Popup measures to nothing and an open one is hosted in its own window, so this
        // adds no height to the card in either state. It lives in the header only so it inherits
        // the window's resources and language bindings.
        header.Children.Add(popup);

        CloseOnScroll(card, toggle);
    }

    /// <summary>Closes a card's help the moment the page scrolls under it.
    ///
    /// A Popup is placed in SCREEN coordinates and does not follow its placement target, so a
    /// wheel notch would slide the card away and leave its paragraph hanging over whatever
    /// arrived in its place - still pointing at a row that is no longer there. The inline panel
    /// this replaced could not come adrift like that, so the popup has to be told to let go.
    ///
    /// Subscribed once here rather than on each open. The card is already parented and laid out
    /// when this pass runs (that is why it runs at Loaded priority), the ScrollViewer it sits in
    /// lives exactly as long as it does, and a handler that outlives neither cannot leak.</summary>
    private static void CloseOnScroll(CardControl card, ToggleButton toggle)
    {
        if (Ancestors(card).OfType<System.Windows.Controls.ScrollViewer>().FirstOrDefault()
            is not { } scroller)
        {
            return;
        }

        scroller.ScrollChanged += (_, e) =>
        {
            // Only a real move. A ScrollChanged also fires when the EXTENT changes - which is what
            // opening the popup's own content can cause - and closing on that would make the help
            // shut itself the instant it opened.
            if (toggle.IsChecked == true && (e.VerticalChange != 0 || e.HorizontalChange != 0))
                toggle.IsChecked = false;
        };
    }

    /// <summary>The (?) button: no chrome, and invisible until the card is pointed at.
    ///
    /// Opacity rather than Visibility, deliberately. A collapsed button surrenders its width, so
    /// the title would jump sideways the moment the pointer arrived - the same defect as a card
    /// that grows, just on the other axis. At zero opacity the button still occupies its 18px and
    /// nothing moves when it fades in. It cannot be clicked while invisible either, because the
    /// only thing that reveals it is the pointer being over the card.
    ///
    /// Built here rather than as a XAML resource because everything else in this file is built in
    /// code, and a style in App.xaml would put half of one affordance in another file.</summary>
    private static Style BuildToggleStyle()
    {
        var template = new ControlTemplate(typeof(ToggleButton));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        template.VisualTree = presenter;

        var style = new Style(typeof(ToggleButton));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.TemplateProperty, template));
        style.Setters.Add(new Setter(FrameworkElement.WidthProperty, 18d));
        style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 18d));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(6, 0, 0, 0)));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.FontSizeProperty, 10d));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.CursorProperty,
            System.Windows.Input.Cursors.Hand));
        // The secondary TEXT brush, never Appearance="Primary": that paints from the Windows
        // accent, and this machine's accent is black - the same trap ModeRadio and AnchorCell
        // in the XAML already carry a comment about.
        style.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty,
            Brush("TextFillColorSecondaryBrush")));
        style.Setters.Add(new Setter(UIElement.OpacityProperty, 0d));

        // Revealed by the pointer being anywhere on the CARD, not on the 18px button itself -
        // nobody hunts for a control they cannot see.
        var cardHovered = new DataTrigger
        {
            Binding = new Binding(nameof(UIElement.IsMouseOver))
            {
                RelativeSource = new RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor)
                {
                    AncestorType = typeof(CardControl),
                },
            },
            Value = true,
        };
        cardHovered.Setters.Add(new Setter(UIElement.OpacityProperty, 1d));
        style.Triggers.Add(cardHovered);

        // A keyboard user never generates a hover, so focus reveals it too. Without this the
        // button is reachable by Tab and still invisible, which is worse than not being there.
        var focused = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
        focused.Setters.Add(new Setter(UIElement.OpacityProperty, 1d));
        style.Triggers.Add(focused);

        // Stays lit while its popup is open, so the pointer can leave the card to read the
        // paragraph without the button it came from vanishing behind it.
        var open = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        open.Setters.Add(new Setter(UIElement.OpacityProperty, 1d));
        style.Triggers.Add(open);

        var hovered = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hovered.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty,
            Brush("TextFillColorPrimaryBrush")));
        style.Triggers.Add(hovered);

        style.Seal();
        return style;
    }

    /// <summary>Anything that is not a card: the title in bold over the body, as a tooltip.</summary>
    private static void DecorateWithToolTip(FrameworkElement element, HelpTopic topic)
    {
        var stack = new StackPanel { MaxWidth = 320 };

        var title = new TextBlock
        {
            FontTypography = FontTypography.BodyStrong,
            TextWrapping = TextWrapping.Wrap,
        };
        LocBinding.Set(title, TextBlock.TextProperty, topic.TitleKey);
        stack.Children.Add(title);

        var body = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };
        LocBinding.Set(body, TextBlock.TextProperty, $"help.{topic.Key}.body");
        stack.Children.Add(body);

        element.ToolTip = new ToolTip { Content = stack };
        ToolTipService.SetInitialShowDelay(element, 400);
        ToolTipService.SetShowDuration(element, 30000);

        // A tooltip is invisible to someone driving the app from the keyboard, so the same words go
        // where a screen reader will actually find them.
        LocBinding.Set(element, AutomationProperties.HelpTextProperty, $"help.{topic.Key}.body");
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

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject node)
    {
        for (var parent = VisualTreeHelper.GetParent(node); parent is not null;
             parent = VisualTreeHelper.GetParent(parent))
        {
            yield return parent;
        }
    }

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
