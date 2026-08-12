namespace AorinEQ.Tests;

/// <summary>Names a facility of the MACHINE that a test needs in order to mean anything.
///
/// This repository does not mock (see any test file): the audio tests drive real endpoints through
/// real COM and the Equalizer APO tests read the real install, because a mock of either would only
/// prove the mock. That is the right trade at the desk and on the E2E machine, and it makes those
/// tests unrunnable on a GitHub-hosted Windows runner, which has no audio device and no Equalizer
/// APO - they do not fail there because something is broken, they fail because there is nothing to
/// talk to.
///
/// So they are labelled rather than weakened. CI excludes exactly these traits and says so in its
/// output; nothing is skipped conditionally at runtime, because a test that quietly skips itself
/// when the hardware goes missing stops being a test at the desk too. The full suite still runs
/// unfiltered locally and before every release.
///
/// Adding a trait here means adding it to the filter in .github/workflows/ci.yml.</summary>
public static class Requires
{
    public const string Key = "Requires";

    /// <summary>At least one active render endpoint (IMMDeviceEnumerator returns nothing without
    /// one, so these tests assert on an empty collection or a null device).</summary>
    public const string AudioEndpoint = "AudioEndpoint";

    /// <summary>Equalizer APO installed, i.e. HKLM\SOFTWARE\EqualizerAPO and its config
    /// directory.</summary>
    public const string EqualizerApo = "EqualizerApo";
}
