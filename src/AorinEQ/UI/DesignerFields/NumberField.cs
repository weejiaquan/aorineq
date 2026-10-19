using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AorinEQ.Core;
// WPF and WinForms are both referenced by this project, so the names below are
// ambiguous without an explicit alias. Every one of these is the WPF type.
using Cursors = System.Windows.Input.Cursors;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using TextBox = System.Windows.Controls.TextBox;
using VAlign = System.Windows.VerticalAlignment;

namespace AorinEQ.UI.DesignerFields;

/// <summary>A labelled number box the way image editors do them: drag the label to scrub, ↑/↓
/// step by one and Shift+↑/↓ by ten, Enter or leaving the box commits. Out-of-range input is
/// pulled into range (the tooltip says what the range is); anything that is not a finite number
/// turns the box red and puts the last good value back. "NaN" and "Infinity" parse as doubles,
/// so they are rejected explicitly - a NaN font size is an exception the moment WPF lays it out.</summary>
internal sealed class NumberField : Grid
{
    /// <summary>Screen pixels of label drag per step.</summary>
    public const double ScrubPixelsPerStep = 2;

    private readonly double _min, _max, _step;
    private readonly int _decimals;
    private bool _setting;
    private double? _scrubOrigin;
    private double _scrubStartValue;

    public NumberField(string label, double min, double max, int decimals = 0, double step = 1)
    {
        _min = min;
        _max = Math.Max(min, max);
        _decimals = decimals;
        _step = step;
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Label = new TextBlock
        {
            Text = label, FontSize = 11, VerticalAlignment = VAlign.Center,
            Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.SizeWE,
        };
        Box = new TextBox { MinWidth = 56, VerticalContentAlignment = VAlign.Center };
        SetColumn(Box, 1);
        Children.Add(Label);
        Children.Add(Box);
        ToolTip = Loc.T("designer.field.range", Format(_min, decimals), Format(_max, decimals));

        Box.PreviewKeyDown += OnKeyDown;
        Box.LostKeyboardFocus += (_, _) =>
        {
            Commit();
            EditEnded?.Invoke();
        };
        Box.TextChanged += (_, _) => { if (!_setting) ShowInvalid(false); };
        Label.MouseLeftButtonDown += OnScrubStart;
        Label.MouseMove += OnScrubMove;
        Label.MouseLeftButtonUp += OnScrubEnd;
        Label.LostMouseCapture += (_, _) => _scrubOrigin = null;
        ShowValue(_min);
    }

    public TextBlock Label { get; }

    public TextBox Box { get; }

    public double Value { get; private set; }

    public bool IsInvalid { get; private set; }

    /// <summary>The box holds typing that has not been committed.</summary>
    public bool IsEditing => Box.Text != Format(Value, _decimals);

    /// <summary>The user changed the value (scrub, arrows, Enter, leaving the box).</summary>
    public event Action<double>? ValueChanged;

    /// <summary>An edit finished: the box lost focus, Enter was pressed, or a scrub ended.</summary>
    public event Action? EditEnded;

    /// <summary>Shows a value without reporting it.</summary>
    public void ShowValue(double value)
    {
        Value = Math.Round(Math.Clamp(value, _min, _max), _decimals, MidpointRounding.AwayFromZero);
        SetText(Format(Value, _decimals));
        ShowInvalid(false);
    }

    public void Commit()
    {
        if (TryParse(Box.Text, _min, _max, _decimals, out var parsed))
        {
            Change(parsed);
            return;
        }
        ShowInvalid(true);
        SetText(Format(Value, _decimals));
    }

    public static bool TryParse(string text, double min, double max, int decimals, out double value)
    {
        value = 0;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed) || !double.IsFinite(parsed))
            return false;
        value = Math.Round(Math.Clamp(parsed, min, max), decimals, MidpointRounding.AwayFromZero);
        return true;
    }

    public static double Stepped(double value, bool up, bool large, double step, double min, double max, int decimals) =>
        Math.Round(Math.Clamp(value + (up ? step : -step) * (large ? 10 : 1), min, max), decimals, MidpointRounding.AwayFromZero);

    public static double Scrubbed(double start, double pixels, double step, double min, double max, int decimals) =>
        Math.Round(Math.Clamp(start + Math.Round(pixels / ScrubPixelsPerStep) * step, min, max), decimals, MidpointRounding.AwayFromZero);

    public static string Format(double value, int decimals) =>
        value.ToString(decimals == 0 ? "0" : "0." + new string('#', decimals), CultureInfo.CurrentCulture);

    private void Change(double next)
    {
        bool changed = next != Value;
        ShowValue(next);
        if (changed) ValueChanged?.Invoke(next);
    }

    private void SetText(string text)
    {
        _setting = true;
        Box.Text = text;
        _setting = false;
    }

    private void ShowInvalid(bool invalid)
    {
        IsInvalid = invalid;
        FieldStyle.ShowInvalid(Box, invalid);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Commit();
                EditEnded?.Invoke();
                e.Handled = true;
                break;
            case Key.Up or Key.Down:
                double current = TryParse(Box.Text, _min, _max, _decimals, out var typed) ? typed : Value;
                Change(Stepped(current, e.Key == Key.Up, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), _step, _min, _max, _decimals));
                e.Handled = true;
                break;
        }
    }

    private void OnScrubStart(object sender, MouseButtonEventArgs e)
    {
        if (!IsEnabled || !Label.CaptureMouse()) return;
        _scrubOrigin = e.GetPosition(this).X;
        _scrubStartValue = Value;
        e.Handled = true;
    }

    private void OnScrubMove(object sender, MouseEventArgs e)
    {
        if (_scrubOrigin is not { } origin || e.LeftButton != MouseButtonState.Pressed) return;
        Change(Scrubbed(_scrubStartValue, e.GetPosition(this).X - origin, _step, _min, _max, _decimals));
    }

    private void OnScrubEnd(object sender, MouseButtonEventArgs e)
    {
        if (_scrubOrigin is null) return;
        Label.ReleaseMouseCapture();
        EditEnded?.Invoke();
    }
}
