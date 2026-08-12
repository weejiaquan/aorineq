using System.Globalization;

namespace AorinEQ.Core;

/// <summary>One column of the EQ editor's band strip, as the text and state the controls show.
/// A pure projection of the band list, so "what the strip should display" is decided once and can
/// be checked without a window.</summary>
public sealed record EqBandColumn(
    int Number, EqBandType Type, string Fc, string GainDb, string Q, bool GainEnabled, bool Selected);

/// <summary>The band strip's view model. The strip used to be refreshed only by the incremental
/// paths that changed it (+ / × / a node drag), so every BULK replace — an AutoEq import, a
/// preset switch, pasted text, an aorineq:// preset link — left it showing the previous
/// chain while the curve was already correct. Projecting it from the band list instead means the
/// strip cannot disagree with the model, and the projection is testable on its own.</summary>
public static class EqStripModel
{
    /// <summary>Keeps a selection index valid for a (possibly replaced) band list: -1 when there
    /// are no bands, otherwise inside the list. A bulk replace with a shorter chain would
    /// otherwise leave an index pointing past the end.</summary>
    public static int ClampSelection(int selected, int bandCount) =>
        bandCount <= 0 ? -1 : Math.Clamp(selected, 0, bandCount - 1);

    public static IReadOnlyList<EqBandColumn> Build(IReadOnlyList<EqBand> bands, int selectedIndex)
    {
        var columns = new EqBandColumn[bands.Count];
        for (int i = 0; i < bands.Count; i++)
        {
            var band = bands[i];
            columns[i] = new EqBandColumn(
                i + 1, band.Type, FormatFc(band.Fc), FormatGain(band.GainDb), FormatQ(band.Q),
                band.HasGain, i == selectedIndex);
        }
        return columns;
    }

    // The editor's numeric side panel formats the selected band with these too, so both surfaces
    // always show the same number for the same value.
    public static string FormatFc(double fc) => fc.ToString("0.##", CultureInfo.InvariantCulture);
    public static string FormatGain(double db) => db.ToString("0.0", CultureInfo.InvariantCulture);
    public static string FormatQ(double q) => q.ToString("0.00", CultureInfo.InvariantCulture);
}
