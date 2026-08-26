using AorinEQ.Core;
using Xunit;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>The OSD hides itself with a fade-out whose Completed handler calls Hide(). Pressing a
/// volume key during that fade is supposed to rescue the window: cancel the animation, restore
/// opacity, show it again. It did not work, and the reason is a WPF behaviour that is easy to
/// assume away — BeginAnimation(property, null) removes the animation but its Completed handler
/// STILL FIRES. Measured, in a real WPF process, against the exact sequence the window runs:
///
///     Completed fired after cancellation : True
///     window hidden right after Show()   : True
///
/// So the rescued OSD was shown and then hidden again a few milliseconds later by the handler of
/// the fade it had just cancelled. The user-visible symptom is a volume key that sometimes shows
/// no OSD at all: it needs the press to land inside the 150 ms fade, which happens when you nudge
/// the volume, pause about a second and a half, and nudge it again.
///
/// This is the policy that fixes it, and it lives in Core because BOTH OsdWindow and
/// SkinOsdWindow fade out this way and neither may drift from the other.</summary>
public class OsdFadeTests
{
    private readonly ITestOutputHelper _out;

    public OsdFadeTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void A_fade_that_runs_to_completion_hides_the_window()
    {
        var fade = new OsdFade();

        var token = fade.Begin();

        Assert.True(fade.MayHide(token));
    }

    [Fact]
    public void A_cancelled_fade_does_not_hide_the_window()
    {
        // THE BUG. ShowVolume cancels the fade and re-shows the window; the cancelled animation's
        // Completed then fires anyway and must now find that it is no longer allowed to hide.
        var fade = new OsdFade();
        var token = fade.Begin();

        fade.Cancel();

        Assert.False(fade.MayHide(token));
        _out.WriteLine("cancelled fade refused permission to hide - the OSD stays up");
    }

    [Fact]
    public void A_fade_superseded_by_a_newer_one_does_not_hide_the_window()
    {
        // Cancel/Begin in quick succession: the volume key rescues the OSD, and 1.5s later the
        // hide timer starts a fresh fade. The FIRST fade's Completed may still be pending.
        var fade = new OsdFade();
        var stale = fade.Begin();
        fade.Cancel();

        var current = fade.Begin();

        Assert.False(fade.MayHide(stale));
        Assert.True(fade.MayHide(current));
    }

    [Fact]
    public void Only_the_newest_fade_may_hide_even_without_a_cancel_between_them()
    {
        var fade = new OsdFade();

        var first = fade.Begin();
        var second = fade.Begin();

        Assert.False(fade.MayHide(first));
        Assert.True(fade.MayHide(second));
    }

    [Fact]
    public void Cancelling_when_no_fade_is_running_is_harmless()
    {
        // MouseEnter cancels unconditionally — it cannot know whether a fade is running, and the
        // window is usually just sitting there fully opaque.
        var fade = new OsdFade();

        fade.Cancel();
        var token = fade.Begin();

        Assert.True(fade.MayHide(token));
    }

    [Fact]
    public void A_token_from_before_any_fade_began_may_never_hide()
    {
        // default(int) must not be mistaken for a live fade, or a stray Completed would hide the
        // window on the strength of an uninitialised field.
        var fade = new OsdFade();

        Assert.False(fade.MayHide(0));
        _out.WriteLine("token 0 is never valid, so an uninitialised field cannot hide the OSD");
    }

    [Fact]
    public void Tokens_are_not_reused_across_many_fades()
    {
        // A recycled token would let an old Completed match a much later fade. Volume keys and
        // hide timers cycle this constantly over a session that runs for days.
        var fade = new OsdFade();
        var seen = new HashSet<int>();

        for (var i = 0; i < 1000; i++)
        {
            var token = fade.Begin();
            Assert.True(seen.Add(token), $"token {token} was handed out twice (iteration {i})");
            fade.Cancel();
        }

        Assert.Equal(1000, seen.Count);
    }
}
