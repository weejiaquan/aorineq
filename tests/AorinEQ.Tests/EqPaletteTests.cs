using System.Drawing;
using AorinEQ.Core;

namespace AorinEQ.Tests;

/// <summary>The equalizer editor's plot, meters and spectrum are custom-drawn, so nothing in the
/// Fluent theme system can make them legible for us. Until v3.1.0 they were tuned for dark only;
/// these tests are what keeps the light palette honest, because "unreadable in light mode" is
/// precisely the defect that survives a glance at a screenshot taken in dark mode.
///
/// Contrast is measured the WCAG way (linearised channels, translucent colours composited over the
/// surface they are drawn on) rather than by comparing raw bytes, which gets saturated colours like
/// the meter green wrong.</summary>
public class EqPaletteTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public EqPaletteTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    public static TheoryData<string, EqPalette> BothPalettes() =>
        new() { { "dark", EqPalette.Dark }, { "light", EqPalette.Light } };

    [Fact]
    public void TheThemeChoosesThePalette()
    {
        Assert.Same(EqPalette.Light, EqPalette.For(light: true));
        Assert.Same(EqPalette.Dark, EqPalette.For(light: false));
    }

    /// <summary>The two palettes must genuinely differ — a copy-paste that left the light plot
    /// near-black would pass every contrast check below (dark text on a dark plot fails, but light
    /// text on it passes) while being exactly the bug this release exists to fix.</summary>
    [Fact]
    public void TheLightPlotSurfaceIsActuallyLightAndTheDarkOneDark()
    {
        double light = EqPalette.RelativeLuminance(EqPalette.Light.PlotBackground);
        double dark = EqPalette.RelativeLuminance(EqPalette.Dark.PlotBackground);
        _out.WriteLine($"plot luminance: light={light:F4} dark={dark:F4}");

        Assert.True(light > 0.7, $"light plot surface is not light (luminance {light:F4})");
        Assert.True(dark < 0.05, $"dark plot surface is not dark (luminance {dark:F4})");
    }

    /// <summary>Text drawn on the plot and panels must clear WCAG AA for normal text (4.5:1). The
    /// axis labels are small and the dimmest thing on the surface, so they are the real test.</summary>
    [Theory]
    [MemberData(nameof(BothPalettes))]
    public void TextIsReadableOnTheSurfaceItIsDrawnOn(string name, EqPalette p)
    {
        var checks = new (string What, Color Fg, Color Bg)[]
        {
            ("axis text on plot", p.AxisText, p.PlotBackground),
            ("text on panel", p.Text, p.PanelBackground),
            ("dim text on panel", p.TextDim, p.PanelBackground),
            ("clip idle text", p.ClipIdleText, p.ClipIdle),
            ("clip latched text", p.ClipLatchedText, p.ClipLatched),
        };

        foreach (var (what, fg, bg) in checks)
        {
            double ratio = EqPalette.ContrastRatio(fg, bg);
            _out.WriteLine($"{name}: {what} = {ratio:F2}:1");
            Assert.True(ratio >= 4.5, $"{name}: {what} is only {ratio:F2}:1, needs 4.5:1");
        }
    }

    /// <summary>The FOREGROUND graphics — the response curve, the draggable band nodes and the
    /// meter bars themselves (measured against the track they fill, which is what a reader
    /// actually compares them to). These are what the user reads a value off and grabs with the mouse, so
    /// WCAG's 3:1 non-text threshold applies in full. (The layers drawn behind them — grid,
    /// spectrum, band shading, 0 dB line — are context and are ranked separately below; holding
    /// them to 3:1 too would mean a backdrop as loud as the data on top of it.)</summary>
    [Theory]
    [MemberData(nameof(BothPalettes))]
    public void EveryDrawnElementStandsOutFromItsSurface(string name, EqPalette p)
    {
        var checks = new (string What, Color Fg, Color Bg)[]
        {
            ("curve on plot", p.Curve, p.PlotBackground),
            ("node on plot", p.Node, p.PlotBackground),
            ("selected node on plot", p.NodeSelected, p.PlotBackground),
            ("meter rms on track", p.MeterRms, p.MeterTrack),
            ("meter peak on track", p.MeterPeak, p.MeterTrack),
        };

        foreach (var (what, fg, bg) in checks)
        {
            double ratio = EqPalette.ContrastRatio(fg, bg);
            _out.WriteLine($"{name}: {what} = {ratio:F2}:1");
            Assert.True(ratio >= 3.0, $"{name}: {what} is only {ratio:F2}:1, needs 3.0:1");
        }
    }

    /// <summary>The grid, the spectrum trace and the 0 dB line are all drawn BEHIND the response
    /// curve — context, not the data the user is editing, so WCAG's 3:1 for meaningful graphics is the wrong bar — a grid loud enough to
    /// clear it competes with the curve it exists to measure. They are held to a legibility
    /// ordering instead: both clearly separated from the surface, the 0 dB line clearly louder than
    /// the decade grid, and both quieter than the curve. That ordering is what a light palette
    /// copied from a dark one destroys, which is the failure these guard.</summary>
    [Theory]
    [MemberData(nameof(BothPalettes))]
    public void ReferenceMarksAreVisibleAndCorrectlyRanked(string name, EqPalette p)
    {
        double grid = EqPalette.ContrastRatio(p.Grid, p.PlotBackground);
        double spectrum = EqPalette.ContrastRatio(p.Spectrum, p.PlotBackground);
        double band = EqPalette.ContrastRatio(p.BandFill, p.PlotBackground);
        double bandSel = EqPalette.ContrastRatio(p.BandSelectedFill, p.PlotBackground);
        double zero = EqPalette.ContrastRatio(p.ZeroLine, p.PlotBackground);
        double curve = EqPalette.ContrastRatio(p.Curve, p.PlotBackground);
        _out.WriteLine($"{name}: grid = {grid:F2}:1, spectrum = {spectrum:F2}:1, "
            + $"band fill = {band:F2}:1, selected band fill = {bandSel:F2}:1, "
            + $"zero line = {zero:F2}:1, curve = {curve:F2}:1");

        Assert.True(grid >= 1.25, $"{name}: grid is only {grid:F2}:1 — invisible against the plot");
        Assert.True(spectrum >= 1.4, $"{name}: spectrum is only {spectrum:F2}:1 against the plot");
        Assert.True(band >= 1.4, $"{name}: band shading is only {band:F2}:1 against the plot");
        // The meter track is an empty trough — a container for the bar, not a reading. It only has
        // to read as recessed; what must be legible is the BAR inside it, which is held to 3:1
        // against this track above. A trough on a dark panel is inherently low-contrast and every
        // meter in every audio tool looks like that.
        double track = EqPalette.ContrastRatio(p.MeterTrack, p.PanelBackground);
        _out.WriteLine($"{name}: meter track on panel = {track:F2}:1");
        Assert.True(track >= 1.08, $"{name}: meter track is only {track:F2}:1 — invisible on the panel");
        Assert.True(bandSel >= 1.4, $"{name}: selected band shading is only {bandSel:F2}:1 against the plot");
        Assert.True(zero >= 2.0, $"{name}: the 0 dB line is only {zero:F2}:1 against the plot");
        Assert.True(zero > grid, $"{name}: the 0 dB line ({zero:F2}) is not louder than the grid ({grid:F2})");
        Assert.True(curve > zero, $"{name}: the curve ({curve:F2}) does not stand out from the 0 dB line ({zero:F2})");
        Assert.True(curve > spectrum, $"{name}: the curve ({curve:F2}) does not stand out from the spectrum ({spectrum:F2})");
        Assert.True(curve > band, $"{name}: the curve ({curve:F2}) does not stand out from the band shading ({band:F2})");
    }

    /// <summary>A selected band node must be tellable from an unselected one — they carry the
    /// editor's only indication of which band the side panel is editing. The grid line is the one
    /// element deliberately allowed to be quiet, so it is checked against the surface (above) and
    /// not against the curve.</summary>
    [Theory]
    [MemberData(nameof(BothPalettes))]
    public void SelectionIsDistinguishableFromTheRestOfThePlot(string name, EqPalette p)
    {
        // Distance, not contrast: these are told apart by HUE (blue vs amber), and a lightness
        // ratio scores that pair at barely 1.5:1 while they are unmistakable on screen.
        double node = EqPalette.Distance(p.NodeSelected, p.Node);
        double fill = EqPalette.Distance(
            EqPalette.Flatten(p.BandSelectedFill, p.PlotBackground),
            EqPalette.Flatten(p.BandFill, p.PlotBackground));
        _out.WriteLine($"{name}: selected vs normal node distance = {node:F0}, band fill = {fill:F0}");

        Assert.True(node >= 150, $"{name}: selected node only {node:F0} from a normal one");
        Assert.True(fill >= 80, $"{name}: selected band fill only {fill:F0} from a normal one");
    }

    /// <summary>The node outline exists so a node stays visible where it sits ON the curve. If it
    /// matches the curve colour it does nothing. A hairline separator does not need the 3:1 that
    /// meaningful graphics do — it only has to read as an edge — so 2:1 is the bar.</summary>
    [Theory]
    [MemberData(nameof(BothPalettes))]
    public void NodeOutlineSeparatesNodesFromTheCurve(string name, EqPalette p)
    {
        double ratio = EqPalette.ContrastRatio(p.NodeStroke, p.Curve);
        _out.WriteLine($"{name}: node outline vs curve = {ratio:F2}:1");
        Assert.True(ratio >= 2.0, $"{name}: node outline only {ratio:F2}:1 against the curve");
    }

    /// <summary>Latched clipping is an alarm — it has to be obvious against the idle state.</summary>
    [Theory]
    [MemberData(nameof(BothPalettes))]
    public void ClipAlarmIsObviousAgainstItsIdleState(string name, EqPalette p)
    {
        double ratio = EqPalette.ContrastRatio(p.ClipLatched, p.ClipIdle);
        _out.WriteLine($"{name}: clip latched vs idle = {ratio:F2}:1");
        Assert.True(ratio >= 1.8, $"{name}: clip alarm only {ratio:F2}:1 against idle");
    }

    /// <summary>Alpha compositing has to be right for the translucent band fills to be measured
    /// as they are seen. Pinned with a worked example rather than trusted.</summary>
    [Fact]
    public void TranslucentColoursAreCompositedBeforeBeingMeasured()
    {
        var half = Color.FromArgb(128, 255, 255, 255);
        var onBlack = EqPalette.Flatten(half, Color.FromArgb(255, 0, 0, 0));
        _out.WriteLine($"50% white over black -> {onBlack}");

        Assert.Equal(255, onBlack.A);
        Assert.Equal(128, onBlack.R);
        Assert.Equal(128, onBlack.G);
        Assert.Equal(128, onBlack.B);

        // Fully opaque colours pass through untouched.
        var opaque = Color.FromArgb(255, 10, 20, 30);
        Assert.Equal(opaque, EqPalette.Flatten(opaque, Color.FromArgb(255, 200, 200, 200)));
    }

    /// <summary>Black on white is WCAG's 21:1 ceiling; identical colours are 1:1. Pins the formula
    /// so a refactor of it cannot quietly relax every threshold above.</summary>
    [Fact]
    public void ContrastRatioMatchesTheWcagEndpoints()
    {
        var black = Color.FromArgb(255, 0, 0, 0);
        var white = Color.FromArgb(255, 255, 255, 255);
        _out.WriteLine($"black/white = {EqPalette.ContrastRatio(black, white):F2}");

        Assert.Equal(21.0, EqPalette.ContrastRatio(black, white), 2);
        Assert.Equal(21.0, EqPalette.ContrastRatio(white, black), 2);
        Assert.Equal(1.0, EqPalette.ContrastRatio(white, white), 3);
    }
}
