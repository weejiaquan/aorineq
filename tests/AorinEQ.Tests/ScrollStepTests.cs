using AorinEQ.Core;

namespace AorinEQ.Tests;

/// <summary>The pure half of scroll-to-adjust: turning a stream of raw WM_MOUSEWHEEL deltas into
/// volume steps. Everything here is arithmetic with no Win32 in it, which is the point — the hook
/// that feeds it cannot be unit-tested, so all the behaviour that can go wrong lives on this side
/// of the line.
///
/// The case that motivates the class at all is the high-resolution wheel: precision touchpads and
/// several mice send deltas well under WHEEL_DELTA, and code that treats every message as one
/// notch sends the volume to 100 in a flick.</summary>
public class ScrollStepTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public ScrollStepTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private const int Notch = ScrollStep.WheelDelta;

    [Fact]
    public void OneNotchUpMovesOneConfiguredStep()
    {
        var s = new ScrollStep();
        var moved = s.Feed(Notch, stepPercent: 2, inverted: false);
        _out.WriteLine($"delta=+{Notch} step=2 -> {moved:+#;-#;0}");
        Assert.Equal(2, moved);
    }

    [Fact]
    public void OneNotchDownMovesOneConfiguredStepDown()
    {
        var s = new ScrollStep();
        var moved = s.Feed(-Notch, stepPercent: 5, inverted: false);
        _out.WriteLine($"delta=-{Notch} step=5 -> {moved:+#;-#;0}");
        Assert.Equal(-5, moved);
    }

    /// <summary>A high-resolution wheel's partial delta must move nothing on its own, or a
    /// touchpad flick becomes a volume jump.</summary>
    [Fact]
    public void APartialDeltaMovesNothingYet()
    {
        var s = new ScrollStep();
        _out.WriteLine($"delta=+40 -> {s.Feed(40, 2, false)}");
        Assert.Equal(0, s.Feed(40, stepPercent: 2, inverted: false));
    }

    [Fact]
    public void PartialDeltasAccumulateIntoOneNotch()
    {
        var s = new ScrollStep();
        Assert.Equal(0, s.Feed(40, 2, false));
        Assert.Equal(0, s.Feed(40, 2, false));
        var moved = s.Feed(40, 2, false);
        _out.WriteLine($"40+40+40 = one notch -> {moved:+#;-#;0}");
        Assert.Equal(2, moved);
    }

    /// <summary>Having fired, the accumulator must be empty again — otherwise the fourth 40 would
    /// fire a second notch after only a third of a wheel turn.</summary>
    [Fact]
    public void TheAccumulatorEmptiesAfterFiring()
    {
        var s = new ScrollStep();
        s.Feed(Notch, 2, false);
        _out.WriteLine($"next partial after a full notch -> {s.Feed(40, 2, false)}");
        Assert.Equal(0, s.Feed(40, 2, false));
    }

    /// <summary>A fast wheel coalesces several notches into one message.</summary>
    [Fact]
    public void SeveralNotchesInOneMessageMoveSeveralSteps()
    {
        var s = new ScrollStep();
        var moved = s.Feed(Notch * 3, stepPercent: 2, inverted: false);
        _out.WriteLine($"delta={Notch * 3} step=2 -> {moved:+#;-#;0}");
        Assert.Equal(6, moved);
    }

    /// <summary>The leftover past a whole notch is kept, so a stream of odd-sized deltas neither
    /// loses movement nor drifts.</summary>
    [Fact]
    public void TheRemainderPastAWholeNotchIsCarried()
    {
        var s = new ScrollStep();
        Assert.Equal(2, s.Feed(Notch + 80, stepPercent: 2, inverted: false)); // one notch, 80 left
        var moved = s.Feed(40, stepPercent: 2, inverted: false);              // 80 + 40 = the second
        _out.WriteLine($"carried remainder fired -> {moved:+#;-#;0}");
        Assert.Equal(2, moved);
    }

    /// <summary>Reversing must respond at once. If a pending +80 were merely added to, a full
    /// notch down would net to -40 and do nothing — the user would have to scroll down twice to
    /// undo one scroll up.</summary>
    [Fact]
    public void ReversingDirectionDiscardsThePendingPartial()
    {
        var s = new ScrollStep();
        Assert.Equal(0, s.Feed(80, 2, false));
        var moved = s.Feed(-Notch, stepPercent: 2, inverted: false);
        _out.WriteLine($"+80 then -{Notch} -> {moved:+#;-#;0}");
        Assert.Equal(-2, moved);
    }

    [Fact]
    public void AZeroDeltaMovesNothingAndKeepsThePending()
    {
        var s = new ScrollStep();
        s.Feed(80, 2, false);
        Assert.Equal(0, s.Feed(0, 2, false));
        _out.WriteLine("a zero delta left the pending 80 intact");
        Assert.Equal(2, s.Feed(40, 2, false));
    }

    [Fact]
    public void InvertingFlipsTheDirection()
    {
        var s = new ScrollStep();
        var moved = s.Feed(Notch, stepPercent: 2, inverted: true);
        _out.WriteLine($"delta=+{Notch} inverted -> {moved:+#;-#;0}");
        Assert.Equal(-2, moved);
    }

    /// <summary>Inversion is applied to the RESULT, not to the incoming delta: inverting must not
    /// make a steady scroll look like a direction reversal and throw the accumulator away.</summary>
    [Fact]
    public void InvertingStillAccumulatesPartials()
    {
        var s = new ScrollStep();
        Assert.Equal(0, s.Feed(60, 2, inverted: true));
        var moved = s.Feed(60, 2, inverted: true);
        _out.WriteLine($"60+60 inverted -> {moved:+#;-#;0}");
        Assert.Equal(-2, moved);
    }

    [Fact]
    public void NoModifierUsesTheConfiguredStep()
    {
        _out.WriteLine($"StepFor(none, configured=5) = {ScrollStep.StepFor(false, false, 5)}");
        Assert.Equal(5, ScrollStep.StepFor(ctrl: false, shift: false, configuredStepPercent: 5));
    }

    [Fact]
    public void CtrlGivesAFineStep()
    {
        Assert.Equal(ScrollStep.FineStepPercent, ScrollStep.StepFor(ctrl: true, shift: false, configuredStepPercent: 5));
        Assert.Equal(1, ScrollStep.FineStepPercent);
    }

    [Fact]
    public void ShiftGivesACoarseStep()
    {
        Assert.Equal(ScrollStep.CoarseStepPercent, ScrollStep.StepFor(ctrl: false, shift: true, configuredStepPercent: 2));
        Assert.Equal(10, ScrollStep.CoarseStepPercent);
    }

    /// <summary>Both held is an accident far more often than an intent, so it resolves to the
    /// step that can do least damage to someone's ears.</summary>
    [Fact]
    public void CtrlWinsOverShiftWhenBothAreHeld()
    {
        _out.WriteLine($"StepFor(ctrl+shift, configured=2) = {ScrollStep.StepFor(true, true, 2)}");
        Assert.Equal(ScrollStep.FineStepPercent, ScrollStep.StepFor(ctrl: true, shift: true, configuredStepPercent: 2));
    }

    /// <summary>Windows moves its own volume 2% per notch and does NOT scale by the system's
    /// "lines to scroll per notch" (SPI_GETWHEELSCROLLLINES, 3 by default) — volume is not lines.
    /// This pins that: one notch is one step, whatever the mouse control panel says.</summary>
    [Fact]
    public void OneNotchIsOneStepRegardlessOfTheSystemScrollLinesSetting()
    {
        var s = new ScrollStep();
        var moved = s.Feed(Notch, stepPercent: 2, inverted: false);
        _out.WriteLine($"one notch at the Windows-default step -> {moved:+#;-#;0}%");
        Assert.Equal(2, moved);
    }
}
