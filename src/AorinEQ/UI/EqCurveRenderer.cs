using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AorinEQ.Core;
// WinForms is referenced app-wide (tray icon); pin the WPF types this renderer means.
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace AorinEQ.UI;

/// <summary>The EQ response plot's coordinate mapping and curve geometry. The editor draws
/// per-band curves, a grid and draggable nodes on top of this; the read-only surfaces — the
/// apply-preset confirm dialog's preview and Simple mode's curve — use it directly. One
/// definition of "where does this frequency and gain land on screen", so a preview can never
/// disagree with the editor about what a preset looks like.</summary>
public static class EqCurveRenderer
{
    public const double FMin = 20, FMax = 20000;

    /// <summary>Curve resolution. Enough to show a high-Q notch's shape without turning every
    /// redraw into thousands of points.</summary>
    public const int CurvePoints = 240;

    /// <summary>The ± dB scales the editor cycles through, smallest first.</summary>
    public static readonly int[] DbRanges = { 12, 24, 30 };

    public static double XFromFreq(double freq, double width) =>
        width * Math.Log(Math.Clamp(freq, FMin, FMax) / FMin) / Math.Log(FMax / FMin);

    public static double FreqFromX(double x, double width) =>
        FMin * Math.Exp(Math.Clamp(x, 0, width) / Math.Max(width, 1) * Math.Log(FMax / FMin));

    public static double YFromDb(double db, double height, double dbRange) =>
        height / 2 - db * height / (2.0 * dbRange);

    public static double DbFromY(double y, double height, double dbRange) =>
        (height / 2 - y) * 2.0 * dbRange / Math.Max(height, 1);

    /// <summary>The chain's summed response as plot points across the full frequency axis.</summary>
    public static PointCollection Curve(IEnumerable<EqBand> bands, double width, double height,
        double dbRange)
    {
        var freqs = EqResponse.LogFrequencies(CurvePoints, FMin, FMax);
        var response = EqResponse.ResponseDb(bands, freqs);
        var points = new PointCollection(freqs.Length);
        for (int i = 0; i < freqs.Length; i++)
            points.Add(new Point(XFromFreq(freqs[i], width), YFromDb(response[i], height, dbRange)));
        return points;
    }

    /// <summary>The smallest scale from <see cref="DbRanges"/> that shows the whole chain
    /// without clipping it against the top or bottom edge — so a preview of a gentle tilt isn't
    /// a flat line and a preview of a huge boost isn't cut off.</summary>
    public static int FittingDbRange(IReadOnlyList<EqBand> bands)
    {
        if (bands.Count == 0)
            return DbRanges[0];
        var response = EqResponse.ResponseDb(bands,
            EqResponse.LogFrequencies(CurvePoints, FMin, FMax));
        double peak = response.Max(Math.Abs);
        foreach (var range in DbRanges)
        {
            if (peak <= range * 0.95)
                return range;
        }
        return DbRanges[^1];
    }

    /// <summary>Draws a read-only preview into <paramref name="canvas"/>: decade grid lines, the
    /// 0 dB line, the summed curve and a corner scale label. Replaces whatever was there.</summary>
    public static void DrawPreview(Canvas canvas, IReadOnlyList<EqBand> bands, int dbRange)
    {
        canvas.Children.Clear();
        double width = canvas.ActualWidth, height = canvas.ActualHeight;
        if (width < 20 || height < 20)
            return;

        var gridBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x38));
        var zeroBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x58));
        var textBrush = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x78));

        foreach (var f in new double[] { 100, 1000, 10000 })
        {
            double x = XFromFreq(f, width);
            canvas.Children.Add(new Line
            {
                X1 = x, X2 = x, Y1 = 0, Y2 = height, Stroke = gridBrush, StrokeThickness = 1,
            });
        }
        double zeroY = YFromDb(0, height, dbRange);
        canvas.Children.Add(new Line
        {
            X1 = 0, X2 = width, Y1 = zeroY, Y2 = zeroY, Stroke = zeroBrush, StrokeThickness = 1.2,
        });

        if (bands.Count > 0)
        {
            canvas.Children.Add(new Polyline
            {
                Points = Curve(bands, width, height, dbRange),
                Stroke = new SolidColorBrush(Color.FromRgb(0x6F, 0xA8, 0xFF)),
                StrokeThickness = 2,
            });
        }

        var label = new TextBlock
        {
            Text = $"±{dbRange} dB · 20 Hz – 20 kHz",
            Foreground = textBrush,
            FontSize = 10,
        };
        Canvas.SetLeft(label, 4);
        Canvas.SetTop(label, 2);
        canvas.Children.Add(label);
    }
}
