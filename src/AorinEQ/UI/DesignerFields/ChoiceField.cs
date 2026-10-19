using System.Windows;
using System.Windows.Controls;
using ComboBox = System.Windows.Controls.ComboBox;
using VAlign = System.Windows.VerticalAlignment;

namespace AorinEQ.UI.DesignerFields;

/// <summary>A label and a drop-down of fixed choices, each a shown label over a stored value.</summary>
internal sealed class ChoiceField : Grid
{
    private readonly IReadOnlyList<(string Label, string Value)> _choices;
    private bool _setting;

    public ChoiceField(string label, IReadOnlyList<(string Label, string Value)> choices)
    {
        _choices = choices;
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Children.Add(new TextBlock { Text = label, FontSize = 11, VerticalAlignment = VAlign.Center, Margin = new Thickness(0, 0, 8, 0) });
        Combo = new ComboBox();
        foreach (var (text, _) in choices) Combo.Items.Add(new ComboBoxItem { Content = text });
        SetColumn(Combo, 1);
        Children.Add(Combo);
        Combo.SelectionChanged += (_, _) =>
        {
            if (!_setting && Combo.SelectedIndex >= 0) ValueChanged?.Invoke(Value);
        };
    }

    public ComboBox Combo { get; }

    public string Value => _choices[Math.Max(0, Combo.SelectedIndex)].Value;

    public event Action<string>? ValueChanged;

    public void ShowValue(string value)
    {
        _setting = true;
        Combo.SelectedIndex = Math.Max(0, _choices.ToList().FindIndex(c => c.Value == value));
        _setting = false;
    }
}
