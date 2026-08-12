using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using ApoVolume.Core;

namespace ApoVolume.Tests;

/// <summary>Handle-counting tests measure a PROCESS-wide number, so they cannot run alongside any
/// other test that touches GDI+ or icons (AppIconTests does). This collection is serialised
/// against every other collection in the assembly.</summary>
[CollectionDefinition(GdiHandleCollection.Name, DisableParallelization = true)]
public class GdiHandleCollection
{
    public const string Name = "gdi-handles";
}

/// <summary>The tray icon is now drawn at runtime, once per distinct look, and the drawing produces
/// two kinds of OS handle: a GDI bitmap and a USER icon. This project has leaked both before —
/// v2.0.1 fixed an <c>Icon.FromHandle(bmp.GetHicon())</c> that never called <c>DestroyIcon</c>, and
/// a leak like that is invisible until a long session runs the process out of handles. A cache is
/// what keeps a held volume key from redrawing at all, so the two properties are tested together:
/// repeated requests must return the very same Icon instance, and the process handle counts must
/// not move while thousands of volume changes go through.
///
/// Counts come from <c>GetGuiResources</c>, the same number Task Manager's "GDI objects" and "USER
/// objects" columns show.</summary>
[Collection(GdiHandleCollection.Name)]
public class TrayIconRendererTests
{
    private const uint GrGdiObjects = 0;
    private const uint GrUserObjects = 1;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public TrayIconRendererTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private static (uint Gdi, uint User) Handles()
    {
        var process = Process.GetCurrentProcess().Handle;
        return (GetGuiResources(process, GrGdiObjects), GetGuiResources(process, GrUserObjects));
    }

    /// <summary>Every distinct look the tray can ask for at one size: the four arc levels plus
    /// muted, in both themes.</summary>
    private static void RequestEveryState(TrayIconRenderer renderer, int size)
    {
        foreach (bool light in new[] { false, true })
        {
            foreach (int percent in new[] { 0, 20, 50, 80, 100 })
            {
                renderer.Get(percent, muted: false, lightTaskbar: light, sizePx: size);
                renderer.Get(percent, muted: true, lightTaskbar: light, sizePx: size);
            }
        }
    }

    [Fact]
    public void RendersAnIconAtTheRequestedSize()
    {
        using var renderer = new TrayIconRenderer();
        foreach (int size in new[] { 16, 20, 24, 32 })
        {
            var icon = renderer.Get(50, muted: false, lightTaskbar: false, sizePx: size);
            _out.WriteLine($"asked {size}px, got {icon.Width}x{icon.Height}, handle={icon.Handle}");
            Assert.Equal(size, icon.Width);
            Assert.Equal(size, icon.Height);
            Assert.NotEqual(IntPtr.Zero, icon.Handle);
        }
    }

    /// <summary>The cache key is the LOOK, not the percent: holding volume-up walks through dozens
    /// of percentages that all draw the same three arcs, and not one of them may allocate. Reference
    /// equality is the assertion because it is the only thing that proves no drawing happened.</summary>
    [Fact]
    public void EveryPercentInAnArcBandSharesOneIcon()
    {
        using var renderer = new TrayIconRenderer();
        foreach (var band in new[] { new[] { 1, 10, 20, 33 }, new[] { 34, 50, 66 }, new[] { 67, 80, 100 } })
        {
            var first = renderer.Get(band[0], false, false, 16);
            foreach (int percent in band)
                Assert.Same(first, renderer.Get(percent, false, false, 16));
            _out.WriteLine($"{band[0]}..{band[^1]}% share icon {first.Handle}");
        }
    }

    /// <summary>Muted looks the same at every volume, so it must not occupy four cache entries —
    /// and, more importantly, must not redraw when the volume changes while muted.</summary>
    [Fact]
    public void MutedSharesOneIconAcrossEveryVolume()
    {
        using var renderer = new TrayIconRenderer();
        var first = renderer.Get(0, muted: true, lightTaskbar: false, sizePx: 16);
        foreach (int percent in new[] { 0, 1, 33, 34, 66, 67, 100 })
            Assert.Same(first, renderer.Get(percent, muted: true, lightTaskbar: false, sizePx: 16));
    }

    /// <summary>Each axis of the cache key genuinely selects a different icon — a key that dropped
    /// the theme or the size would silently serve a white glyph to a light taskbar, or a 16px icon
    /// to a 200%-DPI shell.</summary>
    [Fact]
    public void ArcLevelMuteThemeAndSizeEachSelectADifferentIcon()
    {
        using var renderer = new TrayIconRenderer();
        var baseline = renderer.Get(50, muted: false, lightTaskbar: false, sizePx: 16);

        Assert.NotSame(baseline, renderer.Get(100, muted: false, lightTaskbar: false, sizePx: 16));
        Assert.NotSame(baseline, renderer.Get(50, muted: true, lightTaskbar: false, sizePx: 16));
        Assert.NotSame(baseline, renderer.Get(50, muted: false, lightTaskbar: true, sizePx: 16));
        Assert.NotSame(baseline, renderer.Get(50, muted: false, lightTaskbar: false, sizePx: 32));

        // …and asking again for the original still gets the original, i.e. nothing was evicted.
        Assert.Same(baseline, renderer.Get(50, muted: false, lightTaskbar: false, sizePx: 16));
    }

    /// <summary>The regression guard for key repeat: once every look has been drawn once, thousands
    /// of further volume changes must allocate no OS handles at all.</summary>
    [Fact]
    public void ThousandsOfVolumeChangesAllocateNoHandles()
    {
        using var renderer = new TrayIconRenderer();
        RequestEveryState(renderer, 16);      // warm the cache and GDI+'s own one-time allocations
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var before = Handles();
        for (int i = 0; i < 2000; i++)
        {
            int percent = i % 101;
            renderer.Get(percent, muted: i % 7 == 0, lightTaskbar: i % 3 == 0, sizePx: 16);
        }
        var after = Handles();

        _out.WriteLine($"2000 volume changes: GDI {before.Gdi}->{after.Gdi}, USER {before.User}->{after.User}");
        Assert.Equal(before.Gdi, after.Gdi);
        Assert.Equal(before.User, after.User);
    }

    /// <summary>Every handle the renderer creates is released exactly once, at Dispose. Measured
    /// around the renderer's whole lifetime: whatever it took, it gives all of it back. A missing
    /// DestroyIcon (the v2.0.1 bug) leaves the USER count permanently higher.</summary>
    [Fact]
    public void DisposeReleasesEveryHandleItTook()
    {
        // One throwaway renderer first, so GDI+ start-up costs are not counted as a leak.
        using (var warmup = new TrayIconRenderer()) RequestEveryState(warmup, 16);
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var before = Handles();
        var renderer = new TrayIconRenderer();
        RequestEveryState(renderer, 16);
        RequestEveryState(renderer, 32);
        var peak = Handles();
        renderer.Dispose();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var after = Handles();

        _out.WriteLine($"GDI {before.Gdi} -> peak {peak.Gdi} -> {after.Gdi}");
        _out.WriteLine($"USER {before.User} -> peak {peak.User} -> {after.User}");
        Assert.True(peak.User > before.User, "no icons were created at all — the test proves nothing");
        Assert.Equal(before.Gdi, after.Gdi);
        Assert.Equal(before.User, after.User);
    }

    /// <summary>Disposing twice must not double-free the icon handles — Dispose is reachable from
    /// both the tray's own Dispose and a using block in a test.</summary>
    [Fact]
    public void DisposeIsIdempotent()
    {
        var renderer = new TrayIconRenderer();
        RequestEveryState(renderer, 16);
        renderer.Dispose();
        var after = Handles();
        renderer.Dispose();
        Assert.Equal(after, Handles());
    }

    /// <summary>The icons are destroyed at Dispose, so handing one out afterwards would hand out a
    /// dangling handle — the shell would draw garbage. Fail fast instead.</summary>
    [Fact]
    public void GetAfterDisposeThrows()
    {
        var renderer = new TrayIconRenderer();
        renderer.Get(50, false, false, 16);
        renderer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => renderer.Get(50, false, false, 16));
    }

    /// <summary>The renderer's states are the glyph's states: the icon the shell receives really
    /// carries the arc count <see cref="TrayGlyph.ArcCount"/> asked for. Probed on the icon's own
    /// bitmap (at the arc radii from the 32x32 design grid, scaled to 256px so a probe can't land
    /// on an antialiased edge) so the assertion covers the whole handle round trip rather than the
    /// bitmap the renderer happened to draw.</summary>
    [Theory]
    [InlineData(0, false, false, false)]
    [InlineData(20, true, false, false)]
    [InlineData(50, true, true, false)]
    [InlineData(90, true, true, true)]
    public void IconCarriesTheArcCountForThatVolume(int percent, bool first, bool second, bool third)
    {
        using var renderer = new TrayIconRenderer();
        var icon = renderer.Get(percent, muted: false, lightTaskbar: false, sizePx: 256);
        using var bmp = icon.ToBitmap();

        const float U = 256 / 32f;
        bool Opaque(float designX) => bmp.GetPixel((int)(designX * U), (int)(16 * U)).A > 128;

        _out.WriteLine($"{percent}%: inner={Opaque(21f)} middle={Opaque(24.5f)} outer={Opaque(28f)}");
        Assert.True(Opaque(6.5f), "speaker body missing from the icon");
        Assert.Equal(first, Opaque(21f));
        Assert.Equal(second, Opaque(24.5f));
        Assert.Equal(third, Opaque(28f));
    }
}
