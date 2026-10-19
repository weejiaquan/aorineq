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
    /// <summary>DIPs of label drag per step.</summary>
    public const double ScrubPixelsPerStep = 2;

    private readonly double _step;
    private double _min, _max;
    private readonly int _decimals;
    private bool _setting;
    private double? _scrubOrigin;
    private double _scrubStartValue;
    private bool _stepped;

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
            Focusable = true, FocusVisualStyle = null,
        };
        Box = new TextBox { MinWidth = 56, VerticalContentAlignment = VAlign.Center };
        SetColumn(Box, 1);
        Children.Add(Label);
        Children.Add(Box);
        ShowRange();

        Box.PreviewKeyDown += OnKeyDown;
        Box.LostKeyboardFocus += (_, _) =>
        {
            Commit();
            EndEdit();
        };
        Box.TextChanged += (_, _) => { if (!_setting) ShowInvalid(false); };
        // Focusable only so a click can give it focus and Esc reaches a scrub; Tab passes it by.
        KeyboardNavigation.SetIsTabStop(Label, false);
        Label.PreviewKeyDown += OnKeyDown;
        // Arrow steps on the label are an edit that leaving it ends, as leaving the box does.
        Label.LostKeyboardFocus += (_, _) => { if (_stepped) EndEdit(); };
        Label.MouseLeftButtonDown += OnScrubStart;
        Label.MouseMove += OnScrubMove;
        Label.MouseLeftButtonUp += OnScrubEnd;
        Label.LostMouseCapture += (_, _) => OnScrubCaptureLost();
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

    /// <summary>Changes the range in place - the art it is measured against changed size - so the
    /// field being typed in, stepped or scrubbed is not replaced. The value is clamped to it by the
    /// next <see cref="ShowValue"/> or commit, not here: this reports nothing.</summary>
    public void SetRange(double min, double max)
    {
        max = Math.Max(min, max);
        if (min == _min && max == _max) return;
        _min = min;
        _max = max;
        ShowRange();
    }

    private void ShowRange() => ToolTip = Loc.T("designer.field.range", Format(_min, _decimals), Format(_max, _decimals));

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
        Math.Round(Math.Clamp(start + Math.Round(pixels / ScrubPixelsPerStep, MidpointRounding.AwayFromZero) * step, min, max), decimals, MidpointRounding.AwayFromZero);

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
        if (HandleKey(e.Key, e.KeyboardDevice.Modifiers)) e.Handled = true;
    }

    /// <summary>The field's keys: Enter commits, ↑/↓ step (by ten with Shift), Esc during a scrub
    /// puts back the value from before it. Returns whether the key was the field's.</summary>
    internal bool HandleKey(Key key, ModifierKeys modifiers)
    {
        switch (key)
        {
            case Key.Enter:
                Commit();
                EndEdit();
                return true;
            case Key.Up or Key.Down:
                double current = TryParse(Box.Text, _min, _max, _decimals, out var typed) ? typed : Value;
                Change(Stepped(current, key == Key.Up, modifiers.HasFlag(ModifierKeys.Shift), _step, _min, _max, _decimals));
                _stepped = true;
                return true;
            case Key.Escape when _scrubOrigin is not null:
                Change(_scrubStartValue);
                Label.ReleaseMouseCapture(); // ends the scrub through OnScrubCaptureLost
                return true;
            default:
                return false;
        }
    }

    private void OnScrubStart(object sender, MouseButtonEventArgs e)
    {
        // Focus first: it commits any typing in the box before the scrub starts from Value, and
        // puts Esc where the scrub hears it.
        Label.Focus();
        if (BeginScrub(e.GetPosition(this).X)) e.Handled = true;
    }

    /// <summary>Starts a scrub with the pointer at <paramref name="x"/> DIPs into this field;
    /// false when the label could not take the mouse.</summary>
    internal bool BeginScrub(double x)
    {
        if (!IsEnabled || !Label.CaptureMouse()) return false;
        _scrubOrigin = x;
        _scrubStartValue = Value;
        return true;
    }

    private void OnScrubMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) ScrubTo(e.GetPosition(this).X);
    }

    /// <summary>The pointer of a running scrub is at <paramref name="x"/> DIPs into this field.</summary>
    internal void ScrubTo(double x)
    {
        if (_scrubOrigin is { } origin)
            Change(Scrubbed(_scrubStartValue, x - origin, _step, _min, _max, _decimals));
    }

    // Every way a scrub ends - the button released, capture taken away, Esc - goes through losing
    // capture, so the edit ends exactly once and the history's merge run never stays open.
    private void OnScrubEnd(object sender, MouseButtonEventArgs e)
    {
        if (_scrubOrigin is not null) Label.ReleaseMouseCapture();
    }

    private void OnScrubCaptureLost()
    {
        if (_scrubOrigin is null) return;
        _scrubOrigin = null;
        EndEdit();
    }

    private void EndEdit()
    {
        _stepped = false;
        EditEnded?.Invoke();
    }
}
