using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AorinEQ.Core;
using Button = System.Windows.Controls.Button;
using HAlign = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;

namespace AorinEQ.UI.DesignerFields;

/// <summary>One art layer: a small thumbnail on the checkerboard, the file's name and its frame
/// size, and the three things that can be done to it - choose a file, build a sprite sheet from
/// a frame sequence, or (for optional art) remove it. The host owns the dialogs.</summary>
internal sealed class ArtField : Grid
{
    public const int ThumbnailHeight = 80;

    private (string Path, DateTime Written)? _thumbnailOf;

    public ArtField(bool optional)
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Thumbnail = new Image { Height = 40, MaxWidth = 96, Stretch = Stretch.Uniform };
        Children.Add(new System.Windows.Controls.Border
        {
            Background = DesignerBrushes.Checkerboard, Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = System.Windows.VerticalAlignment.Top, Child = Thumbnail,
        });

        FileName = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        FrameSize = new TextBlock { FontSize = 11, Margin = new Thickness(0, 2, 0, 6) };
        FrameSize.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        Browse = new Button { Content = Loc.T("designer.options-scroll.label"), Padding = new Thickness(10, 3, 10, 3) };
        Sheet = new Button
        {
            Content = Loc.T("designer.options-scroll.label-2"), Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(6, 0, 0, 0), ToolTip = Loc.T("designer.options-scroll.tooltip"),
        };
        Remove = new Button
        {
            Content = Loc.T("designer.art.remove"), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0),
            Visibility = optional ? Visibility.Visible : Visibility.Collapsed,
        };
        Browse.Click += (_, _) => BrowseRequested?.Invoke();
        Sheet.Click += (_, _) => SheetRequested?.Invoke();
        Remove.Click += (_, _) => RemoveRequested?.Invoke();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HAlign.Left };
        buttons.Children.Add(Browse);
        buttons.Children.Add(Sheet);
        buttons.Children.Add(Remove);
        var text = new StackPanel();
        text.Children.Add(FileName);
        text.Children.Add(FrameSize);
        text.Children.Add(buttons);
        SetColumn(text, 1);
        Children.Add(text);
    }

    public Image Thumbnail { get; }
    public TextBlock FileName { get; }
    public TextBlock FrameSize { get; }
    public Button Browse { get; }
    public Button Sheet { get; }
    public Button Remove { get; }

    public event Action? BrowseRequested;
    public event Action? SheetRequested;
    public event Action? RemoveRequested;

    /// <summary>Shows a layer's art. The thumbnail is decoded again only when the file changed;
    /// art that does not decode shows no thumbnail rather than failing the panel.</summary>
    public void ShowArt(string? path, string displayName, (int Width, int Height)? frameSize)
    {
        FileName.Text = displayName;
        FileName.ToolTip = path;
        FrameSize.Text = frameSize is { } size ? Loc.T("designer.art.frame-size", size.Width, size.Height) : "";
        Remove.IsEnabled = path is not null;

        (string, DateTime)? stamp = path is not null && File.Exists(path) ? (path, File.GetLastWriteTimeUtc(path)) : null;
        if (stamp == _thumbnailOf) return;
        _thumbnailOf = stamp;
        Thumbnail.Source = stamp is null ? null : TryThumbnail(path!);
    }

    private static BitmapSource? TryThumbnail(string path)
    {
        try
        {
            return SkinOsdWindow.LoadBitmap(path, ThumbnailHeight);
        }
        catch (Exception ex) when (SkinFrames.IsImagingFailure(ex))
        {
            return null;
        }
    }
}
