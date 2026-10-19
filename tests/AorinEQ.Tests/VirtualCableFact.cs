using AorinEQ.Core;
using Xunit;

namespace AorinEQ.Tests;

/// <summary>A test that only means anything with VB-Cable installed and its CABLE Input enabled.
///
/// The spec asks for these to be REPORTED AS SKIPPED, WITH THE REASON, when the cable is absent —
/// not to pass quietly. xunit 2 has no runtime skip, so the decision is made when the test is
/// discovered: the attribute sets Skip, and the runner lists the test as skipped with this text.
/// That is visible in every run, which is the property Requires.cs cares about; it is still a
/// departure from that file's "label, do not skip" rule, made because the spec demands it.</summary>
public sealed class VirtualCableFactAttribute : FactAttribute
{
    public VirtualCableFactAttribute()
    {
        if (VirtualCable.Find() is not null) return;
        Skip = VirtualCable.IsInstalledButInactive()
            ? "VB-Cable is installed but CABLE Input is disabled - enable it in Sound settings to run this test."
            : "VB-Cable is not installed on this machine - install it (Settings > AirPlay) to run this test.";
    }
}
