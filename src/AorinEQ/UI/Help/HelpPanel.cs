using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AorinEQ.Core;
using Wpf.Ui.Controls;
using ScrollViewer = System.Windows.Controls.ScrollViewer;
using Brush = System.Windows.Media.Brush;
using Application = System.Windows.Application;
using Grid = System.Windows.Controls.Grid;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = Wpf.Ui.Controls.TextBlock;

namespace AorinEQ.UI;

/// <summary>Every topic for one window, in a scrollable panel.
///
/// Hover help only ever reaches someone who already suspects there is something to hover. This is
/// the surface for the other case - and it is where search lands a result that lives in a window
/// rather than on a settings page, because chasing an individual control inside a window that may
/// not even be open is the kind of thing that works on the machine it was written on and nowhere
/// else.
///
/// Generated from the catalogue, so a topic added there appears here without this file or the
/// window it belongs to being touched.</summary>
public static class HelpPanel
{
    /// <summary>Opens the panel for a surface, optionally scrolled to one topic.</summary>
    public static void Show(Window owner, string surface, string? scrollToKey = null)
    {
        var topics = HelpCatalogue.ForSurface(surface);
        if (topics.Count == 0) return;

        var stack = new StackPanel { Margin = new Thickness(20) };
        var anchors = new Dictionary<string, FrameworkElement>(StringComparer.Ordinal);

        var heading = new TextBlock
        {
            FontTypography = FontTypography.Title,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
            // Every text style states its own Foreground. A ui:TextBlock outside a themed container
            // falls back to black, which on Mica is the 1.41:1 defect this project already shipped.
            Foreground = Brush("TextFillColorPrimaryBrush"),
        };
        LocBinding.Set(heading, TextBlock.TextProperty, $"surface.{surface}.name");
        stack.Children.Add(heading);

        foreach (var topic in topics)
        {
            var block = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };

            var topicTitle = new TextBlock
            {
                FontTypography = FontTypography.BodyStrong,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("TextFillColorPrimaryBrush"),
            };
            LocBinding.Set(topicTitle, TextBlock.TextProperty, topic.TitleKey);
            block.Children.Add(topicTitle);

            var topicBody = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = Brush("TextFillColorSecondaryBrush"),
            };
            LocBinding.Set(topicBody, TextBlock.TextProperty, $"help.{topic.Key}.body");
            block.Children.Add(topicBody);

            if (topic.DocsAnchor is { } anchor)
            {
                var learnMore = new HyperlinkButton
                {
                    NavigateUri = DocsUrl + anchor,
                    Margin = new Thickness(0, 4, 0, 0),
                    Padding = new Thickness(0),
                    Foreground = Brush("TextFillColorPrimaryBrush"),
                };
                LocBinding.Set(learnMore, System.Windows.Controls.ContentControl.ContentProperty,
                    "help.learn-more");
                block.Children.Add(learnMore);
            }

            stack.Children.Add(block);
            anchors[topic.Key] = block;
        }

        var window = new FluentWindow
        {
            Title = Loc.T("help.panel.title"),   // a window CAPTION cannot be rebound live
            Owner = owner,
            Width = 460,
            Height = 620,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowBackdropType = WindowBackdropType.Mica,
            WindowCornerPreference = WindowCornerPreference.Round,
            ExtendsContentIntoTitleBar = true,
            Content = BuildBody(stack),
        };

        window.Loaded += (_, _) =>
        {
            if (scrollToKey is not null && anchors.TryGetValue(scrollToKey, out var target))
                target.BringIntoView();
        };

        window.Show();
    }

    private static Grid BuildBody(StackPanel stack)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var titleBar = new TitleBar();
        LocBinding.Set(titleBar, TitleBar.TitleProperty, "help.panel.title");
        Grid.SetRow(titleBar, 0);
        grid.Children.Add(titleBar);

        var scroller = new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetRow(scroller, 1);
        grid.Children.Add(scroller);

        return grid;
    }

    private const string DocsUrl = "https://github.com/weejiaquan/aorineq/blob/master/docs/reference.md";

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
