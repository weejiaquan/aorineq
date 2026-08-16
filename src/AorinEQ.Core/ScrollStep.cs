namespace AorinEQ.Core;

/// <summary>Turns a stream of raw mouse-wheel deltas into volume steps.
///
/// This is the whole of scroll-to-adjust that can be tested: the WH_MOUSE_LL hook that feeds it
/// and the tray hit-test that gates it are Win32 and cannot be, so every rule that could be got
/// wrong lives here instead. Three of them matter:
///
/// A notch is <see cref="WheelDelta"/>, and a message is NOT a notch. High-resolution wheels —
/// precision touchpads, and plenty of ordinary mice — send fractions of one, so the deltas are
/// accumulated and only whole notches fire; anything less is carried to the next message. Without
/// that, one touchpad flick walks the volume to 100.
///
/// Reversing direction throws the carried partial away rather than netting against it. A pending
/// +80 followed by a full notch down would otherwise sum to -40 and do nothing, so undoing one
/// scroll up would take two scrolls down.
///
/// One notch is one step — it is deliberately NOT multiplied by the system's lines-per-notch
/// setting (SPI_GETWHEELSCROLLLINES, 3 by default), because volume is not lines. Windows' own
/// volume icon ignores it too, and moves 2% per notch, which is this app's default
/// <see cref="Settings.StepPercent"/>.
///
/// Not thread-safe, and does not need to be: the hook marshals every delta onto the dispatcher
/// before it reaches here, the same way <c>KeyboardHook</c> marshals keys.</summary>
public sealed class ScrollStep
{
    /// <summary>WHEEL_DELTA — the delta one detent of a conventional wheel reports.</summary>
    public const int WheelDelta = 120;

    /// <summary>The step while Ctrl is held: the finest the app can move at all.</summary>
    public const int FineStepPercent = 1;

    /// <summary>The step while Shift is held.</summary>
    public const int CoarseStepPercent = 10;

    private int _pending;

    /// <summary>The step one notch should move, given the modifiers held when it arrived. Ctrl
    /// wins over Shift when both are down: both held is an accident far more often than an
    /// intent, so it resolves to the step that can do least damage to someone's ears.</summary>
    public static int StepFor(bool ctrl, bool shift, int configuredStepPercent) =>
        ctrl ? FineStepPercent
        : shift ? CoarseStepPercent
        : configuredStepPercent;

    /// <summary>Feeds one raw wheel delta and returns the signed percent to apply — 0 when the
    /// delta has not yet added up to a whole notch, which is the common case on a
    /// high-resolution wheel.
    ///
    /// <paramref name="inverted"/> flips the RESULT rather than the incoming delta, deliberately:
    /// inverting the delta would make every message of a steady scroll look like a reversal of
    /// the one before it and throw the carried partial away every time.</summary>
    public int Feed(int rawDelta, int stepPercent, bool inverted)
    {
        if (rawDelta == 0) return 0;

        // A reversal is a new gesture, not a continuation of the pending one.
        if (Math.Sign(rawDelta) != Math.Sign(_pending)) _pending = 0;

        _pending += rawDelta;
        int notches = _pending / WheelDelta; // truncates toward zero, so it is signed correctly
        if (notches == 0) return 0;

        _pending -= notches * WheelDelta;
        int moved = notches * stepPercent;
        return inverted ? -moved : moved;
    }
}
