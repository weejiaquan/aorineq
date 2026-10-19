// WPF and WinForms are both referenced by this project: the WPF types are named in full.
using TextBox = System.Windows.Controls.TextBox;

namespace AorinEQ.UI.DesignerFields;

/// <summary>What the designer's typed fields share about bad input: the box goes the theme's
/// critical colour (red without a theme) until a good value is shown again.</summary>
internal static class FieldStyle
{
    public static void ShowInvalid(TextBox box, bool invalid)
    {
        if (invalid)
            box.BorderBrush = box.TryFindResource("SystemFillColorCriticalBrush") as System.Windows.Media.Brush
                ?? System.Windows.Media.Brushes.Red;
        else
            box.ClearValue(System.Windows.Controls.Control.BorderBrushProperty);
    }
}
