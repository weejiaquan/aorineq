using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AorinEQ.Core;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;
using VAlign = System.Windows.VerticalAlignment;

namespace AorinEQ.UI.DesignerFields;

/// <summary>A colour as a swatch (which asks the host to open the picker) and a hex box that
/// formats what is typed to #AARRGGBB on commit and puts the last good colour back on junk.</summary>
internal sealed class ColorField : Grid
{
    private readonly System.Windows.Controls.Border _chip = new();
    private bool _setting;

    public ColorField(string label)
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var caption = new TextBlock { Text = label, FontSize = 11, VerticalAlignment = VAlign.Center, Margin = new Thickness(0, 0, 8, 0) };
        Swatch = new Button
        {
            Width = 34, Height = 24, Padding = new Thickness(2),
            Content = new System.Windows.Controls.Border { Background = DesignerBrushes.Checkerboard, Child = _chip },
        };
        Hex = new TextBox { MinWidth = 96, Margin = new Thickness(8, 0, 0, 0), VerticalContentAlignment = VAlign.Center };
        SetColumn(Swatch, 1);
        SetColumn(Hex, 2);
        Children.Add(caption);
        Children.Add(Swatch);
        Children.Add(Hex);
        Swatch.Click += (_, _) => PickRequested?.Invoke(this);
        Hex.PreviewKeyDown += OnKeyDown;
        Hex.LostKeyboardFocus += (_, _) =>
        {
            Commit();
            EditEnded?.Invoke();
        };
        Hex.TextChanged += (_, _) => { if (!_setting) FieldStyle.ShowInvalid(Hex, false); };
    }

    public Button Swatch { get; }

    public TextBox Hex { get; }

    public string Value { get; private set; } = "#FFFFFFFF";

    public bool IsEditing => Hex.Text != Value;

    public event Action<string>? ValueChanged;

    public event Action? EditEnded;

    /// <summary>The swatch was clicked: the host opens the picker on <see cref="Swatch"/>.</summary>
    public event Action<ColorField>? PickRequested;

    /// <summary>Shows a colour without reporting it; one that parses is shown formatted.</summary>
    public void ShowValue(string color)
    {
        Value = SkinColor.Format(color) ?? color;
        _setting = true;
        Hex.Text = Value;
        _setting = false;
        _chip.Background = PercentTextRenderer.Brush(color) ?? Brushes.Transparent;
        FieldStyle.ShowInvalid(Hex, false);
    }

    /// <summary>A colour from the picker: shown, and reported when it differs.</summary>
    public void ApplyPicked(string color)
    {
        bool changed = !string.Equals(SkinColor.Format(color), Value, StringComparison.OrdinalIgnoreCase);
        ShowValue(color);
        if (changed) ValueChanged?.Invoke(Value);
    }

    public void Commit()
    {
        if (SkinColor.Format(Hex.Text) is { } hex)
        {
            ApplyPicked(hex);
            return;
        }
        FieldStyle.ShowInvalid(Hex, true);
        _setting = true;
        Hex.Text = Value;
        _setting = false;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Commit();
        EditEnded?.Invoke();
        e.Handled = true;
    }
}
