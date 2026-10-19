using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using AorinEQ.Core;
using FontFamily = System.Windows.Media.FontFamily;
using Path = System.Windows.Shapes.Path;
using Size = System.Windows.Size;

namespace AorinEQ.UI;

/// <summary>A skin text that layout sizes, as a TextBlock is sized: the notice name. Drawn by
/// <see cref="PercentTextRenderer"/> - the outline, shadow, weight, italic and letter spacing every
/// other skin text gets - into the box the layout gives it, trimmed with an ellipsis when the box
/// is narrower than the text and aligned in it by the style's align.
///
/// The family is this element's <see cref="FontFamily"/>, not the style's: it is the TextElement
/// property TextBlock inherits, so left unset it takes the window's AppFontFamily and that chain's
/// CJK fallbacks.</summary>
internal sealed class SkinTextView : FrameworkElement
{
    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(typeof(SkinTextView));

    private SkinText _style = SkinLoader.DefaultNotice.Text;
    private double _scale = 1.0;
    private string _text = "";
    private double _textWidth;

    public SkinTextView()
    {
        AddVisualChild(Glyphs);
    }

    /// <summary>The drawn text: its geometry, fill, outline stroke and shadow effect.</summary>
    internal Path Glyphs { get; } = new() { IsHitTestVisible = false };

    public FontFamily FontFamily
    {
        get => (FontFamily)GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public string Text
    {
        get => _text;
        set
        {
            _text = value;
            InvalidateMeasure();
        }
    }

    /// <summary>The style it is drawn in and the scale every size in it is multiplied by.</summary>
    public void Apply(SkinText style, double scale)
    {
        _style = style;
        _scale = scale;
        InvalidateMeasure();
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) =>
        index == 0 ? Glyphs : throw new ArgumentOutOfRangeException(nameof(index));

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = PercentTextRenderer.Update(Glyphs, _style, _text, _scale,
            VisualTreeHelper.GetDpi(this).PixelsPerDip, FontFamily, availableSize.Width);
        _textWidth = size.Width;
        Glyphs.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // The style's x anchors the box, so the text's left edge is the box's anchor under its align.
        double anchor = _style.Align switch { "center" => finalSize.Width / 2, "right" => finalSize.Width, _ => 0 };
        double left = SkinMath.AlignedTextX(anchor, _textWidth, _style.Align);
        Glyphs.Arrange(new Rect(new System.Windows.Point(left, 0), Glyphs.DesiredSize));
        return finalSize;
    }
}
