using System.Globalization;
using System.Windows;
using ApoVolume.Core;

namespace ApoVolume.UI;

public enum EqPresetLinkChoice { ApplyAndSave, SaveOnly, Cancel }

/// <summary>The apo-volume:// trust boundary for EQ presets: one modal confirmation before
/// anything is applied or written, showing where the preset came from, what it contains, which
/// scope it would land in, and — the reason this dialog exists rather than a yes/no box — the
/// response curve itself, so the user can see the tuning before accepting it.
///
/// Everything on show is untrusted: the name has already passed file-name validation, the source
/// is a host name or the fixed "shared link" wording, and the curve is drawn from bands the
/// parser clamped to the model's limits. Text is set as Text (never markup), so a preset can't
/// dress its name up as UI.
///
/// Owned by no window (a link can arrive with nothing else open), centered, topmost so it can't
/// get lost behind the browser that fired the link.</summary>
public partial class EqPresetLinkDialog : Window
{
    private readonly IReadOnlyList<EqBand> _bands;
    private readonly int _dbRange;
    private EqPresetLinkChoice _choice = EqPresetLinkChoice.Cancel; // X / Esc are a Cancel

    private EqPresetLinkDialog(EqPreset preset, string source, string scopeDescription, bool overwrites)
    {
        InitializeComponent();
        _bands = preset.Bands;
        _dbRange = EqCurveRenderer.FittingDbRange(preset.Bands);

        HeadingText.Text = $"Apply EQ preset '{preset.Name}' from {source}?";
        DetailText.Text = string.Create(CultureInfo.InvariantCulture,
            $"{preset.Bands.Count} band{(preset.Bands.Count == 1 ? "" : "s")} · "
            + $"preset preamp {preset.PreampDb:0.0} dB");
        ScopeText.Text = $"Apply & Save applies it to {scopeDescription} and saves it as a preset. "
            + "Save only just adds it to your presets.";
        OverwriteText.Visibility = overwrites ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows the confirmation modally and returns the user's choice.
    /// <paramref name="source"/> is the hosting site's host name, or the fixed wording for a
    /// preset that travelled inside the link itself.</summary>
    public static EqPresetLinkChoice Confirm(EqPreset preset, string source,
        string scopeDescription, bool overwrites)
    {
        var dialog = new EqPresetLinkDialog(preset, source, scopeDescription, overwrites);
        dialog.ShowDialog();
        return dialog._choice;
    }

    /// <summary>The wording used in place of a host for an inline (data=) share link.</summary>
    public const string SharedLinkSource = "a shared link";

    private void OnCurveCanvasSizeChanged(object sender, SizeChangedEventArgs e) =>
        EqCurveRenderer.DrawPreview(CurveCanvas, _bands, _dbRange);

    private void OnApplyAndSave(object sender, RoutedEventArgs e)
    {
        _choice = EqPresetLinkChoice.ApplyAndSave;
        Close();
    }

    private void OnSaveOnly(object sender, RoutedEventArgs e)
    {
        _choice = EqPresetLinkChoice.SaveOnly;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
