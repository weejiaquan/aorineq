using AorinEQ.Core;
using Xunit.Abstractions;

namespace AorinEQ.Tests;

/// <summary>The real screens of this machine, read through the real Win32 enumeration (no mocks).
/// What matters is not the numbers — they are whatever is plugged in — but the PROPERTIES the
/// HUD's placement depends on: that every screen has an identity, that the identity is unique,
/// that exactly one screen is primary, and that a work area is a real rectangle inside its
/// screen. Break any of those and widgets land in the wrong place, or on top of each other.</summary>
public class DisplayMonitorsTests
{
    private readonly ITestOutputHelper _out;
    public DisplayMonitorsTests(ITestOutputHelper output) => _out = output;

    [Fact]
    [Trait(Requires.Key, Requires.Display)]
    public void Every_screen_has_a_unique_identity_and_a_usable_rectangle()
    {
        var monitors = DisplayMonitors.Enumerate();
        foreach (var m in monitors)
            _out.WriteLine($"{m.Name} id={m.DeviceId} bounds={m.Bounds} work={m.WorkArea} primary={m.IsPrimary}");

        Assert.NotEmpty(monitors);
        Assert.All(monitors, m =>
        {
            Assert.False(string.IsNullOrWhiteSpace(m.DeviceId));
            Assert.False(string.IsNullOrWhiteSpace(m.Name));
            Assert.True(m.Bounds.Width > 0 && m.Bounds.Height > 0);
            Assert.True(m.WorkArea.Width > 0 && m.WorkArea.Height > 0);
            // The work area is the screen minus the taskbar, so it can never be larger.
            Assert.True(m.Bounds.Contains(m.WorkArea), $"{m.Name}: work area escapes its screen");
        });
        Assert.Equal(monitors.Count, monitors.Select(m => m.DeviceId).Distinct().Count());
        Assert.Equal(1, monitors.Count(m => m.IsPrimary));
    }

    [Fact]
    [Trait(Requires.Key, Requires.Display)]
    public void The_identity_is_stable_across_two_reads()
    {
        // Placement re-resolves on every display change, so two consecutive enumerations of an
        // unchanged desktop have to agree — otherwise a widget would be "moved to the primary"
        // spuriously, which is the one thing the fallback notice must not cry wolf about.
        var first = DisplayMonitors.Enumerate();
        var second = DisplayMonitors.Enumerate();

        Assert.Equal(first.Select(m => m.DeviceId), second.Select(m => m.DeviceId));
    }

    [Fact]
    [Trait(Requires.Key, Requires.Display)]
    public void A_widget_placed_on_a_real_screen_lands_inside_that_screens_work_area()
    {
        // The end-to-end of the placement contract, against this machine's actual desktop.
        var monitors = DisplayMonitors.Enumerate();
        Assert.NotEmpty(monitors);

        foreach (var m in monitors)
        {
            var widget = HudWidget.Create(HudWidgetTypes.Spectrum) with { MonitorId = m.DeviceId, X = 20, Y = 20 };
            var placed = HudPlacement.Resolve(widget, monitors);
            _out.WriteLine($"{m.Name}: {placed.Bounds} movedToFallback={placed.MovedToFallback}");

            Assert.False(placed.MovedToFallback);
            Assert.Equal(m.DeviceId, placed.Monitor.DeviceId);
            Assert.True(m.WorkArea.Contains(placed.Bounds));
        }
    }
}
