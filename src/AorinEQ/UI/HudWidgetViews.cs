using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AorinEQ.Core;

using ColorConverter = System.Windows.Media.ColorConverter;
// WPF and WinForms are both referenced by this project, so the names below are
// ambiguous without an explicit alias. Every one of these is the WPF type.
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using Brush = System.Windows.Media.Brush;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace AorinEQ.UI;

/// <summary>Everything a widget needs to draw one frame, gathered ONCE by the HUD and handed to
/// every widget. Passing a context rather than letting each view reach back into the app is what
/// keeps "one analysis a frame" true: the analysis in here was taken once, and four widgets
/// holding the same instance cannot each provoke another FFT.</summary>
/// <param name="Analysis">The shared reading, or null when nothing is capturing.</param>
/// <param name="Elapsed">Real time since the previous frame, for time-based ballistics.</param>
/// <param name="Percent">Active device volume.</param>
/// <param name="Muted">Active device mute.</param>
/// <param name="VolumeDb">The preamp dB the volume currently corresponds to, or null in system mode.</param>
/// <param name="DeviceName">Friendly name of the active render endpoint.</param>
/// <param name="EqBands">The active scope's band chain — global plus current device.</param>
internal sealed record HudFrame(
    AudioAnalysis? Analysis, TimeSpan Elapsed, int Percent, bool Muted,
    double? VolumeDb, string DeviceName, IReadOnlyList<EqBand> EqBands);

/// <summary>What every widget view can do. Implemented by a UIElement in each case — the window
/// hosts the view directly, so a view is a control and this is the contract on top of it.</summary>
internal interface IHudWidgetView
{
    /// <summary>Style knobs changed.</summary>
    void Apply(HudWidget widget);

    /// <summary>The Windows theme changed.</summary>
    void ApplyPalette(EqPalette palette);

    /// <summary>Draw one frame. Called at the HUD's shared cadence.</summary>
    void Render(HudFrame frame);

    /// <summary>Whether this view has anything new to draw. A widget whose data has not changed
    /// is skipped entirely rather than redrawn identically — that is most of the difference
    /// between four cheap widgets and four expensive ones.</summary>
    bool NeedsRedraw(HudFrame frame);
}

/// <summary>FFT bars over the shared spectrum.</summary>
internal sealed class HudSpectrumView : Canvas, IHudWidgetView
{
    private SpectrumBands _bands = new(32, 0.6, 24);
    private HudWidget _widget = HudWidget.Create(HudWidgetTypes.Spectrum);
    private EqPalette _palette = EqPalette.Dark;
    private readonly List<Rectangle> _bars = new();
    private readonly List<Rectangle> _peaks = new();
    private Color _start = Colors.DeepSkyBlue, _end = Colors.MediumPurple;
    private long _lastGeneration = -1;

    public HudSpectrumView()
    {
        ClipToBounds = true;
        SizeChanged += (_, _) => Layout();
    }

    public void Apply(HudWidget widget)
    {
        _widget = widget;
        _bands.Resize(widget.BandCount);
        _bands.Smoothing = widget.Smoothing;
        _bands.PeakDecayDbPerSecond = widget.PeakDecayDbPerSecond;
        _start = ParseColor(widget.ColorStart, Colors.DeepSkyBlue);
        _end = ParseColor(widget.ColorEnd, Colors.MediumPurple);
        Rebuild();
    }

    public void ApplyPalette(EqPalette palette) => _palette = palette;

    /// <summary>Always: a spectrum over live audio changes every frame, and over silence its
    /// smoothed bars are still falling. Redraw-skipping is for the widgets whose source really
    /// does sit still.</summary>
    public bool NeedsRedraw(HudFrame frame) => true;

    private void Rebuild()
    {
        Children.Clear();
        _bars.Clear();
        _peaks.Clear();
        for (int i = 0; i < _bands.Levels.Count; i++)
        {
            var bar = new Rectangle { RadiusX = 1, RadiusY = 1 };
            var peak = new Rectangle { Height = 2, Visibility = Visibility.Collapsed };
            _bars.Add(bar);
            _peaks.Add(peak);
            Children.Add(bar);
            Children.Add(peak);
        }
        Layout();
    }

    private void Layout()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || _bars.Count == 0) return;
        bool vertical = _widget.Orientation == HudOrientations.Vertical;
        double span = vertical ? ActualHeight : ActualWidth;
        double slot = span / _bars.Count;
        double thickness = Math.Max(1, slot - _widget.BarGap);
        for (int i = 0; i < _bars.Count; i++)
        {
            var brush = new SolidColorBrush(Ramp(i));
            brush.Freeze();
            _bars[i].Fill = brush;
            _peaks[i].Fill = brush;
            if (vertical)
            {
                _bars[i].Height = thickness;
                _peaks[i].Height = thickness;
                _peaks[i].Width = 2;
            }
            else
            {
                _bars[i].Width = thickness;
                _peaks[i].Width = thickness;
                _peaks[i].Height = 2;
            }
        }
    }

    private Color Ramp(int index)
    {
        if (_bars.Count <= 1) return _start;
        double t = index / (double)(_bars.Count - 1);
        return Color.FromArgb(
            (byte)(_start.A + (_end.A - _start.A) * t),
            (byte)(_start.R + (_end.R - _start.R) * t),
            (byte)(_start.G + (_end.G - _start.G) * t),
            (byte)(_start.B + (_end.B - _start.B) * t));
    }

    public void Render(HudFrame frame)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        if (frame.Analysis is { SampleRate: > 0 } a)
            _bands.Update(a.SpectrumDb, a.SampleRate, _widget.MinHz, _widget.MaxHz, frame.Elapsed);
        else
            // No capture: let the bars fall to nothing rather than freeze mid-height, which would
            // read as a hung widget.
            _bands.Update(Array.Empty<double>(), 0, _widget.MinHz, _widget.MaxHz, frame.Elapsed);

        bool vertical = _widget.Orientation == HudOrientations.Vertical;
        bool reversed = _widget.Orientation == HudOrientations.RightToLeft;
        bool mirrored = _widget.Orientation == HudOrientations.Mirrored;
        double span = vertical ? ActualHeight : ActualWidth;
        double depth = vertical ? ActualWidth : ActualHeight;
        double slot = span / _bars.Count;

        for (int i = 0; i < _bars.Count; i++)
        {
            int source = reversed ? _bars.Count - 1 - i : i;
            double level = _bands.LevelFraction(source);
            double size = Math.Max(0, level * (mirrored ? depth / 2 : depth));
            double along = i * slot;

            if (vertical)
            {
                _bars[i].Width = size;
                SetLeft(_bars[i], 0);
                SetTop(_bars[i], along);
            }
            else
            {
                _bars[i].Height = size;
                SetLeft(_bars[i], along);
                SetTop(_bars[i], mirrored ? depth / 2 - size : depth - size);
                if (mirrored) _bars[i].Height = size * 2;
            }

            if (_widget.PeakHold)
            {
                double peak = _bands.PeakFraction(source) * (mirrored ? depth / 2 : depth);
                _peaks[i].Visibility = Visibility.Visible;
                if (vertical)
                {
                    SetLeft(_peaks[i], Math.Min(depth - 2, peak));
                    SetTop(_peaks[i], along);
                }
                else
                {
                    SetLeft(_peaks[i], along);
                    SetTop(_peaks[i], Math.Max(0, (mirrored ? depth / 2 : depth) - peak - 2));
                }
            }
            else
            {
                _peaks[i].Visibility = Visibility.Collapsed;
            }
        }
        _lastGeneration++;
    }

    internal static Color ParseColor(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        try
        {
            return ColorConverter.ConvertFromString(value) is Color c ? c : fallback;
        }
        catch (FormatException) { return fallback; }
        catch (NotSupportedException) { return fallback; }
    }
}

/// <summary>Per-channel peak + RMS in dBFS with a latching clip indicator and a clipped count.
///
/// Because the capture is POST-EAPO, this shows clipping the user's own EQ gain is causing —
/// which is the single most "audio tool" thing in the widget set, and something no generic
/// desktop meter can know.</summary>
internal sealed class HudLevelsView : Grid, IHudWidgetView
{
    private readonly Rectangle _trackL = new(), _trackR = new();
    private readonly Rectangle _rmsL = new(), _rmsR = new();
    private readonly Rectangle _peakL = new(), _peakR = new();
    private readonly TextBlock _readout = new() { FontSize = 11, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
    private readonly Border _clipChip;
    private readonly TextBlock _clipText = new() { FontSize = 10, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
    private readonly HudClipLatch _latch = new();
    private readonly Canvas _meters = new();

    private EqPalette _palette = EqPalette.Dark;
    private double _shownRmsL = MeterMath.FloorDb, _shownRmsR = MeterMath.FloorDb;
    private double _shownPeakL = MeterMath.FloorDb, _shownPeakR = MeterMath.FloorDb;
    private bool _rebased;

    private const double BottomDb = -60, TopDb = 0;

    public HudLevelsView()
    {
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        foreach (var r in new[] { _trackL, _trackR, _rmsL, _rmsR, _peakL, _peakR })
            _meters.Children.Add(r);
        _peakL.Height = 2;
        _peakR.Height = 2;
        _meters.Margin = new Thickness(8, 8, 8, 4);
        SetRow(_meters, 0);
        Children.Add(_meters);

        _readout.Margin = new Thickness(4, 0, 4, 2);
        SetRow(_readout, 1);
        Children.Add(_readout);

        _clipChip = new Border
        {
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(8, 0, 8, 8),
            Padding = new Thickness(4, 2, 4, 2),
            Child = _clipText,
        };
        SetRow(_clipChip, 2);
        Children.Add(_clipChip);

        // Clicking the indicator resets it. Only reachable in edit mode — in live mode the window
        // is click-through, so this can never eat a desktop click.
        _clipChip.MouseLeftButtonUp += (_, e) =>
        {
            _latch.Reset(_lastClipEvents);
            e.Handled = true;
        };
        _meters.SizeChanged += (_, _) => LayoutMeters();
    }

    private int _lastClipEvents;

    public void Apply(HudWidget widget) { }

    public void ApplyPalette(EqPalette palette)
    {
        _palette = palette;
        var track = Freeze(palette.MeterTrack);
        _trackL.Fill = track;
        _trackR.Fill = track;
        _rmsL.Fill = Freeze(palette.MeterRms);
        _rmsR.Fill = Freeze(palette.MeterRms);
        _peakL.Fill = Freeze(palette.MeterPeak);
        _peakR.Fill = Freeze(palette.MeterPeak);
        _readout.Foreground = Freeze(palette.Text);
    }

    private static SolidColorBrush Freeze(System.Drawing.Color c)
    {
        var b = new SolidColorBrush(HudWidgetWindow.ToMediaColor(c));
        b.Freeze();
        return b;
    }

    /// <summary>Always: the meters have release ballistics, so a frame with no new audio is still
    /// a frame in which the bars have to fall.</summary>
    public bool NeedsRedraw(HudFrame frame) => true;

    private void LayoutMeters()
    {
        double w = _meters.ActualWidth, h = _meters.ActualHeight;
        if (w <= 0 || h <= 0) return;
        double barWidth = Math.Max(4, (w - 6) / 2);
        Place(_trackL, 0, 0, barWidth, h);
        Place(_trackR, barWidth + 6, 0, barWidth, h);
        _rmsL.Width = barWidth;
        _rmsR.Width = barWidth;
        _peakL.Width = barWidth;
        _peakR.Width = barWidth;
        Canvas.SetLeft(_rmsL, 0);
        Canvas.SetLeft(_peakL, 0);
        Canvas.SetLeft(_rmsR, barWidth + 6);
        Canvas.SetLeft(_peakR, barWidth + 6);
    }

    private static void Place(Rectangle r, double x, double y, double w, double h)
    {
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        r.Width = w;
        r.Height = h;
    }

    public void Render(HudFrame frame)
    {
        double peakL = MeterMath.FloorDb, peakR = MeterMath.FloorDb;
        double rmsL = MeterMath.FloorDb, rmsR = MeterMath.FloorDb;
        if (frame.Analysis is { } a)
        {
            peakL = a.PeakDbL; peakR = a.PeakDbR;
            rmsL = a.RmsDbL; rmsR = a.RmsDbR;
            _lastClipEvents = a.ClipEvents;
            // A widget shown after an hour of clipping must not come up already lit.
            if (!_rebased) { _latch.Rebase(a.ClipEvents); _rebased = true; }
            else _latch.Observe(a.ClipEvents);
        }

        // Instant attack, smooth release — the same ballistic the EQ editor's meters use.
        double fall = frame.Elapsed.TotalSeconds;
        _shownRmsL = Math.Max(rmsL, _shownRmsL - 75 * fall);
        _shownRmsR = Math.Max(rmsR, _shownRmsR - 75 * fall);
        _shownPeakL = Math.Max(peakL, _shownPeakL - 45 * fall);
        _shownPeakR = Math.Max(peakR, _shownPeakR - 45 * fall);

        double h = _meters.ActualHeight;
        if (h > 0)
        {
            DrawBar(_rmsL, _peakL, _shownRmsL, _shownPeakL, h);
            DrawBar(_rmsR, _peakR, _shownRmsR, _shownPeakR, h);
        }

        _readout.Text = _shownPeakL <= MeterMath.FloorDb && _shownPeakR <= MeterMath.FloorDb
            ? "silent"
            : string.Format(CultureInfo.CurrentCulture, "{0:0.0} dBFS",
                Math.Max(_shownPeakL, _shownPeakR));

        bool latched = _latch.Latched;
        _clipChip.Background = Freeze(latched ? _palette.ClipLatched : _palette.ClipIdle);
        _clipText.Foreground = Freeze(latched ? _palette.ClipLatchedText : _palette.ClipIdleText);
        _clipText.Text = latched ? $"CLIP ×{_latch.Count}" : "no clipping";
    }

    private static void DrawBar(Rectangle rms, Rectangle peak, double rmsDb, double peakDb, double height)
    {
        double Scale(double db) => height * SpectrumBands.Normalize(db, BottomDb, TopDb);
        double rh = Scale(rmsDb);
        rms.Height = rh;
        Canvas.SetTop(rms, height - rh);
        Canvas.SetTop(peak, Math.Max(0, height - Scale(peakDb) - 2));
    }
}

/// <summary>The live response of the ACTIVE scope, drawn with the same RBJ maths the editor uses —
/// <see cref="EqResponse"/>, not a second implementation of it.</summary>
internal sealed class HudEqCurveView : Canvas, IHudWidgetView
{
    private const double FMin = EqResponse.MinFrequency, FMax = EqResponse.MaxFrequency;
    private const double TopDb = 15, BottomDb = -15;

    private readonly Polyline _curve = new() { StrokeThickness = 1.6 };
    private readonly List<UIElement> _grid = new();
    private readonly List<Ellipse> _nodes = new();
    private EqPalette _palette = EqPalette.Dark;
    private HudWidget _widget = HudWidget.Create(HudWidgetTypes.EqCurve);
    private string _lastSignature = "";
    private double _lastWidth, _lastHeight;

    public HudEqCurveView()
    {
        ClipToBounds = true;
        Children.Add(_curve);
        SizeChanged += (_, _) => _lastSignature = ""; // a resize invalidates the drawn geometry
    }

    public void Apply(HudWidget widget)
    {
        _widget = widget;
        _lastSignature = "";
    }

    public void ApplyPalette(EqPalette palette)
    {
        _palette = palette;
        _curve.Stroke = new SolidColorBrush(HudWidgetWindow.ToMediaColor(palette.Curve));
        _lastSignature = "";
    }

    /// <summary>Only when the chain (or the box) actually changed. An EQ curve over an unedited
    /// preset is the same picture in every frame, and redrawing it 30 times a second for nothing
    /// is exactly the cost the shared pipeline exists to avoid paying four times over.</summary>
    public bool NeedsRedraw(HudFrame frame) =>
        Signature(frame.EqBands) != _lastSignature
        || ActualWidth != _lastWidth || ActualHeight != _lastHeight;

    private static string Signature(IReadOnlyList<EqBand> bands) =>
        string.Join(";", bands.Select(b =>
            string.Create(CultureInfo.InvariantCulture, $"{b.Type}:{b.Fc}:{b.GainDb}:{b.Q}")));

    public void Render(HudFrame frame)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 4 || h <= 4) return;
        _lastSignature = Signature(frame.EqBands);
        _lastWidth = w;
        _lastHeight = h;

        foreach (var e in _grid) Children.Remove(e);
        _grid.Clear();
        foreach (var n in _nodes) Children.Remove(n);
        _nodes.Clear();

        if (_widget.ShowGrid)
        {
            var gridBrush = new SolidColorBrush(HudWidgetWindow.ToMediaColor(_palette.Grid));
            gridBrush.Freeze();
            foreach (double f in new[] { 100.0, 1000.0, 10000.0 })
                AddLine(XFor(f, w), 0, XFor(f, w), h, gridBrush, 1);
            var zero = new SolidColorBrush(HudWidgetWindow.ToMediaColor(_palette.ZeroLine));
            zero.Freeze();
            AddLine(0, YFor(0, h), w, YFor(0, h), zero, 1);
        }

        int samples = Math.Max(48, (int)(w / 2));
        var freqs = EqResponse.LogFrequencies(samples, FMin, FMax);
        var response = EqResponse.ResponseDb(frame.EqBands, freqs);
        var points = new PointCollection(samples);
        for (int i = 0; i < samples; i++)
            points.Add(new System.Windows.Point(w * i / (samples - 1.0), YFor(response[i], h)));
        _curve.Points = points;
        SetZIndex(_curve, 10);

        if (_widget.ShowNodes)
        {
            var fill = new SolidColorBrush(HudWidgetWindow.ToMediaColor(_palette.Node));
            fill.Freeze();
            foreach (var band in frame.EqBands)
            {
                if (band.Fc < FMin || band.Fc > FMax) continue;
                var dot = new Ellipse { Width = 6, Height = 6, Fill = fill };
                SetLeft(dot, XFor(band.Fc, w) - 3);
                SetTop(dot, YFor(band.GainDb, h) - 3);
                SetZIndex(dot, 20);
                _nodes.Add(dot);
                Children.Add(dot);
            }
        }
    }

    private void AddLine(double x1, double y1, double x2, double y2, Brush brush, double thickness)
    {
        var line = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = brush, StrokeThickness = thickness };
        _grid.Add(line);
        Children.Add(line);
    }

    private static double XFor(double freq, double width) =>
        width * Math.Log(Math.Clamp(freq, FMin, FMax) / FMin) / Math.Log(FMax / FMin);

    private static double YFor(double db, double height) =>
        height * (1 - SpectrumBands.Normalize(db, BottomDb, TopDb));
}
