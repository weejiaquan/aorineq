namespace AorinEQ.Core;

/// <summary>Decides which fade-out is still allowed to hide the OSD.
///
/// Both OSD windows hide themselves by fading opacity to zero and calling Hide() from the
/// animation's Completed handler, and both rescue a fading window — a volume key arriving through
/// ShowVolume, or the pointer arriving through MouseEnter — by cancelling that animation with
/// <c>BeginAnimation(OpacityProperty, null)</c>.
///
/// Cancelling an animation that way does NOT cancel its Completed event. WPF raises it anyway,
/// and the handler then hid the window that had just been rescued. Measured in a real WPF process
/// against that exact sequence: Completed fired, Hide() ran, and IsVisible was false a few
/// milliseconds after Show(). The symptom was a volume key that showed no OSD at all — but only
/// when the press landed inside the fade, so it looked random.
///
/// Each fade takes a token when it starts, and only the newest uncancelled fade's token may hide.
/// A cancelled or superseded fade's Completed still fires, finds its token stale, and does
/// nothing. This is the same monotonic-token idiom App uses to discard superseded async results.
///
/// Not thread-safe, and deliberately so: every caller is on the dispatcher thread, which is the
/// only thread that may touch a Window at all.</summary>
public sealed class OsdFade
{
    /// <summary>Never handed out by <see cref="Begin"/>, so an uninitialised token cannot hide the
    /// window and <see cref="Cancel"/> has a value meaning "no fade may hide".</summary>
    private const int NoFade = 0;

    private int _lastIssued = NoFade;
    private int _mayHide = NoFade;

    /// <summary>Starts a fade and returns the token its Completed handler must present. Supersedes
    /// any fade already running.</summary>
    public int Begin() => _mayHide = ++_lastIssued;

    /// <summary>Withdraws permission from whatever fade is running. Harmless when there is none —
    /// MouseEnter cancels unconditionally, because it cannot know.</summary>
    public void Cancel() => _mayHide = NoFade;

    /// <summary>Whether the fade holding <paramref name="token"/> may hide the window. False for a
    /// fade that was cancelled or superseded, and for a token that never came from
    /// <see cref="Begin"/>.</summary>
    public bool MayHide(int token) => token != NoFade && token == _mayHide;
}
