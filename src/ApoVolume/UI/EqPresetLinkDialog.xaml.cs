using System.Globalization;
using System.Windows;
using ApoVolume.Core;

namespace ApoVolume.UI;

public enum EqPresetLinkChoice { ApplyAndSave, SaveOnly, Cancel }

/// <summary>What the user chose, and the preset it applies to (fetched by the dialog itself for
/// a hosted link).</summary>
public sealed record EqPresetLinkResult(EqPresetLinkChoice Choice, EqPreset? Preset);

/// <summary>The apo-volume:// trust boundary for EQ presets: one modal confirmation before
/// anything is fetched, applied or written, showing where the preset came from, which scope it
/// would land in, and — the reason this dialog exists rather than a yes/no box — the response
/// curve itself, so the user can see the tuning before accepting it.
///
/// For a HOSTED preset the file is not downloaded until the user clicks. **Preview** fetches and
/// draws it without changing anything; Apply &amp; Save / Save only fetch and then act. This
/// keeps the same boundary <see cref="SkinInstallDialog"/> has — a page that fires links can't
/// make the app hit URLs of its choosing — while still letting the user see the curve first. An
/// inline (data=) preset is already in hand, so it is drawn immediately.
///
/// Everything on show is untrusted: the name has passed file-name validation and is truncated
/// for display, the source is a host name or fixed wording on its OWN line (a long name can't
/// push the provenance out of view), and the curve is drawn from bands the parser clamped. Text
/// is set as Text, never markup, so a preset can't dress its name up as UI.
///
/// Owned by no window (a link can arrive with nothing else open), centered, topmost so it can't
/// get lost behind the browser that fired the link.</summary>
public partial class EqPresetLinkDialog : Window
{
    /// <summary>The wording used in place of a host for an inline (data=) share link.</summary>
    public const string SharedLinkSource = "a shared link";

    /// <summary>How much of an untrusted preset name is shown before it is cut short.</summary>
    private const int MaxShownNameLength = 48;

    private readonly Func<Task<EqPreset>>? _fetch;
    private EqPreset? _preset;
    private int _dbRange = EqCurveRenderer.DbRanges[0];
    private EqPresetLinkChoice _choice = EqPresetLinkChoice.Cancel; // X / Esc are a Cancel
    private bool _busy;

    private EqPresetLinkDialog(string name, string source, string scopeDescription,
        bool overwrites, EqPreset? preset, Func<Task<EqPreset>>? fetch)
    {
        InitializeComponent();
        _fetch = fetch;

        // Grapheme-aware: a name is never cut into a different glyph. Characters that could
        // disguise the rest of the string (bidi overrides) never get this far — FileNames
        // refuses them, so a link carrying one is malformed.
        HeadingText.Text = $"Apply EQ preset '{FileNames.ForDisplay(name, MaxShownNameLength)}'?";
        SourceText.Text = $"From {source}";
        ScopeText.Text = $"Apply & Save applies it to {scopeDescription} and saves it as a preset. "
            + "Save only just adds it to your presets.";
        OverwriteText.Visibility = overwrites ? Visibility.Visible : Visibility.Collapsed;

        if (preset is not null)
        {
            ShowPreset(preset);
        }
        else
        {
            PreviewButton.Visibility = Visibility.Visible;
            DetailText.Text = "This preset is hosted on that site. Nothing is downloaded until "
                + "you choose Preview, Apply & Save or Save only.";
        }
    }

    /// <summary>Shows the confirmation modally and returns the user's choice together with the
    /// preset it applies to. <paramref name="source"/> is the hosting site's host name, or
    /// <see cref="SharedLinkSource"/> for a preset that travelled inside the link.
    /// Exactly one of <paramref name="preset"/> (inline) and <paramref name="fetch"/> (hosted)
    /// is given; <paramref name="fetch"/> must throw <see cref="InvalidOperationException"/>
    /// with a readable message on any failure.</summary>
    public static EqPresetLinkResult Confirm(string name, string source, string scopeDescription,
        bool overwrites, EqPreset? preset, Func<Task<EqPreset>>? fetch)
    {
        var dialog = new EqPresetLinkDialog(name, source, scopeDescription, overwrites, preset, fetch);
        dialog.ShowDialog();
        return new EqPresetLinkResult(dialog._choice, dialog._preset);
    }

    private void ShowPreset(EqPreset preset)
    {
        _preset = preset;
        _dbRange = EqCurveRenderer.FittingDbRange(preset.Bands);
        DetailText.Text = string.Create(CultureInfo.InvariantCulture,
            $"{preset.Bands.Count} band{(preset.Bands.Count == 1 ? "" : "s")} · "
            + $"preset preamp {preset.PreampDb:0.0} dB");
        CurvePlaceholder.Visibility = Visibility.Collapsed;
        PreviewButton.Visibility = Visibility.Collapsed;
        DrawCurve();
    }

    /// <summary>Fetches the hosted preset if it isn't in hand yet. False means the caller must
    /// not proceed — either a fetch is already running, or it failed and the reason is on screen
    /// with the dialog still open so the user can cancel.</summary>
    private async Task<bool> EnsurePresetAsync()
    {
        if (_preset is not null)
            return true;
        if (_fetch is null || _busy)
            return false;

        _busy = true;
        SetButtonsEnabled(false);
        StatusText.Text = "Downloading…";
        try
        {
            var preset = await _fetch();
            StatusText.Text = "";
            ShowPreset(preset);
            return true;
        }
        catch (Exception ex)
        {
            // Everything: this is reached from async void handlers, where an escaping exception
            // would take the process down rather than fail a dialog.
            StatusText.Text = ex.Message;
            return false;
        }
        finally
        {
            _busy = false;
            SetButtonsEnabled(true);
        }
    }

    private void SetButtonsEnabled(bool enabled) =>
        PreviewButton.IsEnabled = ApplySaveButton.IsEnabled = SaveOnlyButton.IsEnabled = enabled;

    private void OnCurveCanvasSizeChanged(object sender, SizeChangedEventArgs e) => DrawCurve();

    private void DrawCurve()
    {
        if (_preset is { } preset)
            EqCurveRenderer.DrawPreview(CurveCanvas, preset.Bands, _dbRange);
    }

    private async void OnPreview(object sender, RoutedEventArgs e) => await EnsurePresetAsync();

    private async void OnApplyAndSave(object sender, RoutedEventArgs e) =>
        await CloseWithAsync(EqPresetLinkChoice.ApplyAndSave);

    private async void OnSaveOnly(object sender, RoutedEventArgs e) =>
        await CloseWithAsync(EqPresetLinkChoice.SaveOnly);

    private async Task CloseWithAsync(EqPresetLinkChoice choice)
    {
        if (!await EnsurePresetAsync())
            return;
        _choice = choice;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
