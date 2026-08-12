using System.Windows;
using System.Windows.Controls;
using ApoVolume.Core;

namespace ApoVolume.UI;

/// <summary>Bulk text entry for an EQ scope: shows the current chain in Equalizer APO
/// ParametricEQ format, accepts a pasted/typed replacement, and validates it with the SAME
/// parser the AutoEq/file imports use (<see cref="EqPreset.TryParse"/>). Validation is live —
/// the offending line and reason are shown inline and OK is blocked — so a scope is never
/// partially replaced.</summary>
public partial class EqTextDialog : Window
{
    /// <summary>The parsed replacement, set only when the dialog closes with OK.</summary>
    public EqPreset? Result { get; private set; }

    public EqTextDialog(EqPreset current)
    {
        InitializeComponent();
        TextArea.Text = current.Bands.Count == 0 && current.PreampDb == 0
            ? "Preamp: 0.0 dB" + Environment.NewLine
            : current.Serialize().ReplaceLineEndings();
        Loaded += (_, _) =>
        {
            TextArea.Focus();
            TextArea.CaretIndex = TextArea.Text.Length;
            Validate();
        };
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        CopiedText.Text = "";
        Validate();
    }

    /// <summary>Parses the current text, updating the inline error and the OK button.
    /// Returns the preset when valid.</summary>
    private EqPreset? Validate()
    {
        if (EqPreset.TryParse("(text)", TextArea.Text, out var preset, out var error))
        {
            ErrorText.Text = preset.Bands.Count == 0
                ? "No filter lines — OK will clear this scope's bands."
                : $"{preset.Bands.Count} filter(s), preamp {preset.PreampDb:0.0} dB.";
            ErrorText.Foreground = System.Windows.Media.Brushes.Gray;
            OkButton.IsEnabled = true;
            return preset;
        }
        ErrorText.Text = error;
        ErrorText.Foreground = System.Windows.Media.Brushes.IndianRed;
        OkButton.IsEnabled = false;
        return null;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        // Re-validated here as well as on every keystroke: the button state is a convenience,
        // this is the gate that actually prevents a partial apply.
        if (Validate() is not { } preset)
            return;
        Result = preset;
        DialogResult = true;
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(TextArea.Text);
            CopiedText.Text = "Copied.";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The clipboard is a shared OS resource another process can hold open.
            CopiedText.Text = "Couldn't access the clipboard — try again.";
        }
    }
}
