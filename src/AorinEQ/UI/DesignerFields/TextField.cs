using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AorinEQ.Core;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;

namespace AorinEQ.UI.DesignerFields;

/// <summary>A caption over a text box that commits on Enter (single-line) or when it loses focus.
/// A value the validator refuses is not committed: the box turns red and keeps what was typed, so
/// it can be fixed rather than retyped.</summary>
internal sealed class TextField : StackPanel
{
    private readonly bool _multiline;
    private readonly Func<string, bool>? _validate;
    private bool _setting;

    public TextField(string label, bool multiline = false, int maxLength = 0, Func<string, bool>? validate = null)
    {
        _multiline = multiline;
        _validate = validate;
        var caption = new TextBlock { Text = label, FontSize = 11, Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap };
        Box = new TextBox
        {
            MaxLength = maxLength,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinHeight = multiline ? 56 : 0,
            MaxHeight = multiline ? 120 : double.PositiveInfinity,
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
        };
        Children.Add(caption);
        Children.Add(Box);
        Box.PreviewKeyDown += OnKeyDown;
        Box.LostKeyboardFocus += (_, _) =>
        {
            Commit();
            EditEnded?.Invoke();
        };
        Box.TextChanged += (_, _) => { if (!_setting) ShowInvalid(false); };
    }

    public TextBox Box { get; }

    public string Value { get; private set; } = "";

    public bool IsInvalid { get; private set; }

    public bool IsEditing => Box.Text != Value;

    public event Action<string>? ValueChanged;

    public event Action? EditEnded;

    /// <summary>The validator refused what was typed.</summary>
    public event Action? Rejected;

    public void ShowValue(string value)
    {
        Value = value;
        _setting = true;
        Box.Text = value;
        _setting = false;
        ShowInvalid(false);
    }

    public void Commit()
    {
        var text = Box.Text;
        if (_validate is not null && !_validate(text))
        {
            ShowInvalid(true);
            Rejected?.Invoke();
            return;
        }
        if (text == Value) return;
        Value = text;
        ValueChanged?.Invoke(text);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _multiline) return;
        Commit();
        EditEnded?.Invoke();
        e.Handled = true;
    }

    private void ShowInvalid(bool invalid)
    {
        IsInvalid = invalid;
        FieldStyle.ShowInvalid(Box, invalid);
    }
}
