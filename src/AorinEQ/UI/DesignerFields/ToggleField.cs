using System.Windows.Controls;

namespace AorinEQ.UI.DesignerFields;

/// <summary>An on/off switch with its label, reporting only what the user changed.</summary>
internal sealed class ToggleField : Grid
{
    private bool _setting;

    public ToggleField(string label)
    {
        Switch = new Wpf.Ui.Controls.ToggleSwitch { Content = label };
        Switch.Checked += (_, _) => Raise();
        Switch.Unchecked += (_, _) => Raise();
        Children.Add(Switch);
    }

    public Wpf.Ui.Controls.ToggleSwitch Switch { get; }

    public bool Value => Switch.IsChecked == true;

    public event Action<bool>? Toggled;

    public void ShowValue(bool on)
    {
        _setting = true;
        Switch.IsChecked = on;
        _setting = false;
    }

    private void Raise()
    {
        if (!_setting) Toggled?.Invoke(Value);
    }
}
