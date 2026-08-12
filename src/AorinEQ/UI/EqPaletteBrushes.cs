using System.Windows;
using System.Windows.Media;
using AorinEQ.Core;

namespace AorinEQ.UI;

/// <summary>Publishes <see cref="EqPalette"/> as WPF brush resources, so the EQ surfaces can be
/// coloured from XAML with <c>{DynamicResource EqPlotBrush}</c> and friends.
///
/// Shared by the editor and the preset-link dialog's preview: both draw the same instrument panel,
/// and a preview that disagreed with the editor about what a preset looks like would be worse than
/// no preview. One seeding routine also means a key can never exist in one window and be missing in
/// the other — an unresolved DynamicResource silently falls back to the control default, which is
/// exactly the class of bug that put black text on a dark window in the first place.</summary>
public static class EqPaletteBrushes
{
    /// <summary>Resource keys, spelled once here and referenced by name in XAML.</summary>
    public const string Plot = "EqPlotBrush";
    public const string Panel = "EqPanelBrush";
    public const string Text = "EqTextBrush";
    public const string TextDim = "EqTextDimBrush";
    public const string Curve = "EqCurveBrush";
    public const string NodeSelected = "EqNodeSelectedBrush";
    public const string MeterTrack = "EqMeterTrackBrush";
    public const string MeterRms = "EqMeterRmsBrush";
    public const string MeterPeak = "EqMeterPeakBrush";
    public const string ClipIdle = "EqClipIdleBrush";
    public const string ClipIdleText = "EqClipIdleTextBrush";

    /// <summary>Seeds (or re-seeds, after a theme change) every EQ brush into
    /// <paramref name="resources"/> from the palette for the current Windows theme, and returns
    /// that palette for the code paths that draw without going through XAML.</summary>
    public static EqPalette Apply(ResourceDictionary resources)
    {
        var palette = EqPalette.For(SystemTheme.AppsUseLightTheme());
        resources[Plot] = Brush(palette.PlotBackground);
        resources[Panel] = Brush(palette.PanelBackground);
        resources[Text] = Brush(palette.Text);
        resources[TextDim] = Brush(palette.TextDim);
        resources[Curve] = Brush(palette.Curve);
        resources[NodeSelected] = Brush(palette.NodeSelected);
        resources[MeterTrack] = Brush(palette.MeterTrack);
        resources[MeterRms] = Brush(palette.MeterRms);
        resources[MeterPeak] = Brush(palette.MeterPeak);
        resources[ClipIdle] = Brush(palette.ClipIdle);
        resources[ClipIdleText] = Brush(palette.ClipIdleText);
        return palette;
    }

    /// <summary>A frozen WPF brush from a Core palette colour. Frozen because these are handed to
    /// elements redrawn at 30 fps and are never changed in place.</summary>
    public static SolidColorBrush Brush(System.Drawing.Color c)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(c.A, c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }
}
