using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using AorinEQ.Core;
using Border = System.Windows.Controls.Border;
using FontFamily = System.Windows.Media.FontFamily;
using Grid = System.Windows.Controls.Grid;
using Image = System.Windows.Controls.Image;
using Size = System.Windows.Size;
using TextBlock = System.Windows.Controls.TextBlock;
using UserControl = System.Windows.Controls.UserControl;
// Aliased because this class inherits FrameworkElement, where HorizontalAlignment and
// VerticalAlignment are PROPERTIES - so the bare enum names resolve to those instead.
using HAlign = System.Windows.HorizontalAlignment;
using VAlign = System.Windows.VerticalAlignment;

namespace AorinEQ.UI;

/// <summary>The device-changed notice: a speaker glyph and a name, drawn in whatever
/// <see cref="SkinNotice"/> it is handed - one of the built-in <see cref="OsdPlates"/> or a
/// skin's own.
///
/// It draws itself two ways, one visible at a time. A plate is a rounded border that grows with
/// the name (the host caps it, and the name trims beyond that). Art is a skin's notice image at
/// its own size, with the glyph and the name placed on it where the skin says; the name is boxed
/// to the skin's text width so a long one trims inside the art instead of running off it.
///
/// Everything a skin wrote is untrusted: a colour that does not parse falls back to the default
/// plate's, and art that does not decode falls back to the default plate entirely.</summary>
public sealed class NoticeView : UserControl
{
    /// <summary>'Speakers' - the same codepoint in Segoe Fluent Icons and Segoe MDL2 Assets.</summary>
    public const string GlyphDevice = "";

    private const double GlyphColumnWidth = 32;

    private readonly Grid _root = new();
    private readonly Grid _plateContent = new() { VerticalAlignment = VAlign.Center };
    private readonly ColumnDefinition _glyphColumn = new();
    private readonly Grid _art = new();
    private readonly Image _artImage = new() { Stretch = Stretch.Fill, SnapsToDevicePixels = true };

    internal Border PlateBorder { get; } = new()
    {
        Effect = new DropShadowEffect { Opacity = 0.4, BlurRadius = 16, ShadowDepth = 0 },
    };

    internal TextBlock NoticeText { get; } = new()
    {
        TextTrimming = TextTrimming.CharacterEllipsis,
        IsHitTestVisible = false,
    };

    internal TextBlock NoticeGlyph { get; } = new()
    {
        Text = GlyphDevice,
        FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
        IsHitTestVisible = false,
    };

    public NoticeView()
    {
        Content = _root;
        Focusable = false;

        _plateContent.ColumnDefinitions.Add(_glyphColumn);
        _plateContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        PlateBorder.Child = _plateContent;

        _root.Children.Add(PlateBorder);
        _root.Children.Add(_art);

        Apply(SkinLoader.DefaultNotice, 1.0);
    }

    /// <summary>Rebuilds the visuals for <paramref name="look"/>. <paramref name="scale"/>
    /// multiplies every size in it - the skin's own scale for art, 1 for a built-in plate.</summary>
    public void Apply(SkinNotice look, double scale)
    {
        if (scale <= 0) scale = 1.0;

        if (look.HasArt)
        {
            BitmapSource frame;
            try
            {
                // First frame only, as the AirPlay strip does: a notice is up for a few seconds and
                // an animation timer for it would cost more than it is worth.
                frame = SkinFrames.Load(look.ArtPath!, look.ArtFrames, 10).Frames[0];
            }
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException
                                           or IOException or ArgumentException
                                           or UnauthorizedAccessException)
            {
                Apply(SkinLoader.DefaultNotice, 1.0);
                return;
            }
            ApplyArt(look, scale, frame);
        }
        else
        {
            ApplyPlate(look, scale);
        }
    }

    public void SetText(string text) => NoticeText.Text = text;

    /// <summary>The size the host window should take. Art is exactly its scaled size; a plate is
    /// measured against <paramref name="maxWidth"/> and never smaller than the volume bar.</summary>
    public Size MeasureNotice(double maxWidth)
    {
        if (_art.Visibility == Visibility.Visible) return new Size(_art.Width, _art.Height);

        PlateBorder.Measure(new Size(maxWidth, double.PositiveInfinity));
        // Never smaller than the volume bar it replaces, so a short name does not get a sliver.
        return new Size(Math.Max(OsdWindow.DarkPillWidth, PlateBorder.DesiredSize.Width),
            Math.Max(OsdWindow.DarkPillHeight, PlateBorder.DesiredSize.Height));
    }

    /// <summary>A brush for a colour a skin wrote, falling back to <paramref name="fallback"/>
    /// when it does not parse, and to null when neither does (or both are absent).</summary>
    internal static SolidColorBrush? Paint(string? color, string? fallback) =>
        PercentTextRenderer.Brush(color) ?? PercentTextRenderer.Brush(fallback);

    private void ApplyPlate(SkinNotice look, double scale)
    {
        var d = SkinLoader.DefaultNotice;
        StyleText(look, scale);

        PlateBorder.Background = Paint(look.Background, d.Background);
        PlateBorder.BorderBrush = Paint(look.Border, d.Border);
        PlateBorder.BorderThickness = new Thickness(PlateBorder.BorderBrush is null ? 0 : 1);
        PlateBorder.CornerRadius = new CornerRadius(look.CornerRadius * scale);
        PlateBorder.Padding = new Thickness(look.PadX * scale, look.PadY * scale, look.PadX * scale, look.PadY * scale);
        _glyphColumn.Width = new GridLength(GlyphColumnWidth * scale);

        NoticeGlyph.Margin = new Thickness(0);
        NoticeGlyph.HorizontalAlignment = HAlign.Left;
        NoticeGlyph.VerticalAlignment = VAlign.Center;
        NoticeText.Margin = new Thickness(0);
        NoticeText.Width = double.NaN;
        NoticeText.TextAlignment = TextAlignment.Left;
        NoticeText.HorizontalAlignment = HAlign.Stretch;
        NoticeText.VerticalAlignment = VAlign.Center;
        Grid.SetColumn(NoticeText, 1);

        _art.Children.Clear();
        _artImage.Source = null;
        _plateContent.Children.Clear();
        _plateContent.Children.Add(NoticeGlyph);
        _plateContent.Children.Add(NoticeText);

        _art.Visibility = Visibility.Collapsed;
        PlateBorder.Visibility = Visibility.Visible;
    }

    private void ApplyArt(SkinNotice look, double scale, BitmapSource frame)
    {
        StyleText(look, scale);

        _art.Width = look.Width * scale;
        _art.Height = look.Height * scale;
        _artImage.Source = frame;

        NoticeGlyph.Margin = new Thickness(look.Glyph.X * scale, look.Glyph.Y * scale, 0, 0);
        NoticeGlyph.HorizontalAlignment = HAlign.Left;
        NoticeGlyph.VerticalAlignment = VAlign.Top;
        NoticeText.Margin = new Thickness(look.Text.X * scale, look.Text.Y * scale, 0, 0);
        NoticeText.Width = look.TextWidth * scale;
        NoticeText.TextAlignment = look.Text.Align switch
        {
            "center" => TextAlignment.Center,
            "right" => TextAlignment.Right,
            _ => TextAlignment.Left,
        };
        NoticeText.HorizontalAlignment = HAlign.Left;
        NoticeText.VerticalAlignment = VAlign.Top;
        Grid.SetColumn(NoticeText, 0);

        _plateContent.Children.Clear();
        _art.Children.Clear();
        _art.Children.Add(_artImage);
        _art.Children.Add(NoticeGlyph);
        _art.Children.Add(NoticeText);

        PlateBorder.Visibility = Visibility.Collapsed;
        _art.Visibility = Visibility.Visible;
    }

    /// <summary>The name and the glyph, which look the same on a plate and on art. The glyph takes
    /// the text's colour unless the look names its own that parses.</summary>
    private void StyleText(SkinNotice look, double scale)
    {
        var d = SkinLoader.DefaultNotice;
        NoticeText.Foreground = Paint(look.Text.Color, d.Text.Color);
        // The default family is left unset so the name inherits the window's AppFontFamily, whose
        // fallback chain carries CJK names; only a family a skin chose is set outright.
        if (look.Text.FontFamily == d.Text.FontFamily) NoticeText.ClearValue(TextBlock.FontFamilyProperty);
        else NoticeText.FontFamily = new FontFamily(look.Text.FontFamily);
        NoticeText.FontSize = look.Text.FontSize * scale;
        // SemiBold rather than Normal for a look that is not bold: the weight the notice name
        // has always had.
        NoticeText.FontWeight = look.Text.Bold ? FontWeights.Bold : FontWeights.SemiBold;

        NoticeGlyph.Foreground = PercentTextRenderer.Brush(look.Glyph.Color) ?? NoticeText.Foreground;
        NoticeGlyph.FontSize = look.Glyph.Size * scale;
        NoticeGlyph.Visibility = look.Glyph.Show ? Visibility.Visible : Visibility.Collapsed;
    }
}
