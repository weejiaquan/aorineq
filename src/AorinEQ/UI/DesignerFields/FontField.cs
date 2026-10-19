using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AorinEQ.Core;
using Binding = System.Windows.Data.Binding;
using ComboBox = System.Windows.Controls.ComboBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using VAlign = System.Windows.VerticalAlignment;

namespace AorinEQ.UI.DesignerFields;

/// <summary>An editable font picker whose list draws each family in itself. Editable so a skin's
/// font that is not installed here is shown and saved verbatim rather than silently replaced.</summary>
internal sealed class FontField : Grid
{
    private static IReadOnlyList<string>? s_families;
    private bool _setting;

    public FontField(string label)
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Children.Add(new TextBlock { Text = label, FontSize = 11, VerticalAlignment = VAlign.Center, Margin = new Thickness(0, 0, 8, 0) });
        Combo = new ComboBox { IsEditable = true, IsTextSearchEnabled = true, ItemsSource = Families, ItemTemplate = PreviewTemplate() };
        SetColumn(Combo, 1);
        Children.Add(Combo);
        Combo.SelectionChanged += (_, _) =>
        {
            if (!_setting && Combo.SelectedItem is string family) Change(family);
        };
        Combo.PreviewKeyDown += OnKeyDown;
        Combo.LostKeyboardFocus += (_, _) =>
        {
            if (Combo.IsKeyboardFocusWithin) return; // focus moved into the combo's own text box
            Commit();
            EditEnded?.Invoke();
        };
    }

    /// <summary>The installed families, sorted, read once per process.</summary>
    public static IReadOnlyList<string> Families => s_families ??=
        Fonts.SystemFontFamilies.Select(f => f.Source).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();

    public ComboBox Combo { get; }

    public string Value { get; private set; } = SkinClamps.DefaultFontFamily;

    public bool IsEditing => (Combo.Text ?? "").Trim() != Value;

    public event Action<string>? ValueChanged;

    public event Action? EditEnded;

    public void ShowValue(string family)
    {
        Value = family;
        _setting = true;
        Combo.SelectedItem = Families.FirstOrDefault(f => string.Equals(f, family, StringComparison.OrdinalIgnoreCase));
        Combo.Text = family;
        _setting = false;
    }

    public void Commit() =>
        Change(string.IsNullOrWhiteSpace(Combo.Text) ? SkinClamps.DefaultFontFamily : Combo.Text.Trim());

    private void Change(string family)
    {
        // Font names are case-insensitive: "arial" is the same font, not an edit to undo.
        if (string.Equals(family, Value, StringComparison.OrdinalIgnoreCase)) return;
        Value = family;
        ValueChanged?.Invoke(family);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Commit();
        EditEnded?.Invoke();
        e.Handled = true;
    }

    private static DataTemplate PreviewTemplate()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding());
        text.SetBinding(TextBlock.FontFamilyProperty, new Binding());
        var template = new DataTemplate { VisualTree = text };
        template.Seal();
        return template;
    }
}
